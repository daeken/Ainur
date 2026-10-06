using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Providers;
using Ainur.Core.Model;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;

namespace Ainur.Tests;

public class FinderReliabilityTests {
	sealed class FinderProvider(Func<ProviderResponse> answer) : IModelProvider {
		public string Id => "deepseek";
		public int Calls;
		public Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) {
			Calls++;
			return Task.FromResult(answer());
		}
	}

	static (AinurRuntime Runtime, ToolContext Context) Setup(TempHome home, IModelProvider provider, CancellationToken ct = default) {
		var rt = home.Runtime(provider, start: false);
		var project = rt.CreateProject("Finder", "d", home.Workspace);
		var root = rt.Store.GetAgent(project.RootAgentId!)!;
		var session = rt.Store.GetSession(root.PrimarySessionId!)!;
		return (rt, new ToolContext { Runtime = rt, Project = project, Agent = root, Session = session,
			Host = rt.GetHost(session.Id)!, InvocationId = "finder-test", CancellationToken = ct });
	}

	// Test adapter lets the gateway's default finder model drive the real hermetic SSE parser.
	sealed class SseFinderProvider(OpenAiResponsesProvider inner) : IModelProvider {
		public string Id => "deepseek";
		public Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) =>
			inner.CompleteAsync(new ProviderRequest {
				Model = new ModelInfo { Id = "finder-test", Provider = "openai", UpstreamModel = "finder-test", Billing = "api", Enabled = true },
				Messages = request.Messages, Tools = request.Tools, OpenAiRoutePolicy = OpenAiRoutePolicy.Api
			}, onDelta, ct);
	}

	static JsonObject Args(bool load = false) => new() { ["need"] = "file", ["load"] = load };

	[Theory]
	[InlineData(false, false)] // Complete-looking JSON at EOF still lacks provider completion.
	[InlineData(false, true)] // [DONE] is not a substitute for response.completed.
	[InlineData(true, false)]
	public async Task RealSseFinderRequiresAuthoritativeCompletion(bool completed, bool done) {
		using var home = new TempHome();
		var handler = new ScriptedHandler();
		var answer = "{\"tools\":[\"read_file\"],\"why\":\"read it\"}";
		var delta = new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = answer };
		var body = $"data: {delta.ToJsonString()}\n\n" +
			(completed ? "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n" : "") +
			(done ? "data: [DONE]\n\n" : "");
		handler.Add(_ => true, _ => ScriptedHandler.Sse(body));
		using var http = new HttpClient(handler);
		var provider = new SseFinderProvider(new OpenAiResponsesProvider(http, apiKey: () => "test-key", codexAuthPath: () => null, routeOverride: "api"));
		var (rt, ctx) = Setup(home, provider);
		using var _ = rt;
		var result = await new FindToolsTool().InvokeAsync(ctx, Args());
		Assert.Single(handler.Seen);
		if(completed) {
			Assert.DoesNotContain("keyword fallback", result.Text);
			Assert.Equal(new[] { "read_file" }, Assert.IsType<List<string>>(result.Value));
		} else {
			Assert.Contains("keyword fallback", result.Text);
			Assert.Single(rt.Store.ModelRequestsInState("unknown"));
		}
	}

	[Theory]
	[InlineData("{\"tools\":[\"read_file\"],\"why\":\"read it\"}", "length")]
	[InlineData("{\"tools\":[\"read_file\"],\"why\":\"read it\"}", "incomplete")]
	[InlineData("{\"tools\":[\"read_file\"],\"why\":\"read it\"}", null)]
	[InlineData("prefix {\"tools\":[\"read_file\"],\"why\":\"read it\"} tail", "stop")]
	[InlineData("{\"tools\":[\"read_file\"],\"why\":\"unfinished", "stop")]
	[InlineData("{}", "stop")]
	[InlineData("{\"tools\":[\"invented\"],\"why\":\"oops\"}", "stop")]
	[InlineData("{\"tools\":[\"read_file\",4],\"why\":\"oops\"}", "stop")]
	[InlineData("{\"tools\":[\"read_file\",\"read_file\"],\"why\":\"oops\"}", "stop")]
	[InlineData("{\"tools\":[\"read_file\",\"write_file\",\"list_files\",\"multi_edit\",\"search_text\"],\"why\":\"too many\"}", "stop")]
	[InlineData("{\"tools\":[],\"why\":4}", "stop")]
	public async Task InvalidOrIncompleteAnswersUseTruthfulKeywordFallback(string content, string? finish) {
		using var home = new TempHome();
		var provider = new FinderProvider(() => new ProviderResponse { Content = content, FinishReason = finish });
		var (rt, ctx) = Setup(home, provider);
		using var _ = rt;
		var expected = rt.Tools.Search(ctx.Project.Id, "file", 15).Take(3).Select(c => c.Tool.Name).ToList();
		var result = await new FindToolsTool().InvokeAsync(ctx, Args());
		Assert.False(result.IsError);
		Assert.Contains("keyword fallback", result.Text);
		Assert.Equal(expected, Assert.IsType<List<string>>(result.Value));
		Assert.Equal(1, provider.Calls);
	}

	[Fact]
	public async Task UnexpectedToolCallsAreNotExecutedAndUseFallback() {
		using var home = new TempHome();
		var provider = new FinderProvider(() => new ProviderResponse { Content = "{\"tools\":[\"read_file\"],\"why\":\"read\"}", FinishReason = "stop",
			ToolCalls = [new ToolCall("bad", "write_file", "{}") ] });
		var (rt, ctx) = Setup(home, provider);
		using var _ = rt;
		var result = await new FindToolsTool().InvokeAsync(ctx, Args());
		Assert.Contains("keyword fallback", result.Text);
		Assert.Equal(1, provider.Calls);
		Assert.DoesNotContain(rt.Store.Items(ctx.Session.Id), i => i.Kind == "tool_result");
	}

	[Fact]
	public async Task ValidCompletedAnswerLoadsOnlySelectedTools() {
		using var home = new TempHome();
		var provider = new FinderProvider(() => new ProviderResponse { Content = " {\"tools\":[\"read_file\"],\"why\":\"read it\"} ", FinishReason = "stop" });
		var (rt, ctx) = Setup(home, provider);
		using var _ = rt;
		var result = await new FindToolsTool().InvokeAsync(ctx, Args(load: true));
		Assert.Equal(new[] { "read_file" }, Assert.IsType<List<string>>(result.Value));
		Assert.DoesNotContain("fallback", result.Text);
		Assert.Contains("Loaded for your next step: read_file", result.Text);
		Assert.Contains(ctx.Host.Cache.Entries(), e => e.Name == "read_file");
		Assert.Equal(1, provider.Calls);
	}

	[Fact]
	public async Task ValidEmptySelectionIsNotMistakenForFallback() {
		using var home = new TempHome();
		var provider = new FinderProvider(() => new ProviderResponse { Content = "{\"tools\":[],\"why\":\"nothing fits\"}", FinishReason = "stop" });
		var (rt, ctx) = Setup(home, provider);
		using var _ = rt;
		var result = await new FindToolsTool().InvokeAsync(ctx, Args());
		Assert.Contains("No registered tool fits", result.Text);
		Assert.DoesNotContain("keyword fallback", result.Text);
		Assert.Equal(1, provider.Calls);
	}

	[Fact]
	public async Task AmbiguousProviderFailureDoesNotDispatchAnotherModel() {
		using var home = new TempHome();
		var provider = new FinderProvider(() => throw new ProviderException("stream ended before terminal event", retryable: true, mayHaveBilled: true));
		var (rt, ctx) = Setup(home, provider);
		using var _ = rt;
		var result = await new FindToolsTool().InvokeAsync(ctx, Args());
		Assert.Contains("keyword fallback", result.Text);
		Assert.Equal(1, provider.Calls);
		var records = rt.Store.ModelRequestsInState("unknown");
		Assert.Single(records);
		Assert.Equal("unknown", records.Single().State);
	}

	[Fact]
	public async Task CancellationIsPropagatedNotHiddenByFallback() {
		using var home = new TempHome();
		using var cts = new CancellationTokenSource();
		var provider = new FinderProvider(() => throw new OperationCanceledException(cts.Token));
		var (rt, ctx) = Setup(home, provider, cts.Token);
		using var _ = rt;
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FindToolsTool().InvokeAsync(ctx, Args()));
		Assert.Equal(1, provider.Calls);
	}
}
