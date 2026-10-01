using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ainur.Core.Providers;

/// <summary>
/// OpenAI Responses API adapter with two auth routes: the ChatGPT subscription backend (primary) and the platform
/// API-key route (fallback). Subscription credentials come from the host Codex credential file
/// (<c>~/.codex/auth.json</c>, env override <c>AINUR_CODEX_AUTH</c>); the API route uses <c>OPENAI_API_KEY</c>
/// (environment, then the macOS keychain service <c>ai.openai.api</c>). The subscription OAuth token is never sent
/// to the platform API: it is not a platform credential, and doing so yields 401/403 (missing scopes).
/// </summary>
public sealed class OpenAiResponsesProvider : IModelProvider {
	public const string SubscriptionEndpoint = "https://chatgpt.com/backend-api/codex/responses";
	public const string ApiEndpoint = "https://api.openai.com/v1/responses";
	const string OauthTokenPath = "/oauth/token";
	const long RefreshWindowMs = 5 * 60 * 1000;
	const string AccountIdClaim = "https://api.openai.com/auth.chatgpt_account_id";

	public string Id => "openai";

	readonly HttpClient http;
	readonly Func<string?> apiKey;
	readonly Func<string?> codexAuthPath;
	readonly Func<string> routeSetting;
	readonly Func<long> nowMs;

	public OpenAiResponsesProvider(HttpClient http, Func<string?>? apiKey = null, Func<string?>? codexAuthPath = null, string? routeOverride = null, Func<long>? nowMs = null) {
		this.http = http;
		this.apiKey = apiKey ?? (() => Credentials.Resolve("openai"));
		this.codexAuthPath = codexAuthPath ?? DefaultCodexAuthPath;
		this.routeSetting = () => routeOverride ?? Environment.GetEnvironmentVariable("AINUR_OPENAI_ROUTE") ?? "auto";
		this.nowMs = nowMs ?? (() => Clock.Now);
	}

	static string? DefaultCodexAuthPath() =>
		Environment.GetEnvironmentVariable("AINUR_CODEX_AUTH") is { Length: > 0 } p
			? p
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

	public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) {
		var route = routeSetting().Trim().ToLowerInvariant();
		switch(route) {
			case "api":
				return await CompleteApiAsync(request, onDelta, ct);
			case "subscription":
				return await CompleteSubscriptionAsync(request, onDelta, ct, allowFailover: false);
			default: { // auto: subscription when a usable credential exists, else api.
				var cred = await ResolveSubscriptionCredentialAsync(ct);
				if(cred is not null)
					return await CompleteSubscriptionAsync(request, onDelta, ct, allowFailover: true, credential: cred);
				return await CompleteApiAsync(request, onDelta, ct);
			}
		}
	}

	async Task<ProviderResponse> CompleteApiAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) {
		var key = apiKey();
		if(string.IsNullOrEmpty(key))
			throw new ProviderException("OpenAI api route selected but no OPENAI_API_KEY found. Set the OPENAI_API_KEY environment variable or store it in the macOS keychain under service 'ai.openai.api'.");
		return await SendAsync(ApiEndpoint, request, onDelta, ct, m => {
			m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
			m.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
		});
	}

	async Task<ProviderResponse> CompleteSubscriptionAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct, bool allowFailover, SubscriptionCredential? credential = null) {
		var emitted = false;
		Action<StreamDelta>? tracked = onDelta is null ? null : d => { emitted = true; onDelta(d); };
		ProviderException? failure = null;
		try {
			credential ??= await ResolveSubscriptionCredentialAsync(ct);
			if(credential is null)
				throw new ProviderException("OpenAI subscription route selected but no usable ChatGPT subscription credential (access token + account id) was found in the Codex auth file.");
			return await SendAsync(SubscriptionEndpoint, request, tracked, ct, m => BuildSubscriptionHeaders(m, credential));
		} catch(ProviderException e) {
			if(!allowFailover || !EligibleForFailover(e, emitted)) throw;
			failure = e;
		}
		var key = apiKey();
		if(string.IsNullOrEmpty(key))
			throw new ProviderException($"OpenAI subscription route failed ({failure?.Message}) and no OPENAI_API_KEY is configured for the api fallback (env OPENAI_API_KEY or macOS keychain service 'ai.openai.api').",
				failure?.Status, retryable: false, mayHaveBilled: false);
		return await SendAsync(ApiEndpoint, request, onDelta, ct, m => {
			m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
			m.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
		});
	}

	static bool EligibleForFailover(ProviderException e, bool emitted) =>
		!emitted && !e.MayHaveBilled && (e.Status is 401 or 403 or 402 or 429 || e is OpenAiFailoverException);

	static void BuildSubscriptionHeaders(HttpRequestMessage m, SubscriptionCredential cred) {
		m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.AccessToken);
		m.Headers.TryAddWithoutValidation("chatgpt-account-id", cred.AccountId);
		m.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
		m.Headers.TryAddWithoutValidation("version", "0.155.0");
		m.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
		m.Headers.TryAddWithoutValidation("session_id", Guid.NewGuid().ToString());
		m.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/0.155.0");
	}

	/// <summary>Reads the Codex credential file and, if needed, refreshes the access token. Returns null when no usable credential exists.</summary>
	async Task<SubscriptionCredential?> ResolveSubscriptionCredentialAsync(CancellationToken ct) {
		var path = codexAuthPath();
		if(path is null || !File.Exists(path)) return null;
		JsonNode? root;
		try { root = JsonNode.Parse(File.ReadAllText(path)); } catch { return null; }
		if(root is not JsonObject obj) return null;
		var tokens = obj["tokens"] as JsonObject;
		var access = tokens?["access_token"]?.GetValue<string>();
		if(string.IsNullOrEmpty(access)) return null;
		var accountId = tokens?["account_id"]?.GetValue<string>() ?? "";
		var refresh = tokens?["refresh_token"]?.GetValue<string>();

		var claims = DecodeJwt(access);
		var now = nowMs();
		var expMs = claims.TryGetValue("exp", out var expRaw) && long.TryParse(expRaw, out var expSec) ? expSec * 1000 : long.MaxValue;
		if(accountId.Length == 0 && claims.TryGetValue(AccountIdClaim, out var claimAccount)) accountId = claimAccount;
		if(accountId.Length == 0) return null;

		if(expMs - now < RefreshWindowMs) {
			var newAccess = await TryRefreshAsync(claims, refresh, obj, path, ct);
			if(newAccess is not null)
				access = newAccess;
			else if(expMs <= now)
				return null; // expired and refresh failed → unusable
			// else: refresh failed but the token is still valid → use it.
		}
		return new SubscriptionCredential(access, accountId);
	}

	/// <summary>Refreshes the OAuth token and writes the new token set back atomically. Returns the new access token, or null on failure.</summary>
	async Task<string?> TryRefreshAsync(Dictionary<string, string> claims, string? refreshToken, JsonObject fileObj, string path, CancellationToken ct) {
		if(string.IsNullOrEmpty(refreshToken)) return null;
		if(!claims.TryGetValue("client_id", out var clientId) || string.IsNullOrEmpty(clientId)) return null;
		var iss = claims.TryGetValue("iss", out var i) ? i : "https://auth.openai.com";
		var tokenUrl = iss.TrimEnd('/') + OauthTokenPath;
		using var msg = new HttpRequestMessage(HttpMethod.Post, tokenUrl) {
			Content = new FormUrlEncodedContent(new Dictionary<string, string> {
				["client_id"] = clientId,
				["grant_type"] = "refresh_token",
				["refresh_token"] = refreshToken,
				["scope"] = "openid profile email",
			}),
		};
		HttpResponseMessage resp;
		try {
			resp = await http.SendAsync(msg, ct);
		} catch(HttpRequestException) {
			return null;
		}
		using var _ = resp;
		if(!resp.IsSuccessStatusCode) return null;
		JsonNode? json;
		try { json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)); } catch { return null; }
		if(json is not JsonObject r) return null;
		var newAccess = r["access_token"]?.GetValue<string>();
		if(string.IsNullOrEmpty(newAccess)) return null;

		if(fileObj["tokens"] is JsonObject t) {
			t["access_token"] = newAccess;
			if(r["id_token"]?.GetValue<string>() is { Length: > 0 } idt) t["id_token"] = idt;
			if(r["refresh_token"]?.GetValue<string>() is { Length: > 0 } rft) t["refresh_token"] = rft;
		}
		fileObj["last_refresh"] = DateTimeOffset.UtcNow.ToString("o");
		WriteAtomic(path, fileObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
		return newAccess;
	}

	static void WriteAtomic(string path, string content) {
		var dir = Path.GetDirectoryName(path) ?? ".";
		var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		File.WriteAllText(tmp, content);
		try {
			if(!OperatingSystem.IsWindows()) {
				try { File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best-effort 0600 */ }
			}
			File.Move(tmp, path, overwrite: true);
		} catch {
			try { File.Delete(tmp); } catch { /* ignore */ }
			throw;
		}
	}

	static Dictionary<string, string> DecodeJwt(string token) {
		var result = new Dictionary<string, string>(StringComparer.Ordinal);
		var parts = token.Split('.');
		if(parts.Length < 2) return result;
		try {
			var json = Base64UrlDecode(parts[1]);
			using var doc = JsonDocument.Parse(json);
			foreach(var prop in doc.RootElement.EnumerateObject()) {
				if(prop.Value.ValueKind == JsonValueKind.Number) result[prop.Name] = prop.Value.GetRawText();
				else if(prop.Value.ValueKind == JsonValueKind.String) result[prop.Name] = prop.Value.GetString()!;
			}
		} catch {
			// Malformed JWT: no claims available.
		}
		return result;
	}

	static string Base64UrlDecode(string s) {
		var t = s.Replace('-', '+').Replace('_', '/');
		t = (t.Length % 4) switch { 2 => t + "==", 3 => t + "=", _ => t };
		return Encoding.UTF8.GetString(Convert.FromBase64String(t));
	}

	public JsonObject BuildRequest(ProviderRequest request) {
		var body = new JsonObject {
			["model"] = request.Model.UpstreamModel,
			["stream"] = true,
			["store"] = false,
			["reasoning"] = new JsonObject { ["effort"] = MapEffort(request.ReasoningEffort) },
		};
		var instructions = string.Join("\n\n", request.Messages.Where(m => m.Role == "system").Select(m => m.Content).Where(c => !string.IsNullOrEmpty(c)));
		if(instructions.Length > 0) body["instructions"] = instructions;

		var input = new JsonArray();
		foreach(var m in request.Messages) {
			switch(m.Role) {
				case "system":
					break;
				case "tool":
					input.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = m.ToolCallId, ["output"] = m.Content ?? "" });
					break;
				case "assistant" when m.ToolCalls is { Count: > 0 }:
					if(!string.IsNullOrEmpty(m.Content)) input.Add(MessageItem("assistant", "output_text", m.Content!));
					foreach(var call in m.ToolCalls)
						input.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments });
					break;
				case "assistant":
					if(!string.IsNullOrEmpty(m.Content)) input.Add(MessageItem("assistant", "output_text", m.Content!));
					break;
				default: // user
					input.Add(MessageItem("user", "input_text", m.Content ?? ""));
					break;
			}
		}
		body["input"] = input;

		if(request.Tools.Count > 0) {
			body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode) new JsonObject {
				["type"] = "function",
				["name"] = t.Name,
				["description"] = t.Description,
				["parameters"] = t.InputSchema.DeepClone(),
				["strict"] = false,
			}).ToArray());
		}
		return body;
	}

	static JsonObject MessageItem(string role, string textType, string text) => new() {
		["type"] = "message",
		["role"] = role,
		["content"] = new JsonArray(new JsonObject { ["type"] = textType, ["text"] = text }),
	};

	public static string MapEffort(string? effort) => effort switch {
		"none" => "none",
		"minimal" => "minimal",
		"low" => "low",
		"medium" => "medium",
		"high" or "xhigh" or "max" or "ultra" => "high",
		_ => "medium",
	};

	async Task<ProviderResponse> SendAsync(string endpoint, ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct, Action<HttpRequestMessage> applyHeaders) {
		var body = BuildRequest(request);
		var rawRequest = body.ToJsonString();
		using var msg = new HttpRequestMessage(HttpMethod.Post, endpoint) {
			Content = new StringContent(rawRequest, Encoding.UTF8, "application/json"),
		};
		msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		applyHeaders(msg);

		HttpResponseMessage resp;
		try {
			resp = await http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
		} catch(HttpRequestException e) {
			throw new OpenAiFailoverException($"{Id} transport error: {e.Message}");
		}
		using var _ = resp;
		if(!resp.IsSuccessStatusCode) {
			var error = await resp.Content.ReadAsStringAsync(ct);
			var status = (int) resp.StatusCode;
			throw new ProviderException($"{Id} HTTP {status}: {TextUtil.Truncate(error, 2000)}", status, retryable: status is 429 or >= 500);
		}

		var result = new ProviderResponse { RawRequest = rawRequest };
		var content = new StringBuilder();
		var reasoning = new StringBuilder();
		var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
		var byItemId = new Dictionary<string, int>();
		var raw = new StringBuilder();
		var sawUsage = false;
		var sawCompleted = false;
		try {
			using var stream = await resp.Content.ReadAsStreamAsync(ct);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			while(await reader.ReadLineAsync(ct) is { } line) {
				if(!line.StartsWith("data:")) continue;
				var data = line[5..].Trim();
				if(data.Length == 0 || data == "[DONE]") continue;
				raw.Append(data).Append('\n');
				using var doc = JsonDocument.Parse(data);
				var root = doc.RootElement;
				if(!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String) continue;
				var type = typeEl.GetString();
				switch(type) {
					case "response.output_text.delta":
						if(root.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String) {
							var text = d.GetString()!;
							content.Append(text);
							onDelta?.Invoke(new("content", text));
						}
						break;
					case "response.reasoning_summary_text.delta":
						if(root.TryGetProperty("delta", out var rd) && rd.ValueKind == JsonValueKind.String) {
							var rtext = rd.GetString()!;
							reasoning.Append(rtext);
							onDelta?.Invoke(new("reasoning", rtext));
						}
						break;
					case "response.function_call_arguments.delta":
						if(root.TryGetProperty("delta", out var ad) && ad.ValueKind == JsonValueKind.String) {
							var index = IndexFor(root, byItemId, calls);
							if(!calls.TryGetValue(index, out var entry)) entry = ("", "", new StringBuilder());
							entry.Args.Append(ad.GetString());
							calls[index] = entry;
						}
						break;
					case "response.output_item.added":
						if(root.TryGetProperty("item", out var added) && added.ValueKind == JsonValueKind.Object)
							RegisterItem(added, root, byItemId, calls);
						break;
					case "response.output_item.done":
						if(root.TryGetProperty("item", out var done) && done.ValueKind == JsonValueKind.Object)
							RegisterItem(done, root, byItemId, calls);
						break;
					case "response.created":
						if(root.TryGetProperty("response", out var created) && created.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String)
							result.UpstreamModel = model.GetString();
						break;
					case "response.completed":
						sawCompleted = true;
						if(root.TryGetProperty("response", out var completed) && completed.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object) {
							sawUsage = true;
							result.Usage = ParseUsage(usage);
						}
						break;
					case "response.failed": {
						var reportedUsage = root.TryGetProperty("response", out var failedResp) && failedResp.TryGetProperty("usage", out var fu) && fu.ValueKind == JsonValueKind.Object;
						throw new ProviderException($"{Id} response failed: {TextUtil.Truncate(data, 2000)}", retryable: false, mayHaveBilled: reportedUsage);
					}
					case "error":
						throw new ProviderException($"{Id} error event: {TextUtil.Truncate(data, 2000)}", retryable: false, mayHaveBilled: false);
					default:
						break;
				}
			}
		} catch(ProviderException) {
			throw;
		} catch(Exception e) when(e is IOException or HttpRequestException or JsonException) {
			throw new ProviderException($"{Id} stream interrupted: {e.Message}", retryable: true, mayHaveBilled: true);
		}

		if(!sawCompleted)
			throw new ProviderException($"{Id} stream ended without response.completed", retryable: true, mayHaveBilled: true);

		result.Content = content.Length > 0 ? content.ToString() : null;
		result.Reasoning = reasoning.Length > 0 ? reasoning.ToString() : null;
		result.ToolCalls = calls.Values.Select((c, i) => new ToolCall(string.IsNullOrEmpty(c.Id) ? $"call_{i}" : c.Id, c.Name, c.Args.Length == 0 ? "{}" : c.Args.ToString())).ToList();
		result.FinishReason = result.ToolCalls.Count > 0 ? "tool_calls" : "stop";
		result.RawResponse = raw.ToString();
		if(!sawUsage) result.Usage = new Usage { Reported = false };
		return result;
	}

	static int IndexFor(JsonElement root, Dictionary<string, int> byItemId, SortedDictionary<int, (string, string, StringBuilder)> calls) {
		if(root.TryGetProperty("item_id", out var ii) && ii.ValueKind == JsonValueKind.String && byItemId.TryGetValue(ii.GetString()!, out var idx))
			return idx;
		if(root.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number)
			return oi.GetInt32();
		return calls.Count;
	}

	static void RegisterItem(JsonElement item, JsonElement evt, Dictionary<string, int> byItemId, SortedDictionary<int, (string Id, string Name, StringBuilder Args)> calls) {
		if(!item.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String || t.GetString() != "function_call") return;
		var itemId = item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : "";
		var callId = item.TryGetProperty("call_id", out var cid) && cid.ValueKind == JsonValueKind.String ? cid.GetString()! : "";
		var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
		var args = item.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString()! : "";
		int index;
		if(itemId.Length > 0 && byItemId.TryGetValue(itemId, out var existing))
			index = existing;
		else if(evt.TryGetProperty("output_index", out var oi) && oi.ValueKind == JsonValueKind.Number)
			index = oi.GetInt32();
		else
			index = calls.Count;
		if(!calls.TryGetValue(index, out var entry)) entry = (callId, name, new StringBuilder());
		if(callId.Length > 0) entry.Id = callId;
		if(name.Length > 0) entry.Name = name;
		if(args.Length > 0) entry.Args = new StringBuilder(args);
		calls[index] = entry;
		if(itemId.Length > 0) byItemId[itemId] = index;
	}

	static Usage ParseUsage(JsonElement u) {
		long Get(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
		var usage = new Usage {
			InputTokens = Get(u, "input_tokens"),
			OutputTokens = Get(u, "output_tokens"),
		};
		if(u.TryGetProperty("input_tokens_details", out var itd) && itd.ValueKind == JsonValueKind.Object)
			usage.CachedInputTokens = Get(itd, "cached_tokens");
		if(u.TryGetProperty("output_tokens_details", out var otd) && otd.ValueKind == JsonValueKind.Object)
			usage.ReasoningTokens = Get(otd, "reasoning_tokens");
		return usage;
	}

	sealed record SubscriptionCredential(string AccessToken, string AccountId);

	/// <summary>Marks a subscription-route failure eligible for a same-attempt api fallback (no usage reported).</summary>
	sealed class OpenAiFailoverException : ProviderException {
		public OpenAiFailoverException(string message) : base(message, null, retryable: true, mayHaveBilled: false) { }
	}
}

public static class OpenAiProvider {
	public static OpenAiResponsesProvider CreateDefault() =>
		new(new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
}
