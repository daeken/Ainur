using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ainur.Core.Providers;

/// <summary>
/// OpenAI-compatible Chat Completions adapter with streaming, used for DeepSeek and Z.ai. Thinking models return
/// <c>reasoning_content</c>, which must be sent back on assistant turns that issued tool calls, so it is preserved verbatim.
/// </summary>
public sealed class ChatCompletionsProvider(string id, HttpClient http, Func<string?> apiKey, string endpoint, Func<string?, string> mapEffort, bool sendEffortLevel = true) : IModelProvider {
	public string Id => id;

	public JsonObject BuildRequest(ProviderRequest request) {
		var messages = new JsonArray();
		foreach(var m in request.Messages) {
			var msg = new JsonObject { ["role"] = m.Role };
			switch(m.Role) {
				case "assistant":
					msg["content"] = m.Content ?? "";
					if(!string.IsNullOrEmpty(m.Reasoning)) msg["reasoning_content"] = m.Reasoning;
					if(m.ToolCalls is { Count: > 0 })
						msg["tool_calls"] = new JsonArray(m.ToolCalls.Select(c => (JsonNode) new JsonObject {
							["id"] = c.Id, ["type"] = "function",
							["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments },
						}).ToArray());
					break;
				case "tool":
					msg["tool_call_id"] = m.ToolCallId;
					msg["content"] = m.Content ?? "";
					break;
				default:
					msg["content"] = m.Content ?? "";
					break;
			}
			messages.Add(msg);
		}
		var body = new JsonObject {
			["model"] = request.Model.UpstreamModel,
			["messages"] = messages,
			["stream"] = true,
			["stream_options"] = new JsonObject { ["include_usage"] = true },
			["max_tokens"] = request.MaxOutputTokens,
		};
		var effort = mapEffort(request.ReasoningEffort);
		if(effort == "none")
			body["thinking"] = new JsonObject { ["type"] = "disabled" };
		else {
			body["thinking"] = new JsonObject { ["type"] = "enabled" };
			if(sendEffortLevel) body["reasoning_effort"] = effort;
		}
		if(request.Tools.Count > 0) {
			body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode) new JsonObject {
				["type"] = "function",
				["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.InputSchema.DeepClone() },
			}).ToArray());
			if(request.ToolChoice is not null) body["tool_choice"] = request.ToolChoice;
		}
		return body;
	}

	public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) {
		var key = apiKey() ?? throw new ProviderException($"No API key found for provider {id}.");
		var body = BuildRequest(request);
		var rawRequest = body.ToJsonString();
		using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint) {
			Content = new StringContent(rawRequest, Encoding.UTF8, "application/json"),
		};
		msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
		msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

		HttpResponseMessage resp;
		try {
			resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
		} catch(HttpRequestException e) {
			throw new ProviderException($"{id} transport error: {e.Message}", retryable: true);
		}
		using var _ = resp;
		if(!resp.IsSuccessStatusCode) {
			var error = await resp.Content.ReadAsStringAsync(ct);
			var status = (int) resp.StatusCode;
			throw new ProviderException($"{id} HTTP {status}: {TextUtil.Truncate(error, 2000)}", status, retryable: status is 429 or >= 500);
		}

		var result = new ProviderResponse { RawRequest = rawRequest };
		var content = new StringBuilder();
		var reasoning = new StringBuilder();
		var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
		var raw = new StringBuilder();
		var sawUsage = false;
		try {
			using var stream = await resp.Content.ReadAsStreamAsync(ct);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			while(await reader.ReadLineAsync(ct) is { } line) {
				if(!line.StartsWith("data:")) continue;
				var data = line[5..].Trim();
				if(data == "[DONE]") break;
				raw.Append(data).Append('\n');
				using var doc = JsonDocument.Parse(data);
				var root = doc.RootElement;
				if(root.TryGetProperty("model", out var model)) result.UpstreamModel = model.GetString();
				if(root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) {
					sawUsage = true;
					result.Usage = ParseUsage(usage);
				}
				if(!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
				var choice = choices[0];
				if(choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
					result.FinishReason = fr.GetString();
				if(!choice.TryGetProperty("delta", out var delta)) continue;
				if(delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String) {
					reasoning.Append(rc.GetString());
					onDelta?.Invoke(new("reasoning", rc.GetString()!));
				}
				if(delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String) {
					content.Append(c.GetString());
					onDelta?.Invoke(new("content", c.GetString()!));
				}
				if(delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
					foreach(var tc in tcs.EnumerateArray()) {
						var index = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : calls.Count;
						if(!calls.TryGetValue(index, out var entry)) entry = ("", "", new StringBuilder());
						if(tc.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String) entry.Id = id.GetString()!;
						if(tc.TryGetProperty("function", out var fn)) {
							if(fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) entry.Name += name.GetString();
							if(fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String) entry.Args.Append(args.GetString());
						}
						calls[index] = entry;
					}
			}
		} catch(Exception e) when(e is IOException or HttpRequestException or JsonException) {
			throw new ProviderException($"{id} stream interrupted: {e.Message}", retryable: true, mayHaveBilled: true);
		}

		result.Content = content.Length > 0 ? content.ToString() : null;
		result.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
		result.ToolCalls = calls.Values.Select((c, i) => new ToolCall(string.IsNullOrEmpty(c.Id) ? $"call_{i}" : c.Id, c.Name, c.Args.Length == 0 ? "{}" : c.Args.ToString())).ToList();
		result.RawResponse = raw.ToString();
		if(!sawUsage) result.Usage = new Usage { Reported = false };
		if(result.FinishReason is null)
			throw new ProviderException($"{id} stream ended without a finish reason", retryable: true, mayHaveBilled: true);
		return result;
	}

	static Usage ParseUsage(JsonElement u) {
		long Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
		var usage = new Usage {
			InputTokens = Get(u, "prompt_tokens"),
			OutputTokens = Get(u, "completion_tokens"),
			CachedInputTokens = Get(u, "prompt_cache_hit_tokens"),
		};
		if(usage.CachedInputTokens == 0 && u.TryGetProperty("prompt_tokens_details", out var ptd) && ptd.ValueKind == JsonValueKind.Object)
			usage.CachedInputTokens = Get(ptd, "cached_tokens");
		if(u.TryGetProperty("completion_tokens_details", out var ctd) && ctd.ValueKind == JsonValueKind.Object)
			usage.ReasoningTokens = Get(ctd, "reasoning_tokens");
		return usage;
	}
}

public static class DeepSeekProvider {
	public static string MapEffort(string? effort) => effort switch {
		null => "high",
		"none" => "none",
		"minimal" or "low" => "low",
		"medium" or "high" or "xhigh" => "high",
		"max" or "ultra" => "max",
		_ => "high",
	};

	public static ChatCompletionsProvider CreateDefault() =>
		new("deepseek", new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, () => Credentials.Resolve("deepseek"), "https://api.deepseek.com/chat/completions", MapEffort);
}

/// <summary>Z.ai GLM models through the GLM Coding Plan (subscription) endpoint.</summary>
public static class ZaiProvider {
	public static string MapEffort(string? effort) => effort switch {
		"none" => "none",
		_ => "enabled",
	};

	public static ChatCompletionsProvider CreateDefault() =>
		new("zai", new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, () => Credentials.Resolve("zai"), "https://api.z.ai/api/coding/paas/v4/chat/completions", MapEffort, sendEffortLevel: false);
}
