using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using System.Text.Json.Nodes;

namespace Ainur.Tests;

/// <summary>
/// Runtime model / reasoning-effort changes for live agents.
/// The important case is <see cref="ModelChangeTakesEffectAtTheNextStepOfTheSameSession"/>: it proves, against a
/// deterministic provider, that a running session picks up a new model on its next model step with no new session
/// and no runtime restart.
/// </summary>
public class AgentModelTests {
	static AinurRuntime Runtime(TempHome home, params FakeProvider[] providers) {
		var registry = new ProviderRegistry();
		foreach(var p in providers) registry.Register(p);
		var rt = new AinurRuntime(new RuntimeOptions { Home = home.Path, AutoStartHosts = false }, registry);
		rt.Start("test");
		return rt;
	}

	static FakeProvider Prov(Func<ProviderRequest, int, ProviderResponse> handler, string id) => new(handler, id);

	[Fact]
	public void ChangePersistsRetargetsThePrimarySessionAndJournals() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"), Prov((_, _) => FakeProvider.Text(""), "openai"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		Assert.Equal("deepseek-v4.1-flash", root.ModelId);
		var sessionBefore = rt.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary");

		var change = rt.SetAgentModel(root.Id, root.Id, "gpt-6-astra", "max");

		Assert.Equal("deepseek-v4.1-flash", change.PreviousModelId);
		Assert.Equal("high", change.PreviousReasoningEffort);
		Assert.Equal("gpt-6-astra", change.Agent.ModelId);
		Assert.Equal("max", change.Agent.ReasoningEffort);
		Assert.Contains(sessionBefore.Id, change.SessionsRetargeted);
		// Persisted agent row.
		Assert.Equal("gpt-6-astra", rt.Store.GetAgent(root.Id)!.ModelId);
		Assert.Equal("max", rt.Store.GetAgent(root.Id)!.ReasoningEffort);
		// The running session was retargeted, which is what makes the change effective without a restart.
		Assert.Equal("gpt-6-astra", rt.Store.GetSession(sessionBefore.Id)!.ModelId);
		// Journaled with old and new values.
		var ev = rt.Store.Events(p.Id).Single(e => e.Kind == "agent.updated" && e.EntityId == root.Id);
		Assert.Contains("gpt-6-astra", ev.Payload);
		Assert.Contains("deepseek-v4.1-flash", ev.Payload);
		Assert.Contains("max", ev.Payload);
	}

	[Fact]
	public void ChangeSurvivesRuntimeRestart() {
		using var home = new TempHome();
		string projectId;
		using(var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"), Prov((_, _) => FakeProvider.Text(""), "openai"))) {
			var p = rt.CreateProject("T", "d", home.Workspace);
			projectId = p.Id;
			rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, "gpt-6-astra", "max");
		}
		using var rt2 = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "openai"));
		var root = rt2.Store.GetAgent(rt2.Store.GetProject(projectId)!.RootAgentId!)!;
		Assert.Equal("gpt-6-astra", root.ModelId);
		Assert.Equal("max", root.ReasoningEffort);
		Assert.Equal("gpt-6-astra", rt2.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary").ModelId);
	}

	[Fact]
	public void UnknownModelIsRejectedByName() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var ex = Assert.Throws<DomainException>(() => rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, "gpt-9-nope", null));
		Assert.Contains("Unknown model 'gpt-9-nope'", ex.Message);
		Assert.Equal("deepseek-v4.1-flash", rt.Store.GetAgent(p.RootAgentId!)!.ModelId);
	}

	[Fact]
	public void DisabledModelIsRejected() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		Assert.False(rt.Store.GetModel("deepseek-v4-pro")!.Enabled);
		var ex = Assert.Throws<DomainException>(() => rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, "deepseek-v4-pro", null));
		Assert.Contains("deepseek-v4-pro", ex.Message);
		Assert.Contains("not usable", ex.Message);
		Assert.Equal("deepseek-v4.1-flash", rt.Store.GetAgent(p.RootAgentId!)!.ModelId);
	}

	[Fact]
	public void EnabledModelWithoutAProviderAdapterIsRejected() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		rt.Db.Write(u => rt.Store.UpsertModel(u, new ModelInfo { Id = "mystery-1", Provider = "mystery", UpstreamModel = "mystery-1", DisplayName = "Mystery", Enabled = true }));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var ex = Assert.Throws<DomainException>(() => rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, "mystery-1", null));
		Assert.Contains("mystery-1", ex.Message);
		Assert.Contains("no live adapter", ex.Message);
	}

	[Fact]
	public void InvalidEffortIsRejected() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var ex = Assert.Throws<DomainException>(() => rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, null, "turbo"));
		Assert.Contains("Reasoning effort 'turbo' is not allowed", ex.Message);
		Assert.Contains("medium, high, max", ex.Message);
		// The rejected change left the created default in place.
		Assert.Equal("high", rt.Store.GetAgent(p.RootAgentId!)!.ReasoningEffort);
	}

	[Fact]
	public void EffortTheModelCannotExpressIsRejected() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"), Prov((_, _) => FakeProvider.Text(""), "zai"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		// glm-5.3 is an enabled zai model; zai does not transmit a reasoning-effort level (only thinking on/off).
		// Every permitted effort maps to thinking-enabled, so the platform vocabulary is accepted as-is.
		var change = rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, "glm-5.3", "medium");
		Assert.Equal("glm-5.3", change.Agent.ModelId);
		Assert.Equal("medium", change.Agent.ReasoningEffort);
	}

	[Fact]
	public void EffortIsKeptWhenOnlyTheModelIsChanged() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "openai"));
		var p = rt.CreateProject("T", "d", home.Workspace, managerModelId: "gpt-6-astra");
		rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, "gpt-6-astra", "high");
		var change = rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, "gpt-6", null);
		Assert.Equal("gpt-6", change.Agent.ModelId);
		Assert.Equal("high", change.Agent.ReasoningEffort);
	}

	[Fact]
	public void NonManagerCannotChangeModels() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"), Prov((_, _) => FakeProvider.Text(""), "openai"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var specialist = rt.CreateAgent(p.Id, new() { Name = "UlmoTest", Role = Roles.Specialist, ManagerId = p.RootAgentId }, p.RootAgentId);
		Assert.Throws<DomainException>(() => rt.SetAgentModel(specialist.Id, specialist.Id, "gpt-6-astra", null));
		var ex = Assert.Throws<DomainException>(() => rt.SetAgentModel(specialist.Id, specialist.Id, null, "max"));
		Assert.Contains("Only a manager", ex.Message);
		Assert.Equal("high", rt.Store.GetAgent(specialist.Id)!.ReasoningEffort);
		// A manager cannot reach outside its subtree either.
		var other = rt.CreateProject("T2", "d", home.Workspace);
		rt.CreateAgent(other.Id, new() { Name = "Far", Role = Roles.Specialist, ManagerId = other.RootAgentId }, other.RootAgentId);
		var ex2 = Assert.Throws<DomainException>(() => rt.SetAgentModel(p.RootAgentId, rt.Store.ListAgents(other.Id).Single(a => a.Name == "Far").Id, "gpt-6-astra", null));
		Assert.Contains("reporting subtree", ex2.Message);
	}

	[Fact]
	public void ManagerCanRepointASubordinate() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"), Prov((_, _) => FakeProvider.Text(""), "openai"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var sub = rt.CreateAgent(p.Id, new() { Name = "AuleTest", Role = Roles.Specialist, ManagerId = p.RootAgentId }, p.RootAgentId);
		var change = rt.SetAgentModel(p.RootAgentId, sub.Id, "gpt-6.1-sol", "medium");
		Assert.Equal("gpt-6.1-sol", change.Agent.ModelId);
		Assert.Equal("medium", rt.Store.GetAgent(sub.Id)!.ReasoningEffort);
	}

	[Fact]
	public async Task ModelChangeTakesEffectAtTheNextStepOfTheSameSession() {
		using var home = new TempHome();
		// The model the request was routed to is what the runtime resolved for that step, so the handler switches
		// its behaviour on it: the manager's first (deepseek) step asks to move itself, every later step finishes.
		ProviderResponse Handler(ProviderRequest req, int _) => req.Model.Id == "deepseek-v4.1-flash"
			? FakeProvider.Call("set_agent_model", """{"agent":"me","model":"gpt-6-astra","reasoning_effort":"max"}""")
			: FakeProvider.Text("switched");
		var cheap = Prov(Handler, "deepseek");
		var astra = Prov(Handler, "openai");
		using var rt = Runtime(home, cheap, astra);
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var sessionsBefore = rt.Store.SessionsForAgent(root.Id).Count;
		var primary = rt.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary");

		rt.PostUserMessage(p.Id, "move yourself onto gpt-6-astra at max");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Count >= 2, TimeSpan.FromSeconds(20), "manager reply");

		// The first step ran on the old model through the deepseek adapter; the very next step of the same wake was
		// routed to the openai adapter on the new model, with the new effort - same session, no restart.
		var first = Assert.Single(cheap.Requests);
		Assert.Equal("deepseek-v4.1-flash", first.Model.Id);
		var second = Assert.Single(astra.Requests);
		Assert.Equal("gpt-6-astra", second.Model.Id);
		Assert.Equal("max", second.ReasoningEffort);
		Assert.Equal(primary.Id, rt.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary").Id);
		Assert.Equal(sessionsBefore, rt.Store.SessionsForAgent(root.Id).Count);
		Assert.Equal("gpt-6-astra", rt.Store.GetAgent(root.Id)!.ModelId);
		Assert.Equal("gpt-6-astra", rt.Store.GetSession(primary.Id)!.ModelId);
	}

	// ------------------------------------------------------------------------------------------------
	// Policy 2026-10-01: unspecified effort defaults to high; medium is the enforced floor.
	// ------------------------------------------------------------------------------------------------

	[Fact]
	public void CreationDefaultsEffortToHighAndKeepsTheAgentAndSessionConsistent() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		Assert.Equal("high", root.ReasoningEffort);
		var sub = rt.CreateAgent(p.Id, new() { Name = "Defaulted", Role = Roles.Specialist, ManagerId = p.RootAgentId }, p.RootAgentId);
		Assert.Equal("high", rt.Store.GetAgent(sub.Id)!.ReasoningEffort);
		// The persisted agent row and its primary session row agree on the model, and every primary session has one.
		foreach(var agent in rt.Store.ListAgents(p.Id)) {
			var sessions = rt.Store.SessionsForAgent(agent.Id).Where(s => s.Kind == "primary").ToList();
			foreach(var s in sessions) Assert.Equal(agent.ModelId, s.ModelId);
		}
	}

	[Theory]
	[InlineData("medium")]
	[InlineData("high")]
	[InlineData("max")]
	[InlineData("HIGH")]
	public void AllowedEffortsAreAcceptedAtCreationAndPersisted(string effort) {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var sub = rt.CreateAgent(p.Id, new() { Name = "Allowed", Role = Roles.Specialist, ManagerId = p.RootAgentId, ReasoningEffort = effort }, p.RootAgentId);
		Assert.Equal(effort.ToLowerInvariant(), rt.Store.GetAgent(sub.Id)!.ReasoningEffort);
		// The create is journaled and the session created for the agent carries the same model.
		Assert.Contains(rt.Store.Events(p.Id), e => e.Kind == "agent.created" && e.EntityId == sub.Id);
		Assert.All(rt.Store.SessionsForAgent(sub.Id), s => Assert.Equal(sub.ModelId, s.ModelId));
	}

	[Theory]
	[InlineData("none")]
	[InlineData("minimal")]
	[InlineData("low")]
	[InlineData("turbo")]
	public void BelowFloorEffortIsRejectedAtCreation(string effort) {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var before = rt.Store.ListAgents(p.Id).Count;
		var ex = Assert.Throws<DomainException>(() => rt.CreateAgent(p.Id, new() { Name = "Rejected", Role = Roles.Specialist, ManagerId = p.RootAgentId, ReasoningEffort = effort }, p.RootAgentId));
		Assert.Contains("'medium' is the platform floor", ex.Message);
		Assert.Equal(before, rt.Store.ListAgents(p.Id).Count);
		Assert.DoesNotContain(rt.Store.ListAgents(p.Id), a => a.Name == "Rejected");
	}

	[Theory]
	[InlineData("none")]
	[InlineData("minimal")]
	[InlineData("low")]
	public void BelowFloorEffortIsRejectedByTheChangeSurface(string effort) {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var ex = Assert.Throws<DomainException>(() => rt.SetAgentModel(p.RootAgentId, p.RootAgentId!, null, effort));
		Assert.Contains("'medium' is the platform floor", ex.Message);
		Assert.Equal("high", rt.Store.GetAgent(p.RootAgentId!)!.ReasoningEffort);
	}

	[Fact]
	public void UnspecifiedEffortDispatchesAsHighWhileExplicitHighAndMaxArePreserved() {
		using var home = new TempHome();
		var provider = Prov((_, _) => FakeProvider.Text("done"), "deepseek");
		using var rt = Runtime(home, provider);
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		Assert.Equal("high", root.ReasoningEffort);
		var high = rt.SetAgentModel(root.Id, root.Id, null, "high");
		Assert.Equal("high", high.Agent.ReasoningEffort);
		var max = rt.SetAgentModel(root.Id, root.Id, null, "max");
		Assert.Equal("max", max.Agent.ReasoningEffort);
		var medium = rt.SetAgentModel(root.Id, root.Id, null, "medium");
		Assert.Equal("medium", medium.Agent.ReasoningEffort);
	}

	[Fact]
	public void ModelOnlyChangeRejectsLegacyLowUntilExplicitlyRepaired() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"), Prov((_, _) => FakeProvider.Text(""), "openai"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		// Simulate a legacy row written before the floor was introduced; service must not propagate it to a new model.
		rt.Db.Write(u => { root.ReasoningEffort = "low"; rt.Store.UpdateAgent(u, root); });
		var ex = Assert.Throws<DomainException>(() => rt.SetAgentModel(root.Id, root.Id, "gpt-6-astra", null));
		Assert.Contains("'medium' is the platform floor", ex.Message);
		Assert.Equal("deepseek-v4.1-flash", rt.Store.GetAgent(root.Id)!.ModelId);
		Assert.Equal("low", rt.Store.GetAgent(root.Id)!.ReasoningEffort);
		var repaired = rt.SetAgentModel(root.Id, root.Id, "gpt-6-astra", "high");
		Assert.Equal("high", repaired.Agent.ReasoningEffort);
		Assert.Equal("gpt-6-astra", rt.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary").ModelId);
	}

	[Fact]
	public async Task ToolSchemasAndInvocationsHonorTheReasoningFloor() {
		using var home = new TempHome();
		using var rt = Runtime(home, Prov((_, _) => FakeProvider.Text(""), "deepseek"));
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var session = rt.Store.SessionsForAgent(root.Id).Single(s => s.Kind == "primary");
		var ctx = new ToolContext { Runtime = rt, Agent = root, Project = p, Session = session,
			Host = rt.GetHost(session.Id)!, InvocationId = "test", CancellationToken = CancellationToken.None };
		var create = new CreateAgentTool();
		var set = new SetAgentModelTool();
		foreach(var schema in new[] { create.InputSchema, set.InputSchema }) {
			var allowed = schema["properties"]!["reasoning_effort"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
			Assert.Equal(new[] { "medium", "high", "max" }, allowed);
		}
		var result = await create.InvokeAsync(ctx, new JsonObject { ["name"] = "ToolCreated", ["title"] = "Tester",
			["role"] = "specialist", ["instructions"] = "test" });
		Assert.False(result.IsError);
		Assert.Equal("high", rt.Store.ListAgents(p.Id).Single(a => a.Name == "ToolCreated").ReasoningEffort);
		var error = await Assert.ThrowsAsync<ToolException>(() => set.InvokeAsync(ctx,
			new JsonObject { ["agent"] = "me", ["reasoning_effort"] = "low" }));
		Assert.Contains("'medium' is the platform floor", error.Message);
		var accepted = await set.InvokeAsync(ctx, new JsonObject { ["agent"] = "me", ["reasoning_effort"] = "medium" });
		Assert.False(accepted.IsError);
		Assert.Equal("medium", rt.Store.GetAgent(root.Id)!.ReasoningEffort);
	}

	[Fact]
	public void ProviderDispatchNeverDowngradesBelowTheFloor() {
		// The floor has to survive the provider mapping: a permitted effort may be raised or passed through, but
		// must never come out as a sub-medium level. deepseek collapses medium onto its 'high' wire value (an
		// upgrade); openai passes it through; zai toggles thinking and transmits no level at all.
		Assert.Equal("high", DeepSeekProvider.MapEffort("medium"));
		Assert.Equal("medium", OpenAiResponsesProvider.MapEffort("medium"));
		Assert.Equal("high", OpenAiResponsesProvider.MapEffort("high"));
		foreach(var effort in AinurRuntime.AllowedReasoningEfforts) {
			Assert.Contains(DeepSeekProvider.MapEffort(effort), new[] { "medium", "high", "max" });
			Assert.Contains(OpenAiResponsesProvider.MapEffort(effort), new[] { "medium", "high", "max" });
		}
		Assert.Equal("enabled", ZaiProvider.MapEffort("medium"));
	}
}
