using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Xunit.Abstractions;

namespace Ainur.Tests;

[Trait("Category", "Live")]
public class LiveBedrockTests(ITestOutputHelper output) {
	[SkippableFact]
	public async Task MantleToolRoundTripWithUsageAndDurableAccounting() {
		Skip.IfNot(Environment.GetEnvironmentVariable("AINUR_LIVE_BEDROCK") == "1", "requires explicit AINUR_LIVE_BEDROCK=1 and an AWS profile");
		using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
		using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
		var provider = new BedrockResponsesProvider(http, Environment.GetEnvironmentVariable("AINUR_MANTLE_REGION") ?? "us-east-1", mantle: true);
		using var home = new TempHome();
		using var rt = home.Runtime(provider, start: false);
		var modelId = Environment.GetEnvironmentVariable("AINUR_LIVE_BEDROCK_MODEL") ?? "mantle-gpt-6.1-sol";
		var project = rt.CreateProject("AWS tool smoke", "Disposable bounded test", home.Workspace, budgetDollars: 1, managerModelId: modelId);
		var model = rt.Store.GetModel(modelId)!;
		var messages = new List<ChatMessage> {
			ChatMessage.System("You are testing a calculator tool. Call add exactly once with a=2,b=3. After its result, reply with only the resulting integer. No other tools or actions."),
			ChatMessage.User("Use add to calculate 2+3."),
		};
		var tools = new List<ToolSpec> { new("add", "Add two integers", Schema.Object(("a", Schema.Integer("First integer"), true), ("b", Schema.Integer("Second integer"), true))) };
		ModelCall Call() => new() { ProjectId = project.Id, Model = model, Purpose = "smoke", Category = "direct", Messages = messages, Tools = tools, MaxOutputTokens = 1024, ReasoningEffort = "medium" };
		// Mirror the runtime's handling: only provider-classified retryable failures are retried, with backoff.
		async Task<ModelCallResult> CallWithRetry() {
			for(var attempt = 1; ; attempt++) {
				using var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
				perAttempt.CancelAfter(TimeSpan.FromSeconds(90));
				try {
					return await rt.Gateway.CallAsync(Call(), perAttempt.Token);
				} catch(ProviderException e) when(e.Retryable && attempt < 4) {
					output.WriteLine($"attempt {attempt} retryable upstream failure: {TextUtil.Truncate(e.Message, 300)}");
					await Task.Delay(TimeSpan.FromSeconds(2 << attempt), timeout.Token);
				}
			}
		}
		var first = await CallWithRetry();
		output.WriteLine($"First response at {DateTimeOffset.UtcNow:O}: {JsonUtil.Serialize(first.Response.Usage)}");
		var tool = Assert.Single(first.Response.ToolCalls);
		Assert.Equal("add", tool.Name);
		var args = JsonNode.Parse(tool.Arguments)!;
		Assert.Equal(2, args["a"]!.GetValue<int>());
		Assert.Equal(3, args["b"]!.GetValue<int>());
		messages.Add(ChatMessage.Assistant(first.Response.Content, calls: [tool]));
		messages.Add(ChatMessage.Tool(tool.Id, (args["a"]!.GetValue<int>() + args["b"]!.GetValue<int>()).ToString()));
		var second = await CallWithRetry();
		Assert.Equal("5", second.Response.Content?.Trim());
		Assert.Empty(second.Response.ToolCalls);
		Assert.Equal(2, rt.Store.CostEvents(project.Id).Count);
		foreach(var response in new[] { first, second }) {
			Assert.True(response.Response.Usage.Reported);
			Assert.True(response.Response.Usage.InputTokens > 0);
			Assert.NotNull(response.Cost);
			Assert.True(response.Cost.EffectiveNanos > 0);
			Assert.Null(response.Cost.CashNanos);
			output.WriteLine($"model={response.Response.UpstreamModel} usage={JsonUtil.Serialize(response.Response.Usage)} cash_basis={response.Cost.CashBasis}");
		}
	}
}
