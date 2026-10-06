using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Dapper;

namespace Ainur.Tests;

public class ProjectOnboardingTests {
	[Fact]
	public void EngineeringSetupInDisposableRepositoryDoesNotDispatchOrChangeOtherProjects() {
		using var home = new TempHome();
		var workspace = Path.Combine(home.Path, "tiny-app");
		Directory.CreateDirectory(workspace);
		File.WriteAllText(Path.Combine(workspace, "README.md"), "# Tiny app\n");
		using var runtime = home.Runtime(start: false);
		var old = runtime.CreateProject("Older project", "Leave unchanged", null, organization: "single");
		var oldAgents = runtime.Store.ListAgents(old.Id);
		var oldObjectives = runtime.Store.ListObjectives(old.Id);
		var project = runtime.CreateProject("Tiny app", "Improve the tiny app", workspace, organization: "engineering");
		var agents = runtime.Store.ListAgents(project.Id);
		var lead = Assert.Single(agents, a => a.ManagerId is null);
		var delivery = Assert.Single(agents, a => a.Title == "Technical delivery manager");
		var reviewer = Assert.Single(agents, a => a.Title == "Independent reviewer");
		Assert.Equal(lead.Id, delivery.ManagerId);
		Assert.Equal(delivery.Id, reviewer.ManagerId);
		Assert.Equal(Roles.Manager, lead.Role);
		Assert.Equal(Roles.Manager, delivery.Role);
		Assert.Equal(Roles.Specialist, reviewer.Role);
		Assert.All(agents, a => { Assert.Equal("gpt-6.1-sol", a.ModelId); Assert.Equal("high", a.ReasoningEffort); Assert.Equal(AgentStates.Sleeping, a.State); });
		Assert.True(Directory.Exists(project.WorkspacePath));
		Assert.Equal("# Tiny app\n", File.ReadAllText(Path.Combine(project.WorkspacePath!, "README.md")));
		Assert.Equal(lead.Id, project.RootAgentId);
		var root = runtime.Store.GetObjective(project.RootObjectiveId!);
		var handoff = Assert.Single(runtime.Store.Children(root!.Id));
		Assert.Equal(delivery.Id, handoff.OwnerId);
		Assert.Equal(root.Id, handoff.ParentId);
		Assert.Equal(lead.Id, handoff.DelegatedById);
		Assert.Equal(ObjectiveStates.Planned, handoff.State);
		Assert.Empty(runtime.Store.ListNotifications(project.Id));
		// Creation itself must neither wake the team nor run a model turn.
		Assert.Equal(0, runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM model_requests WHERE project_id=@projectId", new { projectId = project.Id })));
		Assert.Equal(0, runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COALESCE(SUM(turn_count), 0) FROM sessions WHERE project_id=@projectId", new { projectId = project.Id })));
		Assert.Equal(oldAgents.Select(a => a.Id), runtime.Store.ListAgents(old.Id).Select(a => a.Id));
		Assert.Equal(oldObjectives.Select(o => o.Id), runtime.Store.ListObjectives(old.Id).Select(o => o.Id));
		Assert.Equal("# Tiny app\n", File.ReadAllText(Path.Combine(workspace, "README.md")));
		Assert.Throws<DomainException>(() => runtime.CreateProject("Overlap", "", workspace, organization: "engineering"));
		Assert.Throws<DomainException>(() => runtime.CreateProject("Nested", "", home.Path, organization: "engineering"));
		var alias = Path.Combine(home.Path, "alias");
		Directory.CreateSymbolicLink(alias, workspace);
		Assert.Throws<DomainException>(() => runtime.CreateProject("Symlink alias", "", alias, organization: "engineering"));
		var ancestorAlias = Path.Combine(home.Path, "ancestor-alias");
		Directory.CreateSymbolicLink(ancestorAlias, home.Path);
		Assert.Throws<DomainException>(() => runtime.CreateProject("Symlink ancestor", "", Path.Combine(ancestorAlias, "tiny-app"), organization: "engineering"));
		var independent = Path.Combine(home.Path, "other");
		Directory.CreateDirectory(independent);
		var other = runtime.CreateProject("Independent", "", independent, organization: "engineering");
		Assert.True(Directory.Exists(other.WorkspacePath));
		Assert.NotEqual(project.WorkspacePath, other.WorkspacePath);
		Assert.Throws<DomainException>(() => runtime.CreateProject("No workspace", "", null, organization: "engineering"));
		Assert.Throws<DomainException>(() => runtime.CreateProject("Missing workspace", "", Path.Combine(home.Path, "missing"), organization: "engineering"));
		Assert.Throws<DomainException>(() => runtime.CreateProject("Paid", "", workspace, managerModelId: "gpt-6.1-sol-api", organization: "engineering"));
		Assert.Empty(Directory.GetFiles(workspace).Except([Path.Combine(workspace, "README.md")]));
		// Provider-free persistence check: real Assignment notifications wake and dispatch agents.
		// Drain this offline runtime before sending them so the test records the handoff/review
		// without ever entering a model turn (Runtime(start:false) alone does not prevent Wake).
		runtime.Draining = true;
		var implementer = runtime.CreateAgent(project.Id, new NewAgent {
			Name = "Implementer", Title = "Sample maintainer", ManagerId = delivery.Id,
			ModelId = "gpt-6.1-sol", ReasoningEffort = "high", Instructions = "Edit only the tiny app README."
		}, delivery.Id);
		var task = new Objective {
			Id = Ids.New("obj"), ProjectId = project.Id, ParentId = handoff.Id,
			OwnerId = implementer.Id, DelegatedById = delivery.Id,
			Title = "Clarify the README", Description = "Add usage guidance to the sample README",
			CompletionConditions = "README is independently reviewed and committed in the tiny-app Git repository.",
			State = ObjectiveStates.Active, CreatedAt = Clock.Now, UpdatedAt = Clock.Now
		};
		runtime.Db.Write(u => runtime.Store.InsertObjective(u, task, delivery.Id));
		var assigned = runtime.Notify(project.Id, NotificationTypes.Assignment, delivery.Id, implementer.Id,
			"Improve README.md only in this project's workspace; request independent review.", task.Id);
		Assert.Equal(project.Id, assigned.ProjectId);
		Assert.Equal(implementer.Id, assigned.ToAgentId);
		Assert.Equal(task.Id, assigned.ObjectiveId);
		var review = runtime.Notify(project.Id, NotificationTypes.Assignment, delivery.Id, reviewer.Id,
			"Independently inspect the sample README change and resulting Git commit; report findings to delivery.", task.Id);
		Assert.Equal(reviewer.Id, review.ToAgentId);
		Assert.NotEqual(implementer.Id, reviewer.Id);
		Assert.Equal(project.Id, review.ProjectId);
		Assert.All([implementer, reviewer], a => Assert.Equal(AgentStates.Sleeping, runtime.Store.GetAgent(a.Id)!.State));
		Assert.Equal(0, runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM model_requests WHERE project_id=@projectId", new { projectId = project.Id })));
		Assert.Equal(0, runtime.Db.Read(c => c.ExecuteScalar<int>("SELECT COALESCE(SUM(turn_count), 0) FROM sessions WHERE project_id=@projectId", new { projectId = project.Id })));
		Assert.Empty(runtime.Store.ListNotifications(old.Id));
	}
}
