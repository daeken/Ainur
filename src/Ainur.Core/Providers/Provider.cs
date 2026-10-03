using System.Text.Json.Nodes;
using Ainur.Core.Model;

namespace Ainur.Core.Providers;

public sealed record ToolSpec(string Name, string Description, JsonObject InputSchema);

public sealed record ToolCall(string Id, string Name, string Arguments);

/// <summary>
/// Provider-neutral message. Native fields (reasoning text, call identifiers) are carried through so adapters
/// can rebuild valid native requests without discarding provider-required content.
/// </summary>
public sealed class ChatMessage {
	public string Role { get; set; } = "user"; // system | user | assistant | tool
	public string? Content { get; set; }
	public string? Reasoning { get; set; }
	public List<ToolCall>? ToolCalls { get; set; }
	public string? ToolCallId { get; set; }

	public static ChatMessage System(string text) => new() { Role = "system", Content = text };
	public static ChatMessage User(string text) => new() { Role = "user", Content = text };
	public static ChatMessage Assistant(string? text, string? reasoning = null, List<ToolCall>? calls = null) => new() { Role = "assistant", Content = text, Reasoning = reasoning, ToolCalls = calls };
	public static ChatMessage Tool(string callId, string text) => new() { Role = "tool", ToolCallId = callId, Content = text };
}

/// <summary>A single dispatch's OpenAI route policy; Unknown must never admit a transport.</summary>
public enum OpenAiRoutePolicy { Unknown, Auto, Subscription, Api }

public sealed class ProviderRequest {
	public required ModelInfo Model { get; init; }
	public required List<ChatMessage> Messages { get; init; }
	public List<ToolSpec> Tools { get; init; } = [];
	public int MaxOutputTokens { get; init; } = 8192;
	public string? ReasoningEffort { get; init; }
	public string? ToolChoice { get; init; }
	/// <summary>Opt in to native OpenAI web search; retrieved pages count toward reported input usage.</summary>
	public bool EnableWebSearch { get; init; }
	/// <summary>Gateway-captured route, checked again against provider configuration before transport.</summary>
	public OpenAiRoutePolicy? OpenAiRoutePolicy { get; init; }
}

public sealed class Usage {
	public long InputTokens { get; set; }
	public long CachedInputTokens { get; set; }
	public long OutputTokens { get; set; }
	public long ReasoningTokens { get; set; }
	public bool Reported { get; set; } = true;
}

public sealed class ProviderResponse {
	public string? Content { get; set; }
	public string? Reasoning { get; set; }
	public List<ToolCall> ToolCalls { get; set; } = [];
	public string? FinishReason { get; set; }
	public Usage Usage { get; set; } = new();
	public string? UpstreamModel { get; set; }
	public List<JsonObject> WebSearchCalls { get; set; } = [];
	public List<JsonObject> Annotations { get; set; } = [];
	public string RawRequest { get; set; } = "";
	public string RawResponse { get; set; } = "";
}

public sealed record StreamDelta(string Kind, string Text);

public class ProviderException(string message, int? status = null, bool retryable = false, bool mayHaveBilled = false) : Exception(message) {
	public int? Status => status;
	public bool Retryable => retryable;
	/// <summary>True when the request may have been processed upstream, so its usage is uncertain rather than zero.</summary>
	public bool MayHaveBilled => mayHaveBilled;
}

public interface IModelProvider {
	string Id { get; }
	Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct);
}

public sealed class ProviderRegistry {
	readonly Dictionary<string, IModelProvider> Providers = new(StringComparer.OrdinalIgnoreCase);

	public void Register(IModelProvider provider) => Providers[provider.Id] = provider;
	public bool Has(string id) => Providers.ContainsKey(id);
	public IModelProvider Get(string id) => Providers.TryGetValue(id, out var p) ? p
		: throw new ProviderException($"No adapter is configured for provider '{id}'. Implemented providers: {string.Join(", ", Providers.Keys)}");
	public IEnumerable<string> Ids => Providers.Keys;
}
