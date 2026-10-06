using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Ainur.Core;
using Ainur.Core.Accounting;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Dapper;

namespace Ainur.Tests;

public class BedrockProviderTests {
	const string Completed = "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":120,\"output_tokens\":12,\"input_tokens_details\":{\"cached_tokens\":20}}}}\n\n";
	static ModelInfo Model(string provider = "bedrock", string billing = "api") => new() {
		Id = "bedrock-gpt-6.1-sol", Provider = provider, Billing = billing, Enabled = true,
		UpstreamModel = provider == "bedrock" ? "us.openai.gpt-6.1-sol" : "openai.gpt-6.1-sol", MaxOutputTokens = 4096,
	};
	static ProviderRequest Request(string provider = "bedrock", string billing = "api") => new() {
		Model = Model(provider, billing), Messages = [ChatMessage.System("System instruction"), ChatMessage.User("Hello")],
		MaxOutputTokens = 512, ReasoningEffort = "high",
	};
	sealed class RotatingCredentials : AWSCredentials {
		public int Reads;
		public override ImmutableCredentials GetCredentials() => new("AKIDEXAMPLE", "example-secret", "session-token-" + ++Reads);
		public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(GetCredentials());
	}

	[Theory]
	[InlineData(false, "bedrock-runtime.us-west-2.amazonaws.com", "bedrock")]
	[InlineData(true, "bedrock-mantle.us-west-2.api.aws", "bedrock-mantle")]
	public async Task SignsActualPayloadWithRefreshedSessionCredentialsWithoutPersistingSecrets(bool mantle, string host, string providerId) {
		var handler = new ScriptedHandler();
		handler.Add(_ => true, _ => ScriptedHandler.Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hello\"}\n\n" + Completed));
		var credentials = new RotatingCredentials();
		var provider = new BedrockResponsesProvider(new HttpClient(handler), "us-west-2", mantle, () => credentials);
		for(var i = 1; i <= 2; i++) {
			var deltas = new List<StreamDelta>();
			var result = await provider.CompleteAsync(Request(providerId), deltas.Add, CancellationToken.None);
			var sent = handler.Seen[^1];
			Assert.Equal($"https://{host}/openai/v1/responses", sent.Uri);
			Assert.Equal(host, sent.Headers["Host"]);
			Assert.StartsWith("AWS4-HMAC-SHA256 ", sent.Headers["Authorization"]);
			Assert.Contains("/us-west-2/bedrock/aws4_request", sent.Headers["Authorization"]);
			Assert.Equal("session-token-" + i, sent.Headers["X-Amz-Security-Token"]);
			Assert.Contains("x-amz-security-token", sent.Headers["Authorization"]);
			Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sent.Body))).ToLowerInvariant(), sent.Headers["X-Amz-Content-SHA256"]);
			var body = JsonNode.Parse(sent.Body)!;
			Assert.False(body["store"]!.GetValue<bool>());
			Assert.Equal(512, body["max_output_tokens"]!.GetValue<int>());
			Assert.Equal("high", body["reasoning"]!["effort"]!.GetValue<string>());
			Assert.Equal(Request(providerId).Model.UpstreamModel, body["model"]!.GetValue<string>());
			Assert.Equal("Hello", result.Content);
			Assert.Single(deltas);
			Assert.Equal(120, result.Usage.InputTokens);
			Assert.Equal(20, result.Usage.CachedInputTokens);
			Assert.Equal(12, result.Usage.OutputTokens);
			foreach(var secret in new[] { "AKIDEXAMPLE", "example-secret", "session-token-", "Authorization" })
				Assert.DoesNotContain(secret, result.RawRequest + result.RawResponse);
		}
		Assert.Equal(2, credentials.Reads);
	}

	[Fact]
	public async Task ReplaysFunctionCallIdsAndReportsTruncatedStreamsAsPossiblyBilled() {
		var handler = new ScriptedHandler();
		handler.Add(_ => true, _ => ScriptedHandler.Sse("""
			data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","call_id":"call_aws","name":"add","arguments":""}}

			data: {"type":"response.function_call_arguments.done","item_id":"fc_1","arguments":"{\"a\":2,\"b\":3}"}

			""" + "\n" + Completed));
		var provider = new BedrockResponsesProvider(new HttpClient(handler), "us-west-2", credentials: () => new BasicAWSCredentials("fake", "fake"));
		var response = await provider.CompleteAsync(Request(), null, CancellationToken.None);
		var call = Assert.Single(response.ToolCalls);
		Assert.Equal("call_aws", call.Id);
		Assert.Equal("add", call.Name);
		var replay = provider.BuildRequest(new ProviderRequest {
			Model = Model(), Messages = [ChatMessage.Assistant(null, calls: [call]), ChatMessage.Tool(call.Id, "5")],
		});
		Assert.Equal("call_aws", replay["input"]![0]!["call_id"]!.GetValue<string>());
		Assert.Equal("call_aws", replay["input"]![1]!["call_id"]!.GetValue<string>());
		var broken = new ScriptedHandler();
		broken.Add(_ => true, _ => ScriptedHandler.Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n"));
		var failing = new BedrockResponsesProvider(new HttpClient(broken), "us-west-2", credentials: () => new BasicAWSCredentials("fake", "fake"));
		var error = await Assert.ThrowsAsync<ProviderException>(() => failing.CompleteAsync(Request(), null, CancellationToken.None));
		Assert.True(error.MayHaveBilled);
	}

	[Fact]
	public async Task PreOutputUpstreamErrorEventsAreRetryableButPostOutputErrorsAreNot() {
		async Task<ProviderException> Fail(string sse) {
			var handler = new ScriptedHandler();
			handler.Add(_ => true, _ => ScriptedHandler.Sse(sse));
			var provider = new BedrockResponsesProvider(new HttpClient(handler), "us-east-1", mantle: true, credentials: () => new BasicAWSCredentials("fake", "fake"));
			return await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Request("bedrock-mantle"), null, CancellationToken.None));
		}
		const string ServerError = "data: {\"type\":\"error\",\"error\":{\"type\":\"server_error\",\"code\":\"server_error\",\"message\":\"x\"}}\n\n";
		const string RateLimit = "data: {\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"code\":\"rate_limit_exceeded\",\"message\":\"x\"}}\n\n";
		const string Invalid = "data: {\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"code\":\"invalid_value\",\"message\":\"x\"}}\n\n";
		foreach(var sse in new[] { ServerError, RateLimit }) {
			var e = await Fail(sse);
			Assert.True(e.Retryable);
			Assert.False(e.MayHaveBilled);
		}
		var invalid = await Fail(Invalid);
		Assert.False(invalid.Retryable);
		var afterOutput = await Fail("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" + ServerError);
		Assert.False(afterOutput.Retryable);
		Assert.True(afterOutput.MayHaveBilled);
	}

	[Fact]
	public async Task RejectsWrongBillingBeforeCredentialsAndKeepsCredentialErrorsPrivate() {
		var handler = new ScriptedHandler();
		var reads = 0;
		var provider = new BedrockResponsesProvider(new HttpClient(handler), "us-west-2", credentials: () => {
			reads++; throw new InvalidOperationException("secret-in-credential-process-stderr");
		});
		await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Request(billing: "subscription"), null, CancellationToken.None));
		Assert.Equal(0, reads);
		var error = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Request(), null, CancellationToken.None));
		Assert.DoesNotContain("secret-in-credential-process-stderr", error.Message);
		Assert.Empty(handler.Seen);
	}

	[Fact]
	public void ApiEngineeringUsesConfiguredTeamModelWithoutDispatchingAndIsNotBilledAsSubscription() {
		using var home = new TempHome();
		var provider = new FakeProvider((_, _) => throw new Exception("Onboarding must not dispatch"), "bedrock");
		using var runtime = home.Runtime(provider, o => { o.EngineeringModelId = "bedrock-gpt-6.1-sol"; o.AllowApiModelsForEngineering = true; }, start: false);
		var project = runtime.CreateProject("AWS team", "", home.Workspace, organization: "engineering");
		Assert.Equal(3, runtime.Store.ListAgents(project.Id).Count);
		Assert.All(runtime.Store.ListAgents(project.Id), a => { Assert.Equal("bedrock-gpt-6.1-sol", a.ModelId); Assert.Equal(AgentStates.Sleeping, a.State); });
		Assert.Empty(provider.Requests);
		Assert.Equal(0, runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM model_requests")));
		var model = runtime.Store.GetModel("bedrock-gpt-6.1-sol")!;
		var charge = Pricing.Settle(Pricing.Quote(model, 100, 10), new Usage { InputTokens = 100, OutputTokens = 10 });
		Assert.Equal("api", model.Billing);
		Assert.Null(charge.CashNanos);
		Assert.Equal("unknown", charge.CashBasis);
		runtime.Options.AllowApiModelsForEngineering = false;
		Assert.False(runtime.CanUseEngineeringModel(model));
	}
}
