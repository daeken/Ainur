using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using System.Text.Json.Nodes;

namespace Ainur.Tests;

public class AgentTitleTests {
	static AinurRuntime Runtime(TempHome home) {
		var registry = new ProviderRegistry();
		registry.Register(new FakeProvider((_, _) => FakeProvider.Text(""), "deepseek"));
		var rt = new AinurRuntime(new RuntimeOptions { Home = home.Path, AutoStartHosts = false }, registry);
		rt.Start("test");
		return rt;
	}

	[Fact]
	public void CanonicalRepairUsesExactOldTitleAndJournalsWithoutChangingSessions() {
		using var home = new TempHome();
		using var rt = Runtime(home);
		var project = rt.CreateProject("T", "", home.Workspace);
		var manager = rt.Store.GetAgent(project.RootAgentId!)!;
		var agent = rt.CreateAgent(project.Id, new() { Name = "Test", Title = "Browser &amp; computer-use engineer", Role = Roles.Specialist, ManagerId = manager.Id }, manager.Id);
		var before = rt.Store.SessionsForAgent(agent.Id).Select(s => (s.Id, s.ModelId, s.State)).ToArray();
		var oldModel = agent.ModelId;
		var oldEffort = agent.ReasoningEffort;

		var repaired = rt.SetAgentTitle(manager.Id, agent.Id, "Browser & computer-use engineer", "Browser &amp; computer-use engineer");

		Assert.Equal("Browser & computer-use engineer", repaired.Title);
		Assert.Equal(repaired.Title, rt.Store.GetAgent(agent.Id)!.Title);
		Assert.Equal(oldModel, rt.Store.GetAgent(agent.Id)!.ModelId);
		Assert.Equal(oldEffort, rt.Store.GetAgent(agent.Id)!.ReasoningEffort);
		Assert.Equal(before, rt.Store.SessionsForAgent(agent.Id).Select(s => (s.Id, s.ModelId, s.State)).ToArray());
		var ev = Assert.Single(rt.Store.Events(project.Id), e => e.Kind == "agent.updated" && e.EntityId == agent.Id);
		var change = JsonNode.Parse(ev.Payload)!["title"]!;
		Assert.Equal("Browser &amp; computer-use engineer", change["from"]!.GetValue<string>());
		Assert.Equal("Browser & computer-use engineer", change["to"]!.GetValue<string>());
	}

	[Fact]
	public void TitleUpdateIsVerbatimAndDoesNotDecodeLiteralEntitiesOrMarkup() {
		using var home = new TempHome();
		using var rt = Runtime(home);
		var project = rt.CreateProject("T", "", home.Workspace);
		var root = rt.Store.GetAgent(project.RootAgentId!)!;
		const string literal = "Examples: &amp; <img onerror=alert(1)>";
		rt.SetAgentTitle(root.Id, root.Id, literal, root.Title);
		Assert.Equal(literal, rt.Store.GetAgent(root.Id)!.Title);
	}

	[Fact]
	public void PreconditionsAndAuthorizationRejectAndDoNotJournal() {
		using var home = new TempHome();
		using var rt = Runtime(home);
		var p = rt.CreateProject("T", "", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var specialist = rt.CreateAgent(p.Id, new() { Name = "S", Title = "Original", Role = Roles.Specialist, ManagerId = root.Id }, root.Id);
		var other = rt.CreateProject("Other", "", home.Workspace);
		var eventCount = rt.Store.Events(p.Id).Count;
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(root.Id, specialist.Id, "New", "stale"));
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(specialist.Id, specialist.Id, "New", "Original"));
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(other.RootAgentId!, specialist.Id, "New", "Original"));
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(root.Id, specialist.Id, "  ", "Original"));
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(root.Id, specialist.Id, "A\nB", "Original"));
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(root.Id, specialist.Id, new string('X', 201), "Original"));
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(root.Id, specialist.Id, "New", null));
		Assert.Equal("Original", rt.Store.GetAgent(specialist.Id)!.Title);
		Assert.Equal(eventCount, rt.Store.Events(p.Id).Count);
	}
}
