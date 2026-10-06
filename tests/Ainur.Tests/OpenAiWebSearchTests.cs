using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Providers;

namespace Ainur.Tests;

public class OpenAiWebSearchTests {
	static readonly ModelInfo Model = new() { Id = "gpt-6-astra-api", Provider = "openai", UpstreamModel = "gpt-6-astra", Billing = "api", Enabled = true };

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OutboundToolsAppendOnlyOptedInNativeSearch(bool enabled) {
		var handler = new ScriptedHandler();
		handler.Add(_ => true, _ => ScriptedHandler.Sse("data: {\"type\":\"response.completed\",\"response\":{}}\n\n"));
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "test-key", routeOverride: "api");
		await provider.CompleteAsync(new ProviderRequest {
			Model = Model, Messages = [ChatMessage.User("hi")], EnableWebSearch = enabled,
			Tools = [new ToolSpec("local", "local tool", new JsonObject { ["type"] = "object" })],
		}, null, default);
		var expected = JsonNode.Parse("""[{"type":"function","name":"local","description":"local tool","parameters":{"type":"object"},"strict":false}]""")!.AsArray();
		if(enabled) expected.Add(new JsonObject { ["type"] = "web_search" });
		Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(handler.Seen.Single().Body)!["tools"]));
	}

	[Fact]
	public async Task SearchEventsPreserveMessageQueriesCitationsAndFullInputUsage() {
		var events = new[] {
			"""{"type":"response.output_item.added","output_index":0,"item":{"id":"ws_1","type":"web_search_call","status":"in_progress"}}""",
			"""{"type":"response.web_search_call.in_progress","item_id":"ws_1","output_index":0}""",
			"""{"type":"response.web_search_call.searching","item_id":"ws_1","output_index":0}""",
			"""{"type":"response.web_search_call.completed","item_id":"ws_1","output_index":0}""",
			"""{"type":"response.output_item.done","output_index":0,"item":{"id":"ws_1","type":"web_search_call","status":"completed","action":{"type":"search","query":"example","queries":["example"]}}}""",
			"""{"type":"response.output_item.added","output_index":1,"item":{"id":"msg_1","type":"message","content":[]}}""",
			"""{"type":"response.output_text.delta","delta":"Found example."}""",
			"""{"type":"response.output_text.annotation.added","annotation":{"type":"url_citation","url":"https://example.com","title":"Example","start_index":0,"end_index":13}}""",
			"""{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":12810,"input_tokens_details":{"cached_tokens":4096},"output_tokens":149,"output_tokens_details":{"reasoning_tokens":75}}}}""",
		};
		var handler = new ScriptedHandler();
		handler.Add(_ => true, _ => ScriptedHandler.Sse(string.Join("", events.Select(e => "data: " + e + "\n\n"))));
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "test-key", routeOverride: "api");
		var r = await provider.CompleteAsync(new ProviderRequest { Model = Model, Messages = [ChatMessage.User("search")], EnableWebSearch = true }, null, default);
		Assert.Equal("Found example.", r.Content);
		Assert.Empty(r.ToolCalls);
		Assert.Equal("stop", r.FinishReason);
		Assert.Equal("example", Assert.Single(r.WebSearchCalls)["action"]!["queries"]![0]!.GetValue<string>());
		Assert.Equal("https://example.com", Assert.Single(r.Annotations)["url"]!.GetValue<string>());
		Assert.True(r.Usage.Reported);
		Assert.Equal(12810, r.Usage.InputTokens);
		Assert.Equal(4096, r.Usage.CachedInputTokens);
		Assert.Equal(149, r.Usage.OutputTokens);
		Assert.Equal(75, r.Usage.ReasoningTokens);
		Assert.Contains("web_search_call", r.RawResponse);
	}
}
