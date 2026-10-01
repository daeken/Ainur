using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Tools;

namespace Ainur.Tests;

/// <summary>
/// Scripted <see cref="HttpMessageHandler"/>: never touches the network. Records every request (method, uri, headers,
/// body) and answers from an ordered list of URL-predicate rules. This is what keeps the OpenAI provider tests hermetic.
/// </summary>
public sealed class ScriptedHandler : HttpMessageHandler {
	public sealed record SeenRequest(string Method, string Uri, Dictionary<string, string> Headers, string Body);

	public readonly List<SeenRequest> Seen = [];
	readonly List<(Func<HttpRequestMessage, bool> Match, Func<HttpRequestMessage, HttpResponseMessage> Respond)> Rules = [];

	public void Add(Func<HttpRequestMessage, bool> match, Func<HttpRequestMessage, HttpResponseMessage> respond) => Rules.Add((match, respond));

	public static HttpResponseMessage Sse(string body) {
		var resp = new HttpResponseMessage(HttpStatusCode.OK);
		resp.Content = new StringContent(body, Encoding.UTF8, "text/event-stream");
		return resp;
	}

	public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) {
		var resp = new HttpResponseMessage(code);
		resp.Content = new StringContent(body, Encoding.UTF8, "application/json");
		return resp;
	}

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach(var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
		if(request.Content?.Headers is { } ch)
			foreach(var h in ch) headers.TryAdd(h.Key, string.Join(",", h.Value));
		var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
		Seen.Add(new(request.Method.Method, request.RequestUri?.ToString() ?? "", headers, body));
		foreach(var (match, respond) in Rules)
			if(match(request))
				return respond(request);
		return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("not found", Encoding.UTF8, "text/plain") };
	}
}

public class OpenAiProviderTests {
	const string SubscriptionEndpoint = OpenAiResponsesProvider.SubscriptionEndpoint;
	const string ApiEndpoint = OpenAiResponsesProvider.ApiEndpoint;
	const long NowMs = 1_770_000_000_000; // fixed clock so JWT expiry is deterministic
	static readonly string NoFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-openai-does-not-exist-" + Guid.NewGuid().ToString("N"));

	static long Now() => NowMs;
	static long ExpUnix(long deltaSeconds) => NowMs / 1000 + deltaSeconds;

	static ModelInfo Gpt(string id = "gpt-6-astra") => new() { Id = id, Provider = "openai", UpstreamModel = id, Billing = "subscription", Enabled = true };
	static ProviderRequest Req(params ChatMessage[] messages) => new() { Model = Gpt(), Messages = [.. messages] };

	static string Jwt(long expUnixSeconds, string iss = "https://auth.openai.com", string clientId = "app_test", string accountId = "acc_test") {
		static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
		var header = B64("{\"alg\":\"none\",\"typ\":\"JWT\"}");
		var payload = B64($"{{\"iss\":\"{iss}\",\"client_id\":\"{clientId}\",\"exp\":{expUnixSeconds},\"https://api.openai.com/auth.chatgpt_account_id\":\"{accountId}\"}}");
		return header + "." + payload + ".sig";
	}

	static string Sse(params JsonObject[] events) => string.Concat(events.Select(e => "data: " + e.ToJsonString() + "\n\n"));

	static JsonObject Delta(string text) => new() { ["type"] = "response.output_text.delta", ["delta"] = text };
	static JsonObject Completed(long input, long output, long cached = 0, long reasoning = 0) => new() {
		["type"] = "response.completed",
		["response"] = new JsonObject { ["usage"] = new JsonObject {
			["input_tokens"] = input, ["output_tokens"] = output,
			["input_tokens_details"] = new JsonObject { ["cached_tokens"] = cached },
			["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = reasoning },
		} },
	};

	static OpenAiResponsesProvider Provider(ScriptedHandler handler, string? apiKey = "api-key", Func<string?>? codexAuthPath = null, string? routeOverride = "api") =>
		new(new HttpClient(handler), apiKey: () => apiKey, codexAuthPath: codexAuthPath ?? (() => NoFile), routeOverride: routeOverride, nowMs: Now);

	static void WriteAuth(string path, string accessToken, string accountId = "acc_test", string? refresh = "refresh_test") {
		var obj = new JsonObject {
			["auth_mode"] = "chatgpt",
			["OPENAI_API_KEY"] = "",
			["tokens"] = new JsonObject {
				["id_token"] = "idt_old",
				["access_token"] = accessToken,
				["refresh_token"] = refresh,
				["account_id"] = accountId,
			},
			["last_refresh"] = "2026-01-01T00:00:00Z",
			["extra_unknown"] = "keepme",
		};
		File.WriteAllText(path, obj.ToJsonString());
	}

	// ---------- SSE parsing ----------

	[Fact]
	public async Task StreamsPlainTextAndMapsUsage() {
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(
			new JsonObject { ["type"] = "response.created", ["response"] = new JsonObject { ["model"] = "gpt-6-astra" } },
			Delta("Hello"),
			Delta(" world"),
			Completed(23, 7, cached: 5, reasoning: 3))));
		var provider = Provider(handler);
		var r = await provider.CompleteAsync(Req(ChatMessage.System("sys"), ChatMessage.User("hi")), null, default);
		Assert.Equal("Hello world", r.Content);
		Assert.Equal(23, r.Usage.InputTokens);
		Assert.Equal(7, r.Usage.OutputTokens);
		Assert.Equal(5, r.Usage.CachedInputTokens);
		Assert.Equal(3, r.Usage.ReasoningTokens);
		Assert.True(r.Usage.Reported);
		Assert.Equal("gpt-6-astra", r.UpstreamModel);
		Assert.Equal("stop", r.FinishReason);
	}

	[Fact]
	public async Task StreamsToolCall() {
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(
			new JsonObject { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = new JsonObject { ["type"] = "function_call", ["id"] = "fc_1", ["call_id"] = "call_abc", ["name"] = "get_weather", ["arguments"] = "" } },
			new JsonObject { ["type"] = "response.function_call_arguments.delta", ["item_id"] = "fc_1", ["output_index"] = 0, ["delta"] = "{\"city\":" },
			new JsonObject { ["type"] = "response.function_call_arguments.delta", ["item_id"] = "fc_1", ["output_index"] = 0, ["delta"] = "\"Oslo\"}" },
			new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = new JsonObject { ["type"] = "function_call", ["id"] = "fc_1", ["call_id"] = "call_abc", ["name"] = "get_weather", ["arguments"] = "{\"city\":\"Oslo\"}" } },
			Completed(10, 2))));
		var provider = Provider(handler);
		var r = await provider.CompleteAsync(Req(ChatMessage.User("weather?")), null, default);
		Assert.Single(r.ToolCalls);
		Assert.Equal("get_weather", r.ToolCalls[0].Name);
		Assert.Equal("call_abc", r.ToolCalls[0].Id);
		Assert.Contains("Oslo", r.ToolCalls[0].Arguments);
		Assert.Equal("tool_calls", r.FinishReason);
	}

	[Fact]
	public async Task StreamsReasoningDeltas() {
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(
			new JsonObject { ["type"] = "response.reasoning_summary_text.delta", ["delta"] = "think" },
			new JsonObject { ["type"] = "response.reasoning_summary_text.delta", ["delta"] = "ing..." },
			Delta("answer"),
			Completed(20, 6))));
		var provider = Provider(handler);
		var deltas = new List<StreamDelta>();
		var r = await provider.CompleteAsync(Req(ChatMessage.User("q")), d => deltas.Add(d), default);
		Assert.Equal("thinking...", r.Reasoning);
		Assert.Contains(deltas, d => d.Kind == "reasoning" && d.Text == "ing...");
		Assert.Contains(deltas, d => d.Kind == "content" && d.Text == "answer");
	}

	[Fact]
	public async Task ResponseFailedWithUsageSetsMayHaveBilledTrue() {
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(
			new JsonObject { ["type"] = "response.failed", ["response"] = new JsonObject { ["status"] = "failed", ["usage"] = new JsonObject { ["input_tokens"] = 5, ["output_tokens"] = 1 } } })));
		var provider = Provider(handler);
		var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default));
		Assert.True(ex.MayHaveBilled);
	}

	[Fact]
	public async Task ResponseFailedWithoutUsageSetsMayHaveBilledFalse() {
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(
			new JsonObject { ["type"] = "response.failed", ["response"] = new JsonObject { ["status"] = "failed" } })));
		var provider = Provider(handler);
		var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default));
		Assert.False(ex.MayHaveBilled);
	}

	[Fact]
	public async Task ErrorEventThrows() {
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(
			new JsonObject { ["type"] = "error", ["message"] = "boom" })));
		var provider = Provider(handler);
		var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default));
		Assert.False(ex.MayHaveBilled);
		Assert.Contains("boom", ex.Message);
	}

	// ---------- request body & headers ----------

	[Fact]
	public async Task BuildsResponsesRequestBodyCorrectly() {
		var handler = new ScriptedHandler();
		handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(Delta("ok"), Completed(5, 1))));
		var tool = new ToolSpec("get_weather", "Get the weather.", Schema.Object(("city", Schema.String("City"), true)));
		var provider = Provider(handler);
		await provider.CompleteAsync(new ProviderRequest {
			Model = Gpt(),
			Messages = [ChatMessage.System("You are helpful."), ChatMessage.User("hi")],
			Tools = [tool],
			ReasoningEffort = "low",
		}, null, default);

		var body = (JsonObject) JsonNode.Parse(handler.Seen[0].Body)!;
		Assert.Equal("gpt-6-astra", body["model"]!.GetValue<string>());
		Assert.True(body["stream"]!.GetValue<bool>());
		Assert.False(body["store"]!.GetValue<bool>());
		Assert.Equal("You are helpful.", body["instructions"]!.GetValue<string>());
		Assert.Equal("low", body["reasoning"]!["effort"]!.GetValue<string>());
		var input = body["input"]!.AsArray();
		Assert.Single(input);
		Assert.Equal("message", input[0]!["type"]!.GetValue<string>());
		Assert.Equal("user", input[0]!["role"]!.GetValue<string>());
		Assert.Equal("input_text", input[0]!["content"]![0]!["type"]!.GetValue<string>());
		Assert.Equal("hi", input[0]!["content"]![0]!["text"]!.GetValue<string>());
		var tools = body["tools"]!.AsArray();
		Assert.Single(tools);
		Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
		Assert.Equal("get_weather", tools[0]!["name"]!.GetValue<string>());
		Assert.False(tools[0]!["strict"]!.GetValue<bool>());
	}

	[Fact]
	public async Task SetsSubscriptionHeadersCorrectly() {
		var access = Jwt(ExpUnix(3600));
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			WriteAuth(path, access, accountId: "acc_42");
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString() == SubscriptionEndpoint, m => ScriptedHandler.Sse(Sse(Delta("hi"), Completed(3, 1))));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), codexAuthPath: () => path, routeOverride: "subscription", nowMs: Now);
			await provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default);
			var h = handler.Seen.Single().Headers;
			Assert.Equal("Bearer " + access, h["Authorization"]);
			Assert.Equal("acc_42", h["chatgpt-account-id"]);
			Assert.Equal("codex_cli_rs", h["originator"]);
			Assert.Equal("0.155.0", h["version"]);
			Assert.Equal("responses=experimental", h["OpenAI-Beta"]);
			Assert.True(h.ContainsKey("session_id") && h["session_id"].Length > 0);
			Assert.True(h.ContainsKey("User-Agent"));
		} finally {
			File.Delete(path);
		}
	}

	// ---------- subscription auth / refresh / failover ----------

	[Fact]
	public async Task RefreshesExpiredTokenAndWritesBackPreservingUnknownFields() {
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			WriteAuth(path, Jwt(ExpUnix(-3600)), accountId: "acc_1", refresh: "rt_old");
			var newAccess = Jwt(ExpUnix(3600), accountId: "acc_1");
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString().EndsWith("/oauth/token"), m => ScriptedHandler.Json(new JsonObject {
				["access_token"] = newAccess, ["id_token"] = "idt_new", ["refresh_token"] = "rt_new",
			}.ToJsonString()));
			handler.Add(m => m.RequestUri!.ToString() == SubscriptionEndpoint, m => ScriptedHandler.Sse(Sse(Delta("ok"), Completed(4, 1))));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), codexAuthPath: () => path, routeOverride: "subscription", nowMs: Now);
			var r = await provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default);
			Assert.Equal("ok", r.Content);

			// Refresh happened, then the subscription request used the NEW token.
			Assert.Contains(handler.Seen, x => x.Uri.EndsWith("/oauth/token"));
			var sub = handler.Seen.Single(x => x.Uri == SubscriptionEndpoint);
			Assert.Equal("Bearer " + newAccess, sub.Headers["Authorization"]);

			// Write-back preserved unknown fields and updated the token set.
			var obj = (JsonObject) JsonNode.Parse(File.ReadAllText(path))!;
			Assert.Equal("keepme", obj["extra_unknown"]!.GetValue<string>());
			Assert.Equal(newAccess, obj["tokens"]!["access_token"]!.GetValue<string>());
			Assert.Equal("idt_new", obj["tokens"]!["id_token"]!.GetValue<string>());
			Assert.Equal("rt_new", obj["tokens"]!["refresh_token"]!.GetValue<string>());
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task RefreshFailureFallsBackToApiRoute() {
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			WriteAuth(path, Jwt(ExpUnix(-3600)), refresh: "rt_old"); // expired token
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString().EndsWith("/oauth/token"), m => new HttpResponseMessage(HttpStatusCode.InternalServerError));
			handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(Delta("api answer"), Completed(4, 2))));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "api-key", codexAuthPath: () => path, routeOverride: "auto", nowMs: Now);
			var r = await provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default);
			Assert.Equal("api answer", r.Content);
			Assert.Contains(handler.Seen, x => x.Uri == ApiEndpoint);
			Assert.DoesNotContain(handler.Seen, x => x.Uri == SubscriptionEndpoint);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Subscription401FallsBackToApiRoute() {
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			WriteAuth(path, Jwt(ExpUnix(3600))); // valid token
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString() == SubscriptionEndpoint, m => ScriptedHandler.Json("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized));
			handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(Delta("api answer"), Completed(4, 2))));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "api-key", codexAuthPath: () => path, routeOverride: "auto", nowMs: Now);
			var r = await provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default);
			Assert.Equal("api answer", r.Content);
			Assert.Contains(handler.Seen, x => x.Uri == ApiEndpoint);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task Subscription429FallsBackToApiRoute() {
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			WriteAuth(path, Jwt(ExpUnix(3600)));
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString() == SubscriptionEndpoint, m => ScriptedHandler.Json("{\"error\":\"rate limited\"}", (HttpStatusCode) 429));
			handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(Delta("api answer"), Completed(4, 2))));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "api-key", codexAuthPath: () => path, routeOverride: "auto", nowMs: Now);
			var r = await provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default);
			Assert.Equal("api answer", r.Content);
			Assert.Contains(handler.Seen, x => x.Uri == ApiEndpoint);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task ExplicitSubscriptionRouteDoesNotFallBackToApi() {
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			WriteAuth(path, Jwt(ExpUnix(3600)));
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString() == SubscriptionEndpoint, m => ScriptedHandler.Json("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "api-key", codexAuthPath: () => path, routeOverride: "subscription", nowMs: Now);
			var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default));
			Assert.Equal(401, ex.Status);
			Assert.DoesNotContain(handler.Seen, x => x.Uri == ApiEndpoint);
		} finally {
			File.Delete(path);
		}
	}

	// ---------- route selection & configuration errors ----------

	[Fact]
	public async Task ApiRouteSelectedWhenEnvSetAndNoSubscriptionFile() {
		var prev = Environment.GetEnvironmentVariable("AINUR_OPENAI_ROUTE");
		try {
			Environment.SetEnvironmentVariable("AINUR_OPENAI_ROUTE", "api");
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString() == ApiEndpoint, m => ScriptedHandler.Sse(Sse(Delta("api answer"), Completed(4, 2))));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => "api-key", codexAuthPath: () => NoFile, nowMs: Now);
			var r = await provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default);
			Assert.Equal("api answer", r.Content);
			Assert.Contains(handler.Seen, x => x.Uri == ApiEndpoint);
		} finally {
			Environment.SetEnvironmentVariable("AINUR_OPENAI_ROUTE", prev);
		}
	}

	[Fact]
	public async Task ApiRouteWithNoKeyFailsFastWithClearError() {
		var handler = new ScriptedHandler(); // no rules: any request would 404
		var provider = new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => null, codexAuthPath: () => NoFile, routeOverride: "api", nowMs: Now);
		var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default));
		Assert.Contains("OPENAI_API_KEY", ex.Message);
		Assert.Contains("ai.openai.api", ex.Message);
		Assert.Empty(handler.Seen); // failed fast, no network call
	}

	// ---------- secret hygiene ----------

	[Fact]
	public async Task NoTokenMaterialInRawArtifacts() {
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			var access = Jwt(ExpUnix(3600));
			WriteAuth(path, access, refresh: "REFRESH-SECRET-999");
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString() == SubscriptionEndpoint, m => ScriptedHandler.Sse(Sse(Delta("hi"), Completed(3, 1))));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), codexAuthPath: () => path, routeOverride: "subscription", nowMs: Now);
			var r = await provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default);
			// RawRequest/RawResponse are what the gateway persists as artifacts and DB rows; they must never contain token material.
			Assert.DoesNotContain(access, r.RawRequest);
			Assert.DoesNotContain("REFRESH-SECRET-999", r.RawRequest);
			Assert.DoesNotContain(access, r.RawResponse);
			Assert.DoesNotContain("REFRESH-SECRET-999", r.RawResponse);
		} finally {
			File.Delete(path);
		}
	}

	[Fact]
	public async Task NoTokenMaterialInErrorMessages() {
		var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ainur-auth-" + Guid.NewGuid().ToString("N") + ".json");
		try {
			var access = Jwt(ExpUnix(3600));
			WriteAuth(path, access, refresh: "REFRESH-SECRET-999");
			var handler = new ScriptedHandler();
			handler.Add(m => m.RequestUri!.ToString() == SubscriptionEndpoint, m => ScriptedHandler.Json("{\"error\":\"unauthorized\"}", HttpStatusCode.Unauthorized));
			var provider = new OpenAiResponsesProvider(new HttpClient(handler), codexAuthPath: () => path, routeOverride: "subscription", nowMs: Now);
			var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CompleteAsync(Req(ChatMessage.User("hi")), null, default));
			Assert.DoesNotContain(access, ex.Message);
			Assert.DoesNotContain("REFRESH-SECRET-999", ex.Message);
		} finally {
			File.Delete(path);
		}
	}
}
