using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Xunit.Abstractions;

namespace Ainur.Tests;

/// <summary>
/// Live smoke test against the real ChatGPT subscription endpoint (chatgpt.com/backend-api/codex/responses).
/// Skipped unless a Codex credential file exists (~/.codex/auth.json or AINUR_CODEX_AUTH). CI stays offline: the
/// test is skipped by default and consumes subscription quota only when explicitly run with credentials present.
/// </summary>
[Trait("Category", "Live")]
public class LiveOpenAiTests(ITestOutputHelper output) {
	static string? AuthPath() =>
		Environment.GetEnvironmentVariable("AINUR_CODEX_AUTH") is { Length: > 0 } p
			? p
			: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

	static readonly bool HasSubscription = File.Exists(AuthPath());

	[SkippableFact]
	public async Task SubscriptionSmokeTest() {
		Skip.IfNot(HasSubscription, "no Codex credential file");
		var provider = new OpenAiResponsesProvider(new HttpClient { Timeout = TimeSpan.FromMinutes(30) }, routeOverride: "subscription");
		var model = new ModelInfo { Id = "gpt-6-astra", Provider = "openai", UpstreamModel = "gpt-6-astra", Billing = "subscription", Enabled = true };
		var r = await provider.CompleteAsync(new ProviderRequest {
			Model = model,
			Messages = [ChatMessage.System("You are a helpful assistant."), ChatMessage.User("Reply with exactly: PROBE_OK")],
			ReasoningEffort = "low",
		}, null, default);
		output.WriteLine($"content={r.Content} upstream={r.UpstreamModel} finish={r.FinishReason} usage={JsonUtil.Serialize(r.Usage)}");
		Assert.Contains("PROBE_OK", r.Content);
		Assert.True(r.Usage.InputTokens > 0);
		Assert.True(r.Usage.Reported);
	}
}
