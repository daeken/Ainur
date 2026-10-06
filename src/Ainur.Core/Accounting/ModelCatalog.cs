using Ainur.Core.Model;
using Ainur.Core.Persistence;

namespace Ainur.Core.Accounting;

/// <summary>Initial model inventory described in docs/model-catalog.md; rates require independent validation.</summary>
public static class ModelCatalog {
	const string SeedEstimate = "seeded catalog 2026-10-01 (unverified estimate)";
	const string Assumed = "assumed conservative reference schedule (no validated API price)";

	static ModelInfo M(string id, string provider, string upstream, string name, int? ctx, string? input, string? cached, string? output, string billing, bool enabled, string notes = "", int? maxOut = null, string premium = "1", string? provenance = null, string? fallback = null) => new() {
		Id = id, Provider = provider, UpstreamModel = upstream, DisplayName = name, ContextTokens = ctx, MaxOutputTokens = maxOut,
		InputPerMillion = input, CachedInputPerMillion = cached, OutputPerMillion = output,
		PriceProvenance = provenance ?? (input is null ? "none" : SeedEstimate), Billing = billing, Premium = premium, Enabled = enabled, Notes = notes, FallbackModelId = fallback,
	};

	public static readonly IReadOnlyList<ModelInfo> Seed = [
		// AWS cash prices remain unknown until the catalog/accounting model supports tiered context and cache-write rates.
		// These routes use normal API accounting (never zero-marginal subscription billing).
		M("bedrock-gpt-6.1-sol", "bedrock", "us.openai.gpt-6.1-sol", "GPT 6.1 Sol (Bedrock US)", 1_000_000, null, null, null, "api", true, "AWS SigV4 Responses; US inference profile. Requires AINUR_BEDROCK_ENABLED=1. Tiered cash pricing not configured.", 131_072),
		M("bedrock-gpt-6-astra", "bedrock", "us.openai.gpt-6-astra", "GPT 6 Astra (Bedrock US)", 1_050_000, null, null, null, "api", true, "AWS SigV4 Responses; US inference profile. Requires AINUR_BEDROCK_ENABLED=1. Tiered cash pricing not configured.", 128_000),
		M("mantle-gpt-6.1-sol", "bedrock-mantle", "openai.gpt-6.1-sol", "GPT 6.1 Sol (Mantle)", 1_000_000, null, null, null, "api", true, "AWS SigV4 Responses; requires Mantle in us-east-1. Tiered cash pricing not configured.", 131_072),
		M("mantle-gpt-6-astra", "bedrock-mantle", "openai.gpt-6-astra", "GPT 6 Astra (Mantle)", 1_050_000, null, null, null, "api", true, "AWS SigV4 Responses; Mantle in us-east-1 or us-west-2. Tiered cash pricing not configured.", 128_000),
		// Single-model policy (2026-10-01): only deepseek-v4.1-flash is assigned.
		// The retired rows stay (disabled) so historical
		// model_requests keep a catalog reference and their recorded prices.
		M("deepseek-v4-pro", "deepseek", "deepseek-v4-pro", "DeepSeek V4 Pro", 1_000_000, "0.435", "0.003625", "0.87", "api", false, "Retired 2026-10-01: historical model_requests only; no new assignments. Use the enabled deepseek-v4.1-flash catalog route.", 393_216),
		M("deepseek-v4-flash", "deepseek", "deepseek-v4-flash", "DeepSeek V4 Flash", 1_000_000, "0.14", "0.0028", "0.28", "api", false, "Retired 2026-10-01: historical model_requests only; no new assignments. Use the enabled deepseek-v4.1-flash catalog route.", 393_216),
		M("deepseek-v4.1-flash", "deepseek", "deepseek-flash", "DeepSeek V4.1 Flash", 1_000_000, "0.30", "0.006", "1.20", "api", true, "Sole DeepSeek route for new assignments since 2026-10-01. Verify availability for each deployment.", 393_216),
		// OpenAI subscription-only routes have no paid fallback. Disabled API twins remain for
		// historical references; null price rates preserve honest unknown cash for past API usage.
		M("gpt-6", "openai", "gpt-6", "GPT 6", null, null, null, null, "subscription", true, "Subscription-only Responses route (cash=0). Paid/API-key transport prohibited; failures remain on this route."),
		M("gpt-6-astra", "openai", "gpt-6-astra", "GPT 6 Astra", null, null, null, null, "subscription", true, "Subscription-only Responses route (cash=0). Paid/API-key transport prohibited; failures remain on this route."),
		M("gpt-5.6-sol", "openai", "gpt-5.6-sol", "GPT 5.6 Sol", null, null, null, null, "subscription", true, "Subscription-only Responses route (cash=0). Paid/API-key transport prohibited; failures remain on this route."),
		M("gpt-5.6-terra", "openai", "gpt-5.6-terra", "GPT 5.6 Terra", null, null, null, null, "subscription", true, "Subscription-only Responses route (cash=0). Paid/API-key transport prohibited; failures remain on this route."),
		M("gpt-5.6-luna", "openai", "gpt-5.6-luna", "GPT 5.6 Luna", null, null, null, null, "subscription", true, "Subscription-only Responses route (cash=0). Paid/API-key transport prohibited; failures remain on this route."),
		M("gpt-6-sol", "openai", "gpt-6-sol", "GPT 6 Sol", null, null, null, null, "subscription", true, "Subscription-only Responses route (cash=0). Paid/API-key transport prohibited; failures remain on this route."),
		M("gpt-6.1-sol", "openai", "gpt-6.1-sol", "GPT 6.1 Sol", null, null, null, null, "subscription", true, "Subscription-only Responses route (cash=0). Paid/API-key transport prohibited; failures remain on this route."),
		M("gpt-6-api", "openai", "gpt-6", "GPT 6 (API)", null, null, null, null, "api", false, "Historical-only API row retained for past model requests and cost records. Paid OpenAI API route prohibited for new assignments; no verified API price, so null rates preserve cash_basis=unknown."),
		M("gpt-6-astra-api", "openai", "gpt-6-astra", "GPT 6 Astra (API)", null, null, null, null, "api", false, "Historical-only API row retained for past model requests and cost records. Paid OpenAI API route prohibited for new assignments; no verified API price, so null rates preserve cash_basis=unknown."),
		M("gpt-5.6-sol-api", "openai", "gpt-5.6-sol", "GPT 5.6 Sol (API)", null, null, null, null, "api", false, "Historical-only API row retained for past model requests and cost records. Paid OpenAI API route prohibited for new assignments; no verified API price, so null rates preserve cash_basis=unknown."),
		M("gpt-5.6-terra-api", "openai", "gpt-5.6-terra", "GPT 5.6 Terra (API)", null, null, null, null, "api", false, "Historical-only API row retained for past model requests and cost records. Paid OpenAI API route prohibited for new assignments; no verified API price, so null rates preserve cash_basis=unknown."),
		M("gpt-5.6-luna-api", "openai", "gpt-5.6-luna", "GPT 5.6 Luna (API)", null, null, null, null, "api", false, "Historical-only API row retained for past model requests and cost records. Paid OpenAI API route prohibited for new assignments; no verified API price, so null rates preserve cash_basis=unknown."),
		M("gpt-6-sol-api", "openai", "gpt-6-sol", "GPT 6 Sol (API)", null, null, null, null, "api", false, "Historical-only API row retained for past model requests and cost records. Paid OpenAI API route prohibited for new assignments; no verified API price, so null rates preserve cash_basis=unknown."),
		M("gpt-6.1-sol-api", "openai", "gpt-6.1-sol", "GPT 6.1 Sol (API)", null, null, null, null, "api", false, "Historical-only API row retained for past model requests and cost records. Paid OpenAI API route prohibited for new assignments; no verified API price, so null rates preserve cash_basis=unknown."),
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
		M("glm-5.3", "zai", "glm-5.3", "GLM 5.3", 1_048_576, "1.50", "0.30", "6.00", "subscription", true, "Coding Plan subscription. Reference prices are assumed, unverified; verify availability for each deployment.", 131_072, premium: "1.25", provenance: Assumed),
	];

	public static void EnsureSeeded(Store store) => store.Db.Write(u => {
		foreach(var m in Seed) store.UpsertModel(u, m);
	});
}
