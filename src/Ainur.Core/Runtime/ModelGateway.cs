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
	public int? EstimatedInputTokens { get; init; }
	public int? ContextRevision { get; init; }
	public Action<StreamDelta>? OnDelta { get; init; }
	public Dictionary<string, string>? ToolBindings { get; init; }
}

public sealed record ModelCallResult(ProviderResponse Response, ModelRequestRecord Record, CostEvent? Cost);

/// <summary>
/// The single path for model requests: quote, atomically reserve budget, record intent, dispatch, then settle
/// usage into exactly one cost event. Failures release or conservatively retain reservations.
/// </summary>
public sealed class ModelGateway(Store store, Ledger ledger, ArtifactStore artifacts, ProviderRegistry providers, QuotaManager quotas) {
	public async Task<ModelCallResult> CallAsync(ModelCall call, CancellationToken ct) {
		var provider = providers.Get(call.Model.Provider);
		var estimate = call.EstimatedInputTokens ?? call.Messages.Sum(Context.ContextBuilder.MessageTokens) + call.Tools.Sum(t => Tokens.Estimate(t.InputSchema.ToJsonString()) + Tokens.Estimate(t.Description));
		var quote = Pricing.Quote(call.Model, estimate, call.MaxOutputTokens);
		var record = new ModelRequestRecord {
			Id = Ids.New("req"), ProjectId = call.ProjectId, SessionId = call.SessionId, AgentId = call.AgentId, ObjectiveId = call.ObjectiveId,
			Purpose = call.Purpose, ModelId = call.Model.Id, Provider = call.Model.Provider, UpstreamModel = call.Model.UpstreamModel,
			State = "dispatched", Quote = JsonUtil.Serialize(quote), ContextRevision = call.ContextRevision, StartedAt = Clock.Now,
		};
		var request = new ProviderRequest {
			Model = call.Model, Messages = call.Messages, Tools = call.Tools, MaxOutputTokens = call.MaxOutputTokens, ReasoningEffort = call.ReasoningEffort,
		};
		// Intent and reservation commit before dispatch so a crash leaves evidence of a possibly-billed request.
		store.Db.Write(u => {
			store.InsertModelRequest(u, record);
			if(call.ToolBindings is not null)
				u.Execute("UPDATE model_requests SET tool_bindings=@b WHERE id=@Id", new { b = JsonUtil.Serialize(call.ToolBindings), record.Id });
			// Quota holds are taken in every applicable window together with the dollar reservation.
			var holds = quotas.Reserve(u, call.Model, record.Id, estimate, call.MaxOutputTokens);
			if(holds.Count > 0) {
				quote.ScarcityFloor = Math.Max(quote.ScarcityFloor, holds.Max(h => h.Scarcity));
				quote.ReservedEffectiveNanos = Math.Max(quote.ReservedEffectiveNanos,
					holds.Max(h => Money.FromDollars((decimal) h.Units / Math.Max(1, h.Window.Capacity) * h.Window.WindowValueDollars * h.Scarcity)));
				record.Quote = JsonUtil.Serialize(quote);
				u.Execute("UPDATE model_requests SET quote=@Quote WHERE id=@Id", record);
			}
			ledger.Reserve(u, call.ProjectId, record.Id, quote);
			u.Journal("model.dispatched", call.ProjectId, "model_request", record.Id, call.AgentId, new { call.Purpose, model = call.Model.Id, estimate, reserved_effective = quote.ReservedEffectiveNanos });
		});

		ProviderResponse response;
		try {
			response = await provider.CompleteAsync(request, call.OnDelta, ct);
		} catch(Exception e) {
			var mayHaveBilled = e is ProviderException { MayHaveBilled: true } || e is OperationCanceledException;
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
				call.Purpose, model = call.Model.Id, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, response.FinishReason,
				tool_calls = response.ToolCalls.Count, effective_nanos = cost?.EffectiveNanos, cash_nanos = cost?.CashNanos,
			});
		});
		return new(response, record, cost);
	}
}
