using Ainur.Core.Model;
using Ainur.Core.Persistence;

namespace Ainur.Core.Accounting;

/// <summary>Initial model inventory seeded from docs/model-catalog.md (FlatlineProxy configuration, October 1, 2026).</summary>
public static class ModelCatalog {
	const string Flatline = "FlatlineProxy local config 2026-10-01 (unverified estimate)";

	static ModelInfo M(string id, string provider, string upstream, string name, int? ctx, string? input, string? cached, string? output, string billing, bool enabled, string notes = "", int? maxOut = null) => new() {
		Id = id, Provider = provider, UpstreamModel = upstream, DisplayName = name, ContextTokens = ctx, MaxOutputTokens = maxOut,
		InputPerMillion = input, CachedInputPerMillion = cached, OutputPerMillion = output,
		PriceProvenance = input is null ? "none" : Flatline, Billing = billing, Premium = "1", Enabled = enabled, Notes = notes,
	};

	public static readonly IReadOnlyList<ModelInfo> Seed = [
		M("deepseek-v4-pro", "deepseek", "deepseek-v4-pro", "DeepSeek V4 Pro", 1_000_000, "0.435", "0.003625", "0.87", "api", true, "Live-verified via /models on 2026-10-01.", 393_216),
		M("deepseek-v4-flash", "deepseek", "deepseek-v4-flash", "DeepSeek V4 Flash", 1_000_000, "0.14", "0.0028", "0.28", "api", true, "Upstream currently answers as deepseek-flash; prices are the configured V4 Flash route.", 393_216),
		M("deepseek-v4.1-flash", "deepseek", "deepseek-flash", "DeepSeek V4.1 Flash", 1_000_000, "0.30", "0.006", "1.20", "api", true, "Live-verified via /models on 2026-10-01.", 393_216),
		M("gpt-6", "openai", "gpt-6", "GPT 6", null, null, null, null, "subscription", false, "Adapter not implemented yet."),
		M("gpt-6-astra", "openai", "gpt-6-astra", "GPT 6 Astra", null, null, null, null, "subscription", false, "Adapter not implemented yet."),
		M("gpt-5.6-sol", "openai", "gpt-5.6-sol", "GPT 5.6 Sol", null, null, null, null, "subscription", false, "Adapter not implemented yet."),
		M("gpt-5.6-terra", "openai", "gpt-5.6-terra", "GPT 5.6 Terra", null, null, null, null, "subscription", false, "Adapter not implemented yet."),
		M("gpt-5.6-luna", "openai", "gpt-5.6-luna", "GPT 5.6 Luna", null, null, null, null, "subscription", false, "Adapter not implemented yet."),
		M("claude-fable-5", "anthropic", "claude-fable-5", "Claude Fable 5", 1_000_000, "10.00", null, "50.00", "subscription", false, "Adapter not implemented yet; API price is the disabled API route, used as subscription reference."),
		M("claude-opus-5", "anthropic", "claude-opus-5", "Claude Opus 5", 1_000_000, "5.00", null, "25.00", "subscription", false, "Adapter not implemented yet."),
		M("claude-opus-4-8", "anthropic", "claude-opus-4-8", "Claude Opus 4.8", 1_000_000, "5.00", null, "25.00", "subscription", false, "Adapter not implemented yet."),
		M("claude-sonnet-5", "anthropic", "claude-sonnet-5", "Claude Sonnet 5", 1_000_000, "3.00", null, "15.00", "subscription", false, "Adapter not implemented yet."),
		M("claude-haiku-4-5", "anthropic", "claude-haiku-4-5", "Claude Haiku 4.5", 200_000, "1.00", null, "5.00", "subscription", false, "Adapter not implemented yet."),
		M("grok-4.5", "xai", "grok-4.5", "Grok 4.5", 500_000, null, null, null, "api", false, "Adapter not implemented yet."),
		M("glm-5.1", "zai", "glm-5.1", "GLM 5.1", 204_800, null, null, null, "api", false, "Adapter not implemented yet."),
		M("glm-5.2", "zai", "glm-5.2", "GLM 5.2", 1_048_576, null, null, null, "api", false, "Adapter not implemented yet."),
		M("glm-5.3", "zai", "glm-5.3", "GLM 5.3", 1_048_576, null, null, null, "api", false, "Adapter not implemented yet."),
	];

	public static void EnsureSeeded(Store store) => store.Db.Write(u => {
		foreach(var m in Seed) store.UpsertModel(u, m);
	});
}
