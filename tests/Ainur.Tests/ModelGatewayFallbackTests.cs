using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;

namespace Ainur.Tests;

/// <summary>Deterministic provider for gateway failover tests: each call consumes one script entry, which may succeed or throw.</summary>
public sealed class ScriptedGatewayProvider(string id, params Func<ProviderRequest, Action<StreamDelta>?, ProviderResponse>[] script) : IModelProvider {
	public int Calls;
	public string Id => id;
	public Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) {
		var i = Calls++;
		if(i >= script.Length) throw new InvalidOperationException("script exhausted");
		return Task.FromResult(script[i](request, onDelta));
	}

	public static ProviderResponse Ok(string content = "ok") => new() {
		Content = content, RawRequest = "{}", RawResponse = "{}",
		Usage = new Usage { InputTokens = 5, OutputTokens = 2, Reported = true }, FinishReason = "stop",
	};
	public static ProviderException Auth() => new("auth failed", 401, retryable: false, mayHaveBilled: false);
	public static ProviderException Quota() => new("rate limited", 429, retryable: true, mayHaveBilled: false);
	public static ProviderException Upstream() => new("upstream down", 503, retryable: true, mayHaveBilled: false);
	public static ProviderException Billed() => new("already billed", null, retryable: false, mayHaveBilled: true);
	public static ProviderException BadRequest() => new("bad request", 400, retryable: false, mayHaveBilled: false);
}

public class ModelGatewayFallbackTests {
	static ModelInfo Model(string id, string billing, string? fallback = null) => new() {
		Id = id, Provider = "deepseek", UpstreamModel = id, Billing = billing, Enabled = true, FallbackModelId = fallback,
	};

	static (AinurRuntime rt, TempHome home, ModelInfo primary, ModelInfo fallback, string projectId) Setup(params Func<ProviderRequest, Action<StreamDelta>?, ProviderResponse>[] script) {
		var home = new TempHome();
		var rt = home.Runtime(new ScriptedGatewayProvider("deepseek", script), start: false);
		var primary = Model("prim", "subscription", "fall");
		var fallback = Model("fall", "api");
		rt.Db.Write(u => { rt.Store.UpsertModel(u, primary); rt.Store.UpsertModel(u, fallback); });
		var project = rt.CreateProject("t", "d", home.Workspace);
		return (rt, home, primary, fallback, project.Id);
	}

	static ModelCall Call(string projectId, ModelInfo model, Action<StreamDelta>? onDelta = null) => new() {
		ProjectId = projectId, Purpose = "test", Category = "test", Model = model, Messages = [ChatMessage.User("hi")], OnDelta = onDelta,
	};

	[Fact]
	public async Task FailoverFiresForEligibleNonBilledAuthFailure() {
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => throw ScriptedGatewayProvider.Auth(),
			(_, _) => ScriptedGatewayProvider.Ok("api answer"));
		using(home) {
			var result = await rt.Gateway.CallAsync(Call(projectId, primary), default);
			Assert.Equal("api answer", result.Response.Content);
			Assert.Equal("fall", result.Record.ModelId);

			var succeeded = rt.Store.ModelRequestsInState("succeeded");
			var failed = rt.Store.ModelRequestsInState("failed");
			Assert.Single(succeeded);
			Assert.Equal("fall", succeeded[0].ModelId);
			Assert.Single(failed);
			Assert.Equal("prim", failed[0].ModelId);

			// A failover journal event names primary, fallback and cause.
			var events = rt.Store.Events(projectId);
			var fo = events.First(e => e.Kind == "model.failover");
			Assert.Contains("prim", fo.Payload);
			Assert.Contains("fall", fo.Payload);
			Assert.Contains("auth", fo.Payload);
		}
	}

	[Fact]
	public async Task FailoverFiresForQuota429() {
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => throw ScriptedGatewayProvider.Quota(),
			(_, _) => ScriptedGatewayProvider.Ok("api answer"));
		using(home) {
			var result = await rt.Gateway.CallAsync(Call(projectId, primary), default);
			Assert.Equal("api answer", result.Response.Content);
			var fo = rt.Store.Events(projectId).First(e => e.Kind == "model.failover");
			Assert.Contains("quota", fo.Payload);
		}
	}

	[Fact]
	public async Task DoesNotFailoverWhenMayHaveBilled() {
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => throw ScriptedGatewayProvider.Billed());
		using(home) {
			await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(projectId, primary), default));
			// One attempt only; a conservative reservation is settled rather than released, no second attempt.
			Assert.Single(rt.Store.ModelRequestsInState("unknown"));
			Assert.Empty(rt.Store.ModelRequestsInState("succeeded"));
			Assert.DoesNotContain(rt.Store.Events(projectId), e => e.Kind == "model.failover");
		}
	}

	[Fact]
	public async Task DoesNotFailoverAfterDeltas() {
		var (rt, home, primary, _, projectId) = Setup(
			(req, onDelta) => { onDelta?.Invoke(new StreamDelta("content", "partial")); throw ScriptedGatewayProvider.Auth(); },
			(_, _) => ScriptedGatewayProvider.Ok("should never run"));
		using(home) {
			var deltas = new List<StreamDelta>();
			await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(projectId, primary, d => deltas.Add(d)), default));
			Assert.Contains(deltas, d => d.Text == "partial");
			Assert.Single(rt.Store.ModelRequestsInState("failed"));
			Assert.Empty(rt.Store.ModelRequestsInState("succeeded"));
			Assert.DoesNotContain(rt.Store.Events(projectId), e => e.Kind == "model.failover");
		}
	}

	[Fact]
	public async Task EachAttemptHasOwnRequestRowWithOwnQuote() {
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => throw ScriptedGatewayProvider.Upstream(),
			(_, _) => ScriptedGatewayProvider.Ok("api answer"));
		using(home) {
			await rt.Gateway.CallAsync(Call(projectId, primary), default);
			var all = rt.Store.ListModels();
			var rows = new[] { rt.Store.ModelRequestsInState("failed"), rt.Store.ModelRequestsInState("succeeded") }.SelectMany(x => x).ToList();
			Assert.Equal(2, rows.Count);
			Assert.NotEqual(rows[0].Id, rows[1].Id);

			var primaryRow = rows.Single(r => r.ModelId == "prim");
			var fallbackRow = rows.Single(r => r.ModelId == "fall");
			var primaryQuote = (JsonObject) JsonNode.Parse(primaryRow.Quote)!;
			var fallbackQuote = (JsonObject) JsonNode.Parse(fallbackRow.Quote)!;
			Assert.Equal("subscription", primaryQuote["billing"]!.GetValue<string>());
			Assert.Equal("api", fallbackQuote["billing"]!.GetValue<string>());
		}
	}

	[Fact]
	public async Task SubscriptionAttemptSettlesCashZeroAndApiAttemptSettlesUnknown() {
		// Fallback: primary subscription attempt fails (released), api attempt settles with cash_basis=unknown and no cash.
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => throw ScriptedGatewayProvider.Auth(),
			(_, _) => ScriptedGatewayProvider.Ok("api answer"));
		using(home) {
			await rt.Gateway.CallAsync(Call(projectId, primary), default);
			var costs = rt.Store.CostEvents(projectId);
			Assert.Single(costs); // only the api attempt settles; the subscription attempt was released
			Assert.Null(costs[0].CashNanos);
			Assert.Equal("unknown", costs[0].CashBasis);
			Assert.True(costs[0].EffectiveNanos > 0); // effective valuation is still recorded for unknown-price api use
		}
	}

	[Fact]
	public async Task SubscriptionSuccessSettlesCashZero() {
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => ScriptedGatewayProvider.Ok("sub answer"));
		using(home) {
			var result = await rt.Gateway.CallAsync(Call(projectId, primary), default);
			Assert.Equal("sub answer", result.Response.Content);
			var costs = rt.Store.CostEvents(projectId);
			Assert.Single(costs);
			Assert.Equal(0L, costs[0].CashNanos);
		}
	}

	[Fact]
	public async Task IneligibleFailureReleasesReservationNoDoubleCharge() {
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => throw ScriptedGatewayProvider.BadRequest());
		using(home) {
			await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(projectId, primary), default));
			// Bad request: not billed, not eligible → reservation released, no cost event, no failover.
			Assert.Single(rt.Store.ModelRequestsInState("failed"));
			Assert.Empty(rt.Store.CostEvents(projectId));
			Assert.DoesNotContain(rt.Store.Events(projectId), e => e.Kind == "model.failover");
		}
	}

	[Fact]
	public async Task GatewayRefusesDisabledPrimaryModel() {
		var (rt, home, primary, _, projectId) = Setup(
			(_, _) => ScriptedGatewayProvider.Ok("should never run"));
		using(home) {
			rt.Db.Write(u => { primary.Enabled = false; rt.Store.UpsertModel(u, primary); });
			await Assert.ThrowsAsync<DomainException>(() => rt.Gateway.CallAsync(Call(projectId, primary), default));
			Assert.Empty(rt.Store.ModelRequestsInState("succeeded"));
			Assert.Empty(rt.Store.ModelRequestsInState("failed"));
			Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
		}
	}

	[Fact]
	public async Task DisabledFallbackIsNeverSelected() {
		var (rt, home, primary, fallback, projectId) = Setup(
			(_, _) => throw ScriptedGatewayProvider.Auth(),
			(_, _) => ScriptedGatewayProvider.Ok("must not run"));
		using(home) {
			rt.Db.Write(u => { fallback.Enabled = false; rt.Store.UpsertModel(u, fallback); });
			await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(projectId, primary), default));
			// The disabled fallback is never called: only the primary attempt exists, and no fallback was attempted.
			Assert.Single(rt.Store.ModelRequestsInState("failed"));
			Assert.Empty(rt.Store.ModelRequestsInState("succeeded"));
			Assert.DoesNotContain(rt.Store.Events(projectId), e => e.Kind == "model.failover");
		}
	}
}
