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
	public async Task<ModelCallResult> CallAsync(ModelCall call, CancellationToken ct) {
		var estimate = call.EstimatedInputTokens
			?? call.Messages.Sum(Context.ContextBuilder.MessageTokens)
			+ call.Tools.Sum(t => Tokens.Estimate(t.InputSchema.ToJsonString()) + Tokens.Estimate(t.Description));
		var chain = BuildChain(store.GetModel(call.Model.Id) ?? call.Model);
		for(var i = 0; i < chain.Count; i++) {
			var model = store.GetModel(chain[i].Id) ?? chain[i];
			if(!model.Enabled)
				throw new DomainException($"Model '{model.Id}' is disabled and cannot be called.");
			var emitted = false;
			Action<StreamDelta> onDelta = d => { emitted = true; call.OnDelta?.Invoke(d); };
			var requestId = Ids.New("req");
			try {
				return await AttemptAsync(call, model, requestId, estimate, onDelta, () => emitted, ct);
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
	List<ModelInfo> BuildChain(ModelInfo primary) {
		var chain = new List<ModelInfo> { primary };
		if(!string.IsNullOrEmpty(primary.FallbackModelId)) {
			var fallback = store.GetModel(primary.FallbackModelId!);
			if(fallback is { Enabled: true } && fallback.Id != primary.Id && providers.Has(fallback.Provider))
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

	async Task<ModelCallResult> AttemptAsync(ModelCall call, ModelInfo model, string requestId, long estimate, Action<StreamDelta> onDelta, Func<bool> emitted, CancellationToken ct) {
		var provider = providers.Get(model.Provider);
		var quote = Pricing.Quote(model, estimate, call.MaxOutputTokens);
		var record = new ModelRequestRecord {
			Id = requestId, ProjectId = call.ProjectId, SessionId = call.SessionId, AgentId = call.AgentId, ObjectiveId = call.ObjectiveId,
			Purpose = call.Purpose, ModelId = model.Id, Provider = model.Provider, UpstreamModel = model.UpstreamModel,
			State = "dispatched", Quote = JsonUtil.Serialize(quote), ContextRevision = call.ContextRevision, StartedAt = Clock.Now,
		};
		var request = new ProviderRequest {
			Model = model, Messages = call.Messages, Tools = call.Tools, MaxOutputTokens = call.MaxOutputTokens, ReasoningEffort = call.ReasoningEffort, EnableWebSearch = call.EnableWebSearch,
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
			response = await provider.CompleteAsync(request, onDelta, ct);
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
