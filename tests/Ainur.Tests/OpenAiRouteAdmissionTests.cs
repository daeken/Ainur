using System.Net;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Accounting;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;

namespace Ainur.Tests;

public class OpenAiRouteAdmissionTests {
	static ProviderRequest Request(string billing = "subscription") => new() {
		Model = new ModelInfo { Id = "route-test", Provider = "openai", UpstreamModel = "gpt-6-astra", Billing = billing, Enabled = true },
		Messages = [ChatMessage.User("offline test")], ReasoningEffort = "max",
	};
	static string Auth(TempHome home) {
		var path = Path.Combine(home.Path, "fake-auth.json");
		File.WriteAllText(path, """{"tokens":{"access_token":"synthetic-test-token","account_id":"synthetic-account"}}""");
		return path;
	}
	static HttpResponseMessage Success() => ScriptedHandler.Sse("data: {\"type\":\"response.completed\",\"response\":{}}\n\n");

	[Theory]
	[InlineData("auto")]
	[InlineData("subscription")]
	public async Task MissingSubscriptionCredentialNeverResolvesApiKeyOrSendsHttp(string route) {
		var handler = new ScriptedHandler();
		handler.Add(_ => true, _ => Success());
		var keyReads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { keyReads++; return "test-key"; }, codexAuthPath: () => null, routeOverride: route);
		var error = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Request(), null, default));
		Assert.Contains("no usable ChatGPT subscription credential", error.Message);
		Assert.Equal(0, keyReads);
		Assert.Empty(handler.Seen);
	}

	[Fact]
	public async Task ApiOverrideOnSubscriptionRejectsBeforeAnyCredentialOrHttp() {
		var handler = new ScriptedHandler(); handler.Add(_ => true, _ => Success());
		var reads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { reads++; return "test-key"; }, codexAuthPath: () => { reads++; return null; }, routeOverride: "api");
		var error = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Request(), null, default));
		Assert.Contains("conflicts", error.Message);
		Assert.False(error.Retryable); Assert.False(error.MayHaveBilled);
		Assert.Equal(0, reads); Assert.Empty(handler.Seen);
	}

	[Theory]
	[InlineData(401)] [InlineData(403)] [InlineData(402)] [InlineData(429)] [InlineData(503)] [InlineData(0)]
	public async Task SubscriptionFailurePreservesOriginalCauseAndNeverReadsApiCredential(int status) {
		using var home = new TempHome();
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == OpenAiResponsesProvider.SubscriptionEndpoint,
			_ => status == 0 ? throw new HttpRequestException("synthetic subscription DNS failure") : ScriptedHandler.Json("{\"error\":\"original subscription failure\"}", (HttpStatusCode) status));
		handler.Add(_ => true, _ => Success());
		var keyReads = 0;
		var path = Auth(home);
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { keyReads++; return "test-key"; }, codexAuthPath: () => path, routeOverride: "auto");
		var error = await Assert.ThrowsAnyAsync<ProviderException>(() => provider.CompleteAsync(Request(), null, default));
		Assert.Contains(status == 0 ? "synthetic subscription DNS failure" : "original subscription failure", error.Message);
		Assert.Equal(status == 0 ? null : (int?) status, error.Status);
		Assert.Equal(0, keyReads);
		Assert.Equal(OpenAiResponsesProvider.SubscriptionEndpoint, Assert.Single(handler.Seen).Uri);
	}

	[Theory]
	[InlineData("auto")] [InlineData("api")]
	public async Task ApiBilledRowUsesApiTransportOnlyUnderNonstrictRoute(string route) {
		var handler = new ScriptedHandler(); handler.Add(_ => true, _ => Success());
		var subscriptionReads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "test-key", codexAuthPath: () => { subscriptionReads++; return null; }, routeOverride: route);
		await provider.CompleteAsync(Request("api"), null, default);
		Assert.Equal(OpenAiResponsesProvider.ApiEndpoint, Assert.Single(handler.Seen).Uri);
		Assert.Equal(0, subscriptionReads);
	}

	[Theory]
	[InlineData("subscription")] [InlineData("unrecognized")]
	public async Task StrictOrUnknownRouteRejectsDirectApiBeforeCredentialOrHttp(string route) {
		var handler = new ScriptedHandler(); handler.Add(_ => true, _ => Success());
		var reads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { reads++; return "test-key"; }, codexAuthPath: () => { reads++; return null; }, routeOverride: route);
		await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Request("api"), null, default));
		Assert.Equal(0, reads); Assert.Empty(handler.Seen);
	}

	[Fact]
	public async Task ChangedRouteBetweenGatewaySelectionAndProviderDispatchFailsClosed() {
		using var home = new TempHome(); var path = Auth(home);
		var route = "auto";
		var handler = new ScriptedHandler(); handler.Add(_ => true, _ => Success());
		var keyReads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { keyReads++; return "test-key"; }, codexAuthPath: () => { route = "subscription"; return path; }, routeProvider: () => route);
		using var rt = home.Runtime(provider, start: false);
		var project = rt.CreateProject("offline", "", home.Workspace, managerModelId: "gpt-6-astra");
		// Route mutates while obtaining credentials, after the first dispatch guard.
		// The gateway must still refuse an API fallback before quote/admission.
		await Assert.ThrowsAnyAsync<ProviderException>(() => rt.Gateway.CallAsync(new ModelCall {
			ProjectId = project.Id, Model = rt.Store.GetModel("gpt-6-astra")!, Messages = [ChatMessage.User("offline")], Purpose = "test", Category = "direct",
		}, default));
		Assert.Equal(0, keyReads);
		Assert.Empty(handler.Seen);
		Assert.Equal("gpt-6-astra", Assert.Single(rt.Store.ModelRequestsInState("failed")).ModelId);
		Assert.DoesNotContain(rt.Store.ModelRequestsInState("failed"), request => request.ModelId == "gpt-6-astra-api");
	}

	[Fact]
	public async Task StrictRouteOmitsEligibleApiFallbackBeforeQuoteOrJournal() {
		using var home = new TempHome(); var path = Auth(home);
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == OpenAiResponsesProvider.SubscriptionEndpoint,
			_ => ScriptedHandler.Json("{\"error\":\"original subscription quota\"}", HttpStatusCode.TooManyRequests));
		handler.Add(_ => true, _ => Success());
		var keyReads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { keyReads++; return "test-key"; }, codexAuthPath: () => path, routeOverride: "subscription");
		using var rt = home.Runtime(provider, start: false);
		var project = rt.CreateProject("offline", "", home.Workspace, managerModelId: "gpt-6-astra", noEffectiveLimit: true, cashCeilingDollars: 50);
		await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(new ModelCall {
			ProjectId = project.Id, Model = rt.Store.GetModel("gpt-6-astra")!, Messages = [ChatMessage.User("offline")], Purpose = "test", Category = "direct", ReasoningEffort = "max",
		}, default));
		Assert.Equal(OpenAiResponsesProvider.SubscriptionEndpoint, Assert.Single(handler.Seen).Uri);
		Assert.Equal(0, keyReads);
		var failed = Assert.Single(rt.Store.ModelRequestsInState("failed"));
		Assert.Equal("gpt-6-astra", failed.ModelId);
		Assert.Equal("subscription", JsonNode.Parse(failed.Quote)!["billing"]!.GetValue<string>());
		Assert.Empty(rt.Store.ModelRequestsInState("succeeded"));
		Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
		Assert.DoesNotContain(rt.Store.Events(project.Id), e => e.Kind == "model.failover");
		Assert.Equal(0, rt.Ledger.Summary(project.Id).ReservedCashUnknownCount);
	}

	[Fact]
	public async Task GatewayRejectsDirectStrictApiBeforeQuoteOrCredential() {
		using var home = new TempHome();
		var handler = new ScriptedHandler(); handler.Add(_ => true, _ => Success());
		var reads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { reads++; return "test-key"; }, codexAuthPath: () => null, routeOverride: "subscription");
		using var rt = home.Runtime(provider, start: false);
		var project = rt.CreateProject("offline", "", home.Workspace, managerModelId: "gpt-6-astra");
		await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(new ModelCall {
			ProjectId = project.Id, Model = rt.Store.GetModel("gpt-6-astra-api")!, Messages = [ChatMessage.User("offline")], Purpose = "test", Category = "direct",
		}, default));
		Assert.Equal(0, reads); Assert.Empty(handler.Seen);
		Assert.Empty(rt.Store.ModelRequestsInState("dispatched")); Assert.Empty(rt.Store.ModelRequestsInState("failed"));
	}

	[Fact]
	public async Task GatewayRequotesApiFallbackAndCashCapBlocksBeforeApiHttpOrReservation() {
		using var home = new TempHome(); var path = Auth(home);
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == OpenAiResponsesProvider.SubscriptionEndpoint, _ => ScriptedHandler.Json("{\"error\":\"original quota\"}", HttpStatusCode.TooManyRequests));
		handler.Add(_ => true, _ => Success());
		var keyReads = 0;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => { keyReads++; return "test-key"; }, codexAuthPath: () => path, routeOverride: "auto");
		using var rt = home.Runtime(provider, start: false);
		var project = rt.CreateProject("offline", "", home.Workspace, managerModelId: "gpt-6-astra", noEffectiveLimit: true, cashCeilingDollars: 50);
		var primary = rt.Store.GetModel("gpt-6-astra")!;
		var error = await Assert.ThrowsAsync<BudgetExhaustedException>(() => rt.Gateway.CallAsync(new ModelCall {
			ProjectId = project.Id, Model = primary, Messages = [ChatMessage.User("offline")], Purpose = "test", Category = "direct", ReasoningEffort = "max",
		}, default));
		Assert.Contains("cash price is unknown", error.Message);
		Assert.Equal(OpenAiResponsesProvider.SubscriptionEndpoint, Assert.Single(handler.Seen).Uri);
		Assert.Equal(0, keyReads);
		var failed = Assert.Single(rt.Store.ModelRequestsInState("failed"));
		Assert.Contains("original quota", failed.Error);
		Assert.Equal("subscription", JsonNode.Parse(failed.Quote)!["billing"]!.GetValue<string>());
		Assert.Empty(rt.Store.ModelRequestsInState("dispatched")); Assert.Empty(rt.Store.ModelRequestsInState("succeeded"));
		Assert.Empty(rt.Store.CostEvents(project.Id));
		var summary = rt.Ledger.Summary(project.Id);
		Assert.Equal(0, summary.ReservedCashNanos); Assert.Equal(0, summary.ReservedEffectiveNanos); Assert.Equal(0, summary.ReservedCashUnknownCount);
		var failover = Assert.Single(rt.Store.Events(project.Id), e => e.Kind == "model.failover");
		Assert.Equal(failed.Id, failover.EntityId);
		Assert.Contains("gpt-6-astra-api", failover.Payload);
	}
}
