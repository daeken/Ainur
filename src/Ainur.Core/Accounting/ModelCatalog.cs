using Ainur.Core.Model;
using Ainur.Core.Persistence;

namespace Ainur.Core.Accounting;

/// <summary>Initial model inventory seeded from docs/model-catalog.md (FlatlineProxy configuration, October 1, 2026).</summary>
public static class ModelCatalog {
	const string Flatline = "FlatlineProxy local config 2026-10-01 (unverified estimate)";
	const string Assumed = "assumed conservative reference schedule (no validated API price)";

	static ModelInfo M(string id, string provider, string upstream, string name, int? ctx, string? input, string? cached, string? output, string billing, bool enabled, string notes = "", int? maxOut = null, string premium = "1", string? provenance = null, string? fallback = null) => new() {
		Id = id, Provider = provider, UpstreamModel = upstream, DisplayName = name, ContextTokens = ctx, MaxOutputTokens = maxOut,
		InputPerMillion = input, CachedInputPerMillion = cached, OutputPerMillion = output,
		PriceProvenance = provenance ?? (input is null ? "none" : Flatline), Billing = billing, Premium = premium, Enabled = enabled, Notes = notes, FallbackModelId = fallback,
	};

	public static readonly IReadOnlyList<ModelInfo> Seed = [
		// Single-model policy (2026-10-01): the owner reports the upstream route answers both the V4 Pro and V4 Flash ids
		// as deepseek-flash. Only deepseek-v4.1-flash is assigned; the retired rows stay (disabled) so historical
		// model_requests keep a catalog reference and their recorded prices.
		M("deepseek-v4-pro", "deepseek", "deepseek-v4-pro", "DeepSeek V4 Pro", 1_000_000, "0.435", "0.003625", "0.87", "api", false, "Retired 2026-10-01: owner reports the upstream route serves this id as deepseek-v4.1-flash. Historical model_requests only; no new assignments.", 393_216),
		M("deepseek-v4-flash", "deepseek", "deepseek-v4-flash", "DeepSeek V4 Flash", 1_000_000, "0.14", "0.0028", "0.28", "api", false, "Retired 2026-10-01: upstream answers this id as deepseek-flash, duplicating deepseek-v4.1-flash at stale prices. Historical model_requests only; no new assignments.", 393_216),
		M("deepseek-v4.1-flash", "deepseek", "deepseek-flash", "DeepSeek V4.1 Flash", 1_000_000, "0.30", "0.006", "1.20", "api", true, "Live-verified via /models on 2026-10-01. Sole DeepSeek route for new assignments since 2026-10-01.", 393_216),
		// OpenAI subscription rows (primary, free/cash=0) each with a billing=api `-api` twin as the model-level fallback.
		// No public API price exists for these slugs, so every openai row keeps null rates: subscription rows settle
		// cash=0 via Pricing.Settle, and the `-api` twins settle cash_basis=unknown (the honest answer — never a guessed number).
		M("gpt-6", "openai", "gpt-6", "GPT 6", null, null, null, null, "subscription", true, "Responses API via chatgpt.com/backend-api/codex/responses (subscription, cash=0). Falls back to gpt-6-api on non-billed failure.", fallback: "gpt-6-api"),
		M("gpt-6-astra", "openai", "gpt-6-astra", "GPT 6 Astra", null, null, null, null, "subscription", true, "Responses API via chatgpt.com/backend-api/codex/responses (subscription, cash=0); live-verified 2026-10-01. Falls back to gpt-6-astra-api on non-billed failure.", fallback: "gpt-6-astra-api"),
		M("gpt-5.6-sol", "openai", "gpt-5.6-sol", "GPT 5.6 Sol", null, null, null, null, "subscription", true, "Responses API via chatgpt.com/backend-api/codex/responses (subscription, cash=0). Falls back to gpt-5.6-sol-api on non-billed failure.", fallback: "gpt-5.6-sol-api"),
		M("gpt-5.6-terra", "openai", "gpt-5.6-terra", "GPT 5.6 Terra", null, null, null, null, "subscription", true, "Responses API via chatgpt.com/backend-api/codex/responses (subscription, cash=0). Falls back to gpt-5.6-terra-api on non-billed failure.", fallback: "gpt-5.6-terra-api"),
		M("gpt-5.6-luna", "openai", "gpt-5.6-luna", "GPT 5.6 Luna", null, null, null, null, "subscription", true, "Responses API via chatgpt.com/backend-api/codex/responses (subscription, cash=0). Falls back to gpt-5.6-luna-api on non-billed failure.", fallback: "gpt-5.6-luna-api"),
		M("gpt-6-sol", "openai", "gpt-6-sol", "GPT 6 Sol", null, null, null, null, "subscription", true, "Direct Responses subscription route; exact gpt-6-sol slug live-verified 2026-10-01. Worker route per owner policy; cash=0, fallback to gpt-6-sol-api on eligible non-billed failure.", fallback: "gpt-6-sol-api"),
		M("gpt-6.1-sol", "openai", "gpt-6.1-sol", "GPT 6.1 Sol", null, null, null, null, "subscription", true, "Responses API via chatgpt.com/backend-api/codex/responses (subscription, cash=0); raw/mechanical-work subordinate route per owner policy 2026-10-01. Falls back to gpt-6.1-sol-api on non-billed failure.", fallback: "gpt-6.1-sol-api"),
		M("gpt-6-api", "openai", "gpt-6", "GPT 6 (API)", null, null, null, null, "api", true, "API-key route (api.openai.com/v1/responses). No public price published for this slug; rates left null so Pricing.Settle records cash_basis=unknown."),
		M("gpt-6-astra-api", "openai", "gpt-6-astra", "GPT 6 Astra (API)", null, null, null, null, "api", true, "API-key route (api.openai.com/v1/responses). No public price published for this slug; rates left null so Pricing.Settle records cash_basis=unknown."),
		M("gpt-5.6-sol-api", "openai", "gpt-5.6-sol", "GPT 5.6 Sol (API)", null, null, null, null, "api", true, "API-key route (api.openai.com/v1/responses). No public price published for this slug; rates left null so Pricing.Settle records cash_basis=unknown."),
		M("gpt-5.6-terra-api", "openai", "gpt-5.6-terra", "GPT 5.6 Terra (API)", null, null, null, null, "api", true, "API-key route (api.openai.com/v1/responses). No public price published for this slug; rates left null so Pricing.Settle records cash_basis=unknown."),
		M("gpt-5.6-luna-api", "openai", "gpt-5.6-luna", "GPT 5.6 Luna (API)", null, null, null, null, "api", true, "API-key route (api.openai.com/v1/responses). No public price published for this slug; rates left null so Pricing.Settle records cash_basis=unknown."),
		M("gpt-6-sol-api", "openai", "gpt-6-sol", "GPT 6 Sol (API)", null, null, null, null, "api", true, "API-key route (api.openai.com/v1/responses); not live-verified. No verified public price sourced; null rates preserve cash_basis=unknown."),
		M("gpt-6.1-sol-api", "openai", "gpt-6.1-sol", "GPT 6.1 Sol (API)", null, null, null, null, "api", true, "API-key route (api.openai.com/v1/responses). Slug confirmed present on the platform API 2026-10-01; no public price published, so rates left null so Pricing.Settle records cash_basis=unknown."),
		M("claude-fable-5", "anthropic", "claude-fable-5", "Claude Fable 5", 1_000_000, "10.00", null, "50.00", "subscription", false, "Adapter not implemented yet; API price is the disabled API route, used as subscription reference."),
		M("claude-opus-5", "anthropic", "claude-opus-5", "Claude Opus 5", 1_000_000, "5.00", null, "25.00", "subscription", false, "Adapter not implemented yet."),
		M("claude-opus-4-8", "anthropic", "claude-opus-4-8", "Claude Opus 4.8", 1_000_000, "5.00", null, "25.00", "subscription", false, "Adapter not implemented yet."),
		M("claude-sonnet-5", "anthropic", "claude-sonnet-5", "Claude Sonnet 5", 1_000_000, "3.00", null, "15.00", "subscription", false, "Adapter not implemented yet."),
		M("claude-haiku-4-5", "anthropic", "claude-haiku-4-5", "Claude Haiku 4.5", 200_000, "1.00", null, "5.00", "subscription", false, "Adapter not implemented yet."),
		M("grok-4.5", "xai", "grok-4.5", "Grok 4.5", 500_000, null, null, null, "api", false, "Adapter not implemented yet."),
		// GLM through the GLM Coding Plan subscription endpoint. No validated API price exists, so these are an explicitly
		// configured conservative reference schedule for effective-dollar valuation, with a subscription premium.
		M("glm-5.1", "zai", "glm-5.1", "GLM 5.1", 204_800, "1.50", "0.30", "6.00", "subscription", true, "Coding Plan subscription; reference prices are assumed, unverified.", 131_072, premium: "1.25", provenance: Assumed),
		M("glm-5.2", "zai", "glm-5.2", "GLM 5.2", 1_048_576, "1.50", "0.30", "6.00", "subscription", true, "Coding Plan subscription; reference prices are assumed, unverified.", 131_072, premium: "1.25", provenance: Assumed),
		M("glm-5.3", "zai", "glm-5.3", "GLM 5.3", 1_048_576, "1.50", "0.30", "6.00", "subscription", true, "Coding Plan subscription; live-verified 2026-10-01. Reference prices are assumed, unverified.", 131_072, premium: "1.25", provenance: Assumed),
	];

	public static void EnsureSeeded(Store store) => store.Db.Write(u => {
		foreach(var m in Seed) store.UpsertModel(u, m);
	});
}
