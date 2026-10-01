using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Tools;
using Xunit.Abstractions;

namespace Ainur.Tests;

/// <summary>Live checks against Z.ai (GLM Coding Plan subscription). Kept small: they consume subscription quota.</summary>
[Trait("Category", "Live")]
public class LiveZaiTests(ITestOutputHelper output) {
	static readonly bool HasKey = Credentials.Resolve("zai") is not null && Credentials.Resolve("deepseek") is not null;
	static ModelInfo Glm => Ainur.Core.Accounting.ModelCatalog.Seed.First(m => m.Id == "glm-5.3");

	[SkippableFact]
	public async Task GlmToolCallRoundTrip() {
		Skip.IfNot(HasKey, "no Z.ai credential");
		var provider = ZaiProvider.CreateDefault();
		var tool = new ToolSpec("get_weather", "Get the weather for a city.", Schema.Object(("city", Schema.String("City"), true)));
		var messages = new List<ChatMessage> { ChatMessage.System("Use tools when helpful."), ChatMessage.User("What's the weather in Oslo? Use the tool.") };
		var r1 = await provider.CompleteAsync(new ProviderRequest { Model = Glm, Messages = messages, Tools = [tool], MaxOutputTokens = 2000 }, null, default);
		output.WriteLine($"r1 finish={r1.FinishReason} calls={r1.ToolCalls.Count} reasoning={r1.Reasoning?.Length} usage={JsonUtil.Serialize(r1.Usage)} model={r1.UpstreamModel}");
		Assert.NotEmpty(r1.ToolCalls);
		messages.Add(ChatMessage.Assistant(r1.Content, r1.Reasoning, r1.ToolCalls));
		messages.Add(ChatMessage.Tool(r1.ToolCalls[0].Id, "Snow, -3C"));
		var r2 = await provider.CompleteAsync(new ProviderRequest { Model = Glm, Messages = messages, Tools = [tool], MaxOutputTokens = 2000 }, null, default);
		output.WriteLine($"r2: {r2.Content} usage={JsonUtil.Serialize(r2.Usage)}");
		Assert.Contains("3", r2.Content);
	}

	[SkippableFact]
	public async Task CrossProviderDelegationChargesSubscriptionEffectiveDollars() {
		Skip.IfNot(HasKey, "no Z.ai credential");
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => { o.ManagerModelId = "deepseek-v4.1-flash"; o.MaxStepsPerWake = 30; });
		var p = rt.CreateProject("Cross provider", "Delegation across DeepSeek and Z.ai.", home.Workspace, budgetDollars: 1m);
		rt.PostUserMessage(p.Id, """
			Delegate this; do not do it yourself. Create one persistent specialist on model glm-5.3 and assign them: write
			fib.txt in the workspace with the first 8 Fibonacci numbers starting 0 1, one per line, verified with PowerShell.
			When they report, check the file with read_file, accept the objective if correct, and tell me the numbers.
			""");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Author == "manager" && c.Body.Contains("13")), TimeSpan.FromMinutes(8), "result");
		foreach(var c in rt.Store.Conversation(p.Id)) output.WriteLine($"[{c.Author}] {c.Body}");
		var lines = File.ReadAllLines(Path.Combine(home.Workspace, "fib.txt")).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
		Assert.Equal(["0", "1", "1", "2", "3", "5", "8", "13"], lines);
		var specialist = rt.Store.ListAgents(p.Id).First(a => a.ManagerId == p.RootAgentId);
		Assert.Equal("glm-5.3", specialist.ModelId);
		var glmCosts = rt.Store.CostEvents(p.Id).Where(c => c.AgentId == specialist.Id).ToList();
		Assert.NotEmpty(glmCosts);
		// Subscription use adds no marginal cash but always consumes a positive effective budget charge.
		Assert.All(glmCosts, c => { Assert.Equal(0, c.CashNanos); Assert.Equal("subscription_marginal", c.CashBasis); Assert.True(c.EffectiveNanos > 0); });
		var managerCosts = rt.Store.CostEvents(p.Id).Where(c => c.AgentId == p.RootAgentId).ToList();
		Assert.All(managerCosts, c => Assert.True(c.CashNanos > 0));
		output.WriteLine($"glm effective {Money.Format(glmCosts.Sum(c => c.EffectiveNanos))}, deepseek cash {Money.Format(managerCosts.Sum(c => c.CashNanos ?? 0))}");
	}
}
