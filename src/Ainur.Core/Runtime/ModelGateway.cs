using Ainur.Core.Accounting;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;

namespace Ainur.Core.Runtime;

public sealed class ModelCall {
	public required string ProjectId { get; init; }
	public string? AgentId { get; init; }
	public string? SessionId { get; init; }
	public string? ObjectiveId { get; init; }
	public string? SponsorAgentId { get; init; }
	public required string Purpose { get; init; }
	public required string Category { get; init; }
	public required ModelInfo Model { get; init; }
	public required List<ChatMessage> Messages { get; init; }
	public List<ToolSpec> Tools { get; init; } = [];
	public int MaxOutputTokens { get; init; } = 8192;
	public string? ReasoningEffort { get; init; }
	public bool EnableWebSearch { get; init; }
	public int? EstimatedInputTokens { get; init; }
	public int? ContextRevision { get; init; }
	public Action<StreamDelta>? OnDelta { get; init; }
	public Action<Task>? OnUnsettled { get; init; }
	public Dictionary<string, string>? ToolBindings { get; init; }
}

public sealed record ModelCallResult(ProviderResponse Response, ModelRequestRecord Record, CostEvent? Cost);

/// <summary>
/// The single path for model requests: quote, atomically reserve budget, record intent, dispatch, then settle
/// usage into exactly one cost event. Failures release or conservatively retain reservations.
/// A model with a linked <see cref="ModelInfo.FallbackModelId"/> is retried on the fallback only when the attempt
/// failed without billing (<see cref="ProviderException.MayHaveBilled"/> false), the failure class is eligible, and
/// no delta reached the caller. Every attempt is its own model_request row with its own quote and settlement.
/// </summary>
public sealed class ModelGateway(Store store, Ledger ledger, ArtifactStore artifacts, ProviderRegistry providers, QuotaManager quotas) {
	// Retain maintenance admission until abandoned transport operations actually settle.
	sealed class DeferredAdmission(IDisposable? lease) : IDisposable {
		readonly HashSet<Task> tasks = [];
		bool disposed, released;
		public void Track(Task task) {
			lock(tasks) tasks.Add(task);
			_ = task.ContinueWith(t => {
				_ = t.Exception;
				lock(tasks) { tasks.Remove(t); ReleaseIfSettled(); }
			}, TaskScheduler.Default);
		}
		void ReleaseIfSettled() {
			if(disposed && !released && tasks.All(t => t.IsCompleted)) { released = true; lease?.Dispose(); }
		}
		public void Dispose() { lock(tasks) { disposed = true; ReleaseIfSettled(); } }
	}
	public Func<ModelCall, IDisposable>? Admission { get; init; }
	public TimeSpan FullCallTimeout { get; init; } = TimeSpan.FromMinutes(10);
	public TimeSpan StreamIdleTimeout { get; init; } = TimeSpan.FromSeconds(90);
	public async Task<ModelCallResult> CallAsync(ModelCall call, CancellationToken ct) {
		using var admission = new DeferredAdmission(Admission?.Invoke(call));
		// This is the final dispatch boundary for turns, helpers, and fallbacks. Reject persisted legacy
		// below-floor efforts before quoting, reservations, journals, or provider side effects; explicit
		// set_agent_model with medium/high/max is the supported repair path for such an agent.
		var effort = AinurRuntime.ResolveReasoningEffort(call.ReasoningEffort);
		var images = call.Messages.SelectMany(m => m.Images).ToList();
		// Pin policy before fallback selection and any quote/reservation/provider side effect.
		var primary = store.GetModel(call.Model.Id) ?? call.Model;
		var linked = string.IsNullOrEmpty(primary.FallbackModelId) ? null : store.GetModel(primary.FallbackModelId!);
		var usesOpenAi = string.Equals(primary.Provider, "openai", StringComparison.OrdinalIgnoreCase) ||
			string.Equals(linked?.Provider, "openai", StringComparison.OrdinalIgnoreCase);
		var route = usesOpenAi && providers.Get("openai") is OpenAiResponsesProvider openai
			? openai.SnapshotRoutePolicy() : (OpenAiRoutePolicy?) null;
		if(route == OpenAiRoutePolicy.Unknown)
			throw new ProviderException("OpenAI route is unknown; no model request dispatched.");
		if(route == OpenAiRoutePolicy.Subscription && string.Equals(primary.Provider, "openai", StringComparison.OrdinalIgnoreCase) &&
			string.Equals(primary.Billing, "api", StringComparison.OrdinalIgnoreCase))
			throw new ProviderException("OpenAI subscription-only route excludes API-billed models before quote.");
		// Preflight every fallback before reservation or network dispatch. A non-vision fallback may
		// never silently strip pixels if the primary provider rejects/does not bill a request.
		var chain = BuildChain(primary, route);
		if(images.Count > 0 && chain.Any(m => m.Provider != "openai" || providers.Get(m.Provider) is not OpenAiResponsesProvider))
			throw new ProviderException("Browser image input requires an OpenAI Responses vision route for primary and fallback; no screenshot was sent or silently dropped.");
		if(images.Count > 0) {
			if(string.IsNullOrEmpty(call.SessionId)) throw new ProviderException("Browser image input requires an authenticated persisted session.");
			BrowserImageInput.EnsureAuthorized(store, call.SessionId, call.Messages);
		}
		var imageData = images.Count == 0 ? new Dictionary<string, byte[]>() : BrowserImageInput.Hydrate(artifacts, images);
		var estimate = call.EstimatedInputTokens
			?? call.Messages.Sum(Context.ContextBuilder.MessageTokens)
			+ call.Tools.Sum(t => Tokens.Estimate(t.InputSchema.ToJsonString()) + Tokens.Estimate(t.Description));
		for(var i = 0; i < chain.Count; i++) {
			if(route is not null && providers.Get("openai") is OpenAiResponsesProvider current && current.SnapshotRoutePolicy() != route)
				throw new ProviderException("OpenAI route changed before model quote; no fallback dispatched.");
			var model = store.GetModel(chain[i].Id) ?? chain[i];
			if(route == OpenAiRoutePolicy.Subscription && string.Equals(model.Provider, "openai", StringComparison.OrdinalIgnoreCase) &&
				string.Equals(model.Billing, "api", StringComparison.OrdinalIgnoreCase))
				throw new ProviderException("OpenAI subscription-only route excludes API-billed models before quote.");
			if(!model.Enabled)
				throw new DomainException($"Model '{model.Id}' is disabled and cannot be called.");
			var emitted = false;
			Action<StreamDelta> onDelta = d => { emitted = true; call.OnDelta?.Invoke(d); };
			var requestId = Ids.New("req");
			try {
				return await AttemptAsync(call, model, requestId, estimate, effort, imageData, onDelta, () => emitted, ct, route, admission);
			} catch(Exception e) {
				var cause = FailoverClass(e);
				if(i + 1 < chain.Count && !emitted && cause is not null) {
					var fallback = chain[i + 1];
					store.Db.Write(u => u.Journal("model.failover", call.ProjectId, "model_request", requestId, call.AgentId, new {
						call.Purpose, primary = model.Id, fallback = fallback.Id, cause, error = TextUtil.Truncate(e.Message, 500),
					}));
					continue;
				}
				throw;
			}
		}
		throw new InvalidOperationException("empty model chain");
	}

	/// <summary>Resolves the primary model plus its enabled fallback (if any and different).</summary>
	List<ModelInfo> BuildChain(ModelInfo primary, OpenAiRoutePolicy? route) {
		var chain = new List<ModelInfo> { primary };
		if(!string.IsNullOrEmpty(primary.FallbackModelId)) {
			var fallback = store.GetModel(primary.FallbackModelId!);
			if(fallback is { Enabled: true } && fallback.Id != primary.Id && providers.Has(fallback.Provider) &&
				!(string.Equals(fallback.Provider, "openai", StringComparison.OrdinalIgnoreCase) &&
					(route == OpenAiRoutePolicy.Subscription || route == OpenAiRoutePolicy.Unknown) &&
					string.Equals(fallback.Billing, "api", StringComparison.OrdinalIgnoreCase)))
				chain.Add(fallback);
		}
		return chain;
	}

	/// <summary>
	/// Classifies a provider failure for fallback eligibility. Returns null when the failure is ineligible:
	/// it may have billed, it is not a <see cref="ProviderException"/>, or its class is not one that justifies
	/// re-routing (auth, quota/402/429, upstream 5xx, timeout/unavailable, model-unavailable).
	/// </summary>
	static string? FailoverClass(Exception e) {
		if(e is not ProviderException pe || pe.MayHaveBilled) return null;
		if(pe.Status is { } s) {
			if(s is 401 or 403) return "auth";
			if(s is 402 or 429) return "quota";
			if(s >= 500) return "upstream";
			if(s == 404) return "model_unavailable"; // 422 is request validation, not model availability.
			return null;
		}
		// No HTTP status: a transport/connect failure is retryable; configuration errors are not.
		return pe.Retryable ? "unavailable" : null;
	}

	async Task<ModelCallResult> AttemptAsync(ModelCall call, ModelInfo model, string requestId, long estimate, string effort, IReadOnlyDictionary<string, byte[]> imageData, Action<StreamDelta> onDelta, Func<bool> emitted, CancellationToken ct, OpenAiRoutePolicy? route, DeferredAdmission admission) {
		using var attemptAdmission = Admission?.Invoke(call);
		var provider = providers.Get(model.Provider);
		var quote = Pricing.Quote(model, estimate, call.MaxOutputTokens);
		var record = new ModelRequestRecord {
			Id = requestId, ProjectId = call.ProjectId, SessionId = call.SessionId, AgentId = call.AgentId, ObjectiveId = call.ObjectiveId,
			Purpose = call.Purpose, ModelId = model.Id, Provider = model.Provider, UpstreamModel = model.UpstreamModel,
			State = "dispatched", Quote = JsonUtil.Serialize(quote), ContextRevision = call.ContextRevision, StartedAt = Clock.Now,
		};
		var request = new ProviderRequest {
			FullCallTimeout = FullCallTimeout, StreamIdleTimeout = StreamIdleTimeout, OnUnsettled = task => { admission.Track(task); call.OnUnsettled?.Invoke(task); },
			Model = model, Messages = call.Messages, Tools = call.Tools, MaxOutputTokens = call.MaxOutputTokens, ReasoningEffort = effort, EnableWebSearch = call.EnableWebSearch,
			ImageData = imageData, OpenAiRoutePolicy = route,
		};
		// Intent and reservation commit before dispatch so a crash leaves evidence of a possibly-billed request.
		store.Db.Write(u => {
			store.InsertModelRequest(u, record);
			if(call.ToolBindings is not null)
				u.Execute("UPDATE model_requests SET tool_bindings=@b WHERE id=@Id", new { b = JsonUtil.Serialize(call.ToolBindings), record.Id });
			// Quota holds are taken in every applicable window together with the dollar reservation.
			var holds = quotas.Reserve(u, model, record.Id, estimate, call.MaxOutputTokens);
			if(holds.Count > 0) {
				quote.ScarcityFloor = Math.Max(quote.ScarcityFloor, holds.Max(h => h.Scarcity));
				quote.ReservedEffectiveNanos = Math.Max(quote.ReservedEffectiveNanos,
					holds.Max(h => Money.FromDollars((decimal) h.Units / Math.Max(1, h.Window.Capacity) * h.Window.WindowValueDollars * h.Scarcity)));
				record.Quote = JsonUtil.Serialize(quote);
				u.Execute("UPDATE model_requests SET quote=@Quote WHERE id=@Id", record);
			}
			ledger.Reserve(u, call.ProjectId, record.Id, quote);
			u.Journal("model.dispatched", call.ProjectId, "model_request", record.Id, call.AgentId, new { call.Purpose, model = model.Id, estimate, reserved_effective = quote.ReservedEffectiveNanos });
		});

		ProviderResponse response;
		try {
			using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
			deadline.CancelAfter(FullCallTimeout);
			try { response = await BoundedProviderIO.Await(provider.CompleteAsync(request, d => { if(!deadline.IsCancellationRequested) onDelta(d); }, deadline.Token), deadline.Token, request); }
			catch(OperationCanceledException) when(deadline.IsCancellationRequested && !ct.IsCancellationRequested) {
				throw new ProviderException("Model full-call deadline exceeded", retryable: false, mayHaveBilled: true);
			}
			// A response that won the cancellation race still settles observed usage.
			// SessionHost fences transcript/tool admission against its canceled epoch.
		} catch(Exception e) {
			// Observed output overrides a provider's unbilled claim, even without a caller callback.
			var mayHaveBilled = emitted() || e is ProviderException { MayHaveBilled: true } || e is OperationCanceledException;
			store.Db.Write(u => {
				record.State = mayHaveBilled ? "unknown" : "failed";
				record.Error = e.Message;
				record.FinishedAt = Clock.Now;
				store.UpdateModelRequest(u, record);
				if(mayHaveBilled) {
					// Usage is uncertain: charge a conservative estimate (input only) rather than zero or the full reservation.
					var uncertain = new Usage { InputTokens = estimate, Reported = false };
					var charge = Pricing.Settle(quote, uncertain, quotas.Commit(u, record.Id, uncertain));
					ledger.Settle(u, record, charge with { CashBasis = charge.CashBasis == "unknown" ? "unknown" : "estimated" }, call.Category, call.SponsorAgentId);
				} else {
					ledger.Release(u, record.Id);
					quotas.Release(u, record.Id);
				}
				u.Journal("model.failed", call.ProjectId, "model_request", record.Id, call.AgentId, new { error = TextUtil.Truncate(e.Message, 500), uncertain = mayHaveBilled });
			});
			throw;
		}

		var requestArtifact = artifacts.Put(response.RawRequest);
		var responseArtifact = artifacts.Put(response.RawResponse);
		CostEvent? cost = null;
		store.Db.Write(u => {
			record.State = "succeeded";
			record.Usage = JsonUtil.Serialize(response.Usage);
			record.ResponseArtifact = responseArtifact;
			record.FinishedAt = Clock.Now;
			u.Execute("UPDATE model_requests SET request_artifact=@requestArtifact WHERE id=@Id", new { requestArtifact, record.Id });
			store.UpdateModelRequest(u, record);
			var usage = response.Usage.Reported ? response.Usage : new Usage { InputTokens = estimate, OutputTokens = Tokens.Estimate(response.Content) + Tokens.Estimate(response.Reasoning), Reported = false };
			cost = ledger.Settle(u, record, Pricing.Settle(quote, usage, quotas.Commit(u, record.Id, usage)), call.Category, call.SponsorAgentId);
			u.Journal("model.completed", call.ProjectId, "model_request", record.Id, call.AgentId, new {
				call.Purpose, model = model.Id, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, response.FinishReason,
				tool_calls = response.ToolCalls.Count, effective_nanos = cost?.EffectiveNanos, cash_nanos = cost?.CashNanos,
			});
		});
		return new(response, record, cost);
	}
}
