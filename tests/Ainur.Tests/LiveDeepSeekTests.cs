using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Xunit.Abstractions;

namespace Ainur.Tests;

/// <summary>Live integration checks against DeepSeek. Skipped when no credential is available.</summary>
[Trait("Category", "Live")]
public class LiveDeepSeekTests(ITestOutputHelper output) {
	static readonly bool HasKey = Credentials.Resolve("deepseek") is not null;

	static ModelInfo Flash => Ainur.Core.Accounting.ModelCatalog.Seed.First(m => m.Id == "deepseek-v4.1-flash");

	void Dump(AinurRuntime rt, string projectId) {
		foreach(var c in rt.Store.Conversation(projectId)) output.WriteLine($"[{c.Author}] {c.Body}");
		var s = rt.Ledger.Summary(projectId);
		output.WriteLine($"effective {Money.Format(s.EffectiveNanos)} cash {Money.Format(s.CashKnownNanos)}");
		foreach(var e in rt.Store.Events(projectId, limit: 400).Where(e => e.Kind is "tool.finished" or "agent.created" or "notification.sent" or "compaction.succeeded" or "compaction.failed" or "session.failed" or "agent.paused"))
			output.WriteLine($"  {e.Kind} {e.AgentId} {TextUtil.Truncate(e.Payload, 300)}");
	}

	[SkippableFact]
	public async Task ToolCallRoundTripWithReasoning() {
		Skip.IfNot(HasKey, "no DeepSeek credential");
		var provider = DeepSeekProvider.CreateDefault();
		var tool = new ToolSpec("get_weather", "Get the weather for a city.", Schema.Object(("city", Schema.String("City"), true)));
		var messages = new List<ChatMessage> { ChatMessage.System("Use tools when helpful."), ChatMessage.User("What's the weather in Lisbon? Use the tool.") };
		var r1 = await provider.CompleteAsync(new ProviderRequest { Model = Flash, Messages = messages, Tools = [tool], MaxOutputTokens = 2000, ReasoningEffort = "low" }, null, default);
		output.WriteLine($"r1 finish={r1.FinishReason} calls={r1.ToolCalls.Count} reasoning={r1.Reasoning?.Length} usage={Ainur.Core.JsonUtil.Serialize(r1.Usage)}");
		Assert.NotEmpty(r1.ToolCalls);
		Assert.Equal("get_weather", r1.ToolCalls[0].Name);
		Assert.True(r1.Usage.InputTokens > 0);
		messages.Add(ChatMessage.Assistant(r1.Content, r1.Reasoning, r1.ToolCalls));
		messages.Add(ChatMessage.Tool(r1.ToolCalls[0].Id, "Sunny, 24C"));
		var r2 = await provider.CompleteAsync(new ProviderRequest { Model = Flash, Messages = messages, Tools = [tool], MaxOutputTokens = 2000, ReasoningEffort = "low" }, null, default);
		output.WriteLine($"r2: {r2.Content}");
		Assert.Contains("24", r2.Content);
		// Reasoning omitted on a past turn must also be accepted.
		messages[2] = ChatMessage.Assistant(r1.Content, null, r1.ToolCalls);
		messages.Add(ChatMessage.Assistant(r2.Content));
		messages.Add(ChatMessage.User("And in Porto? Use the tool again."));
		var r3 = await provider.CompleteAsync(new ProviderRequest { Model = Flash, Messages = messages, Tools = [tool], MaxOutputTokens = 2000, ReasoningEffort = "none" }, null, default);
		output.WriteLine($"r3 calls={r3.ToolCalls.Count} content={r3.Content}");
		Assert.NotEmpty(r3.ToolCalls);
	}

	[SkippableFact]
	public async Task ManagerUsesMultiEditAndPowerShell() {
		Skip.IfNot(HasKey, "no DeepSeek credential");
		using var home = new TempHome();
		File.WriteAllText(Path.Combine(home.Workspace, "config.txt"), "name = old\nmode = slow\nlevel = 1\n");
		using var rt = home.Runtime(configure: o => { o.ManagerModelId = "deepseek-v4.1-flash"; o.MaxStepsPerWake = 25; });
		var p = rt.CreateProject("Live edit", "Exercise file tools.", home.Workspace, budgetDollars: 0.5m);
		rt.PostUserMessage(p.Id, "In config.txt, change 'name = old' to 'name = new' and 'mode = slow' to 'mode = fast' in a single multi_edit call, then use the powershell tool to run `Get-Content config.txt` to verify. Do this yourself; do not create agents. Reply with the final file contents.");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Author == "manager"), TimeSpan.FromMinutes(4), "manager reply");
		Dump(rt, p.Id);
		Assert.Equal("name = new\nmode = fast\nlevel = 1\n", File.ReadAllText(Path.Combine(home.Workspace, "config.txt")));
		var tools = rt.Store.Events(p.Id).Where(e => e.Kind == "tool.finished").Select(e => JsonNode.Parse(e.Payload)!["tool_name"]!.GetValue<string>()).ToList();
		Assert.Contains("multi_edit", tools);
		Assert.Contains("powershell", tools);
		Assert.True(rt.Ledger.Summary(p.Id).CashKnownNanos > 0);
	}

	[SkippableFact]
	public async Task ManagerDelegatesToSpecialistWhoReportsBack() {
		Skip.IfNot(HasKey, "no DeepSeek credential");
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => { o.ManagerModelId = "deepseek-v4.1-flash"; o.SpecialistModelId = "deepseek-v4.1-flash"; o.MaxStepsPerWake = 30; });
		var p = rt.CreateProject("Live delegation", "A tiny project to test delegation.", home.Workspace, budgetDollars: 1m);
		rt.PostUserMessage(p.Id, """
			Please delegate this; do not do it yourself. Create one persistent specialist on deepseek-v4.1-flash and assign them an
			objective: write primes.txt in the workspace containing the first ten prime numbers, one per line, and verify it with
			PowerShell. When they report back with evidence, check the file yourself with read_file, mark the objective complete if
			it is right, and tell me the result.
			""");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Author == "manager" && c.Body.Contains("29")), TimeSpan.FromMinutes(8), "delegated result");
		Dump(rt, p.Id);
		var lines = File.ReadAllLines(Path.Combine(home.Workspace, "primes.txt")).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
		Assert.Equal(["2", "3", "5", "7", "11", "13", "17", "19", "23", "29"], lines);
		var agents = rt.Store.ListAgents(p.Id);
		Assert.True(agents.Count >= 2);
		var specialist = agents.First(a => a.ManagerId == p.RootAgentId);
		Assert.Equal("deepseek-v4.1-flash", specialist.ModelId);
		Assert.Contains(rt.Store.ListObjectives(p.Id), o => o.OwnerId == specialist.Id || o.DelegatedById == p.RootAgentId);
		var byAgent = rt.Ledger.ByAgent(p.Id);
		Assert.True(byAgent[specialist.Id].Direct > 0);
	}

	[SkippableFact]
	public async Task LiveRollingAndFullCompaction() {
		Skip.IfNot(HasKey, "no DeepSeek credential");
		using var home = new TempHome();
		var policy = new ContextPolicy { MaxContextTokens = 30_000, ReservedOutputTokens = 4_000, TriggerFraction = 0.55, RollingFraction = 0.5, ElideAfterTurns = 50, ToolTokenBudget = 20_000, CompactorModelId = "deepseek-v4.1-flash" };
		using var rt = home.Runtime(configure: o => { o.ManagerModelId = "deepseek-v4.1-flash"; o.MaxStepsPerWake = 40; o.PolicyOverride = (_, _) => policy; });
		var p = rt.CreateProject("Live compaction", "Exercise compaction.", home.Workspace, budgetDollars: 1m);
		for(var i = 1; i <= 6; i++)
			File.WriteAllText(Path.Combine(home.Workspace, $"part{i}.txt"), $"Secret word for part {i} is {(new[] { "amber", "basalt", "cobalt", "dune", "ember", "fjord" })[i - 1]}.\n" + string.Join("\n", Enumerable.Range(0, 300).Select(n => $"filler line {n} of part {i} lorem ipsum dolor sit amet")));
		rt.PostUserMessage(p.Id, "Read part1.txt through part6.txt one at a time with read_file (one call per step, in order). Then reply with the six secret words in order, nothing else.");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Author == "manager"), TimeSpan.FromMinutes(6), "answer");
		Dump(rt, p.Id);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var view = rt.CurrentView(root.PrimarySessionId!);
		Assert.NotNull(view.SummaryItemId);
		var answer = rt.Store.Conversation(p.Id).Last(c => c.Author == "manager").Body.ToLowerInvariant();
		foreach(var w in new[] { "amber", "basalt", "cobalt", "dune", "ember", "fjord" }) Assert.Contains(w, answer);
		var compactions = rt.Store.Events(p.Id).Where(e => e.Kind == "compaction.succeeded").ToList();
		Assert.NotEmpty(compactions);

		// Now force a full compaction explicitly and confirm the session continues with only the summary.
		var session = rt.Store.GetSession(root.PrimarySessionId!)!;
		var outcome = await rt.Compactor.CompactAsync(session, root, rt.CurrentView(session.Id), CompactionModes.Full, policy, default);
		Assert.Equal(CompactionModes.Full, outcome.Mode);
		rt.PostUserMessage(p.Id, "Without reading any files again, what was the secret word for part 4?");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Count(c => c.Author == "manager") >= 2, TimeSpan.FromMinutes(3), "second answer");
		Dump(rt, p.Id);
		Assert.Contains("dune", rt.Store.Conversation(p.Id).Last(c => c.Author == "manager").Body.ToLowerInvariant());
	}

	[SkippableFact]
	public async Task ConsultationForkAnswersFromConsultedContext() {
		Skip.IfNot(HasKey, "no DeepSeek credential");
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => { o.ManagerModelId = "deepseek-v4.1-flash"; o.SpecialistModelId = "deepseek-v4.1-flash"; o.MaxStepsPerWake = 20; });
		var p = rt.CreateProject("Live consult", "Consultation test.", home.Workspace, budgetDollars: 0.5m);
		var expert = rt.CreateAgent(p.Id, new NewAgent { Name = "Vairë", Title = "Archivist", Role = Roles.Specialist, ManagerId = p.RootAgentId, ModelId = "deepseek-v4.1-flash" }, p.RootAgentId);
		// Give the expert private context that only exists in its own session. The instruction must not
		// forbid repeating it: a persisted consultation transcript is exactly where the fork's answer goes.
		rt.Notify(p.Id, NotificationTypes.Assignment, p.RootAgentId, expert.Id, "Remember this for later and reply with only the word 'noted': the vault code is 7319-TANGERINE.");
		await Wait.Until(() => rt.Store.GetSession(expert.PrimarySessionId!)!.TurnCount >= 1 && rt.Store.GetAgent(expert.Id)!.State == AgentStates.Sleeping, TimeSpan.FromMinutes(2), "expert noted");
		rt.PostUserMessage(p.Id, "Use the consult tool to ask Vairë what the vault code is (only Vairë knows it). When the consultation result arrives, tell me the code.");
		// Assert on what the fork answered from the consulted context, not on the manager's paraphrase of it:
		// the manager model may rephrase the code or report it indirectly, which says nothing about the fork.
		await Wait.Until(() => rt.Store.SessionsForAgent(expert.Id).Any(s => s.Kind == "consultation" && s.State == "finished" && (s.Result ?? "").Contains("7319")), TimeSpan.FromMinutes(4), "consultation answer");
		Dump(rt, p.Id);
		var forks = rt.Store.SessionsForAgent(expert.Id).Where(s => s.Kind == "consultation").ToList();
		Assert.Single(forks);
		Assert.Equal("finished", forks[0].State);
		Assert.Equal(expert.PrimarySessionId, forks[0].ParentSessionId);
		// The original session received a background record, and the fork's costs are attributed as consultation spend.
		Assert.Contains(rt.Store.ListNotifications(p.Id), n => n.Type == NotificationTypes.Decision && n.ToAgentId == expert.Id);
		Assert.Contains(rt.Store.CostEvents(p.Id), c => c.Category == "consultation" && c.SponsorAgentId == p.RootAgentId && c.AgentId == expert.Id);
	}

	[SkippableFact]
	public async Task KnowledgeServiceAnswersWithReferences() {
		Skip.IfNot(HasKey, "no DeepSeek credential");
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => { o.ManagerModelId = "deepseek-v4.1-flash"; o.MaxStepsPerWake = 20; });
		var p = rt.CreateProject("Live knowledge", "Knowledge test.", home.Workspace, budgetDollars: 0.5m);
		Knowledge.Write(rt.Store, p.Id, "decisions/database", "Database choice", "decision", "We use SQLite in WAL mode for local persistence. Postgres was rejected for the bootstrap because it adds an external service.", p.RootAgentId, "design review 2026-09-30", null, null);
		Knowledge.Write(rt.Store, p.Id, "requirements/ui", "UI stack", "requirement", "The UI must be React with TypeScript, built by Vite.", p.RootAgentId, "spec", null, null);
		rt.PostUserMessage(p.Id, "Use ask_knowledge to find out which database we chose and why. When the result arrives, tell me the answer and the document key it came from.");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Author == "manager" && c.Body.Contains("SQLite")), TimeSpan.FromMinutes(4), "knowledge answer");
		Dump(rt, p.Id);
		var service = rt.Store.SessionsForAgent(p.RootAgentId!).Single(s => s.Kind == "service");
		Assert.Equal("finished", service.State);
		Assert.Contains("decisions/database", service.Result);
		Assert.Contains(rt.Store.CostEvents(p.Id), c => c.Category == "knowledge");
	}

	[SkippableFact]
	public async Task ToolFinderLoadsAndCacheEvictsWithinBudget() {
		Skip.IfNot(HasKey, "no DeepSeek credential");
		using var home = new TempHome();
		var policy = new ContextPolicy { ToolTokenBudget = 4_500 };
		using var rt = home.Runtime(configure: o => { o.ManagerModelId = "deepseek-v4.1-flash"; o.MaxStepsPerWake = 25; o.PolicyOverride = (_, _) => policy; });
		// A large inventory of plausible tools the agent has never seen.
		for(var i = 0; i < 120; i++)
			rt.Tools.Register(new ScriptTool($"inventory_tool_{i:000}", $"Inventory helper number {i} for warehouse bin {i * 7} counting and stock audits.", Schema.Object(("bin", Schema.String("Bin id"), false)), "param($bin) 'ok'", ["inventory", "warehouse"], TimeSpan.FromSeconds(10)));
		rt.Tools.Register(new ScriptTool("convert_temperature", "Convert a temperature between Celsius and Fahrenheit.", Schema.Object(("value", Schema.Number("Temperature"), true), ("to", Schema.String("Target unit: C or F", "C", "F"), true)),
			"param([double]$value, [string]$to) if($to -eq 'F') { '{0:0.0} F' -f ($value * 9 / 5 + 32) } else { '{0:0.0} C' -f (($value - 32) * 5 / 9) }", ["temperature", "convert", "units"], TimeSpan.FromSeconds(10)));
		var p = rt.CreateProject("Live tools", "Tool finder test.", home.Workspace, budgetDollars: 0.5m);
		rt.PostUserMessage(p.Id, "Use find_tools to find a tool that converts temperatures, then call that tool directly (not via powershell) to convert 100 C to Fahrenheit. Reply with the result only.");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Author == "manager"), TimeSpan.FromMinutes(4), "converted");
		Dump(rt, p.Id);
		Assert.Contains("212", rt.Store.Conversation(p.Id).Last().Body);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var cache = new ToolCache(rt.Store, rt.Tools, root.PrimarySessionId!);
		Assert.True(cache.RenderedTokens() <= policy.ToolTokenBudget + 2_000, $"tool tokens {cache.RenderedTokens()}");
		var calls = rt.Store.Events(p.Id).Where(e => e.Kind == "tool.finished").Select(e => JsonNode.Parse(e.Payload)!["tool_name"]!.GetValue<string>()).ToList();
		Assert.Contains("find_tools", calls);
		Assert.Contains("convert_temperature", calls);
		Assert.Contains(rt.Store.CostEvents(p.Id), c => c.Category == "tool_finder");
		// Never loaded the full inventory into context.
		Assert.True(cache.Entries().Count(e => e.Name.StartsWith("inventory_tool")) <= 4);
	}
}
