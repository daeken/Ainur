using Ainur.Core;
using Ainur.Core.Model;
using Dapper;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Xunit;

namespace Ainur.Tests;

public sealed class ReviewHandoffTests {
	[Fact]
	public void SleepingReviewerGetsOneDurableAssignmentAndRepeatedResultCannotDispatchTwice() {
		using var f = new HandoffFixture();
		var first = f.Send();
		Assert.Contains("queued", first.Text);
		Assert.Equal(2, f.Notifications().Count);
		Assert.Single(f.Notifications(), n => n.ToAgentId == f.Reviewer.Id && n.Type == NotificationTypes.Assignment);
		var second = f.Send();
		Assert.Contains("queued", second.Text);
		Assert.Equal(2, f.Notifications().Count);
		Assert.Single(f.Notifications(), n => n.ToAgentId == f.Reviewer.Id && n.Type == NotificationTypes.Assignment);
		Assert.Equal(1, f.Runtime.Db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM review_handoffs")));
	}

	[Fact]
	public async Task SleepingReviewerIsWokenByExistingSessionHostAfterDurableCommit() {
		using var f = new HandoffFixture();
		f.Runtime.Start("offline handoff test");
		Assert.Contains("queued", f.Send().Text);
		await Wait.Until(() => f.ReviewerProviderRequests() > 0, TimeSpan.FromSeconds(5), "reviewer wake");
		Assert.Contains(f.Notifications(), n => n.ToAgentId == f.Reviewer.Id && n.State == "delivered");
	}

	[Fact]
	public async Task RestartAfterResultBeforeManagerAssignmentDeliversOriginalResultWithoutNewDispatch() {
		using var f = new HandoffFixture(managerHasSession: false);
		Assert.Contains("queued", f.Send().Text);
		f.Restart();
		f.AssignManagerSession();
		f.Runtime.Start("offline handoff restart test");
		f.Runtime.Wake(f.Manager.Id); // supported wake after assigning the delayed session; no new notification
		await Wait.Until(() => f.Notifications().Any(n => n.ToAgentId == f.Manager.Id && n.State == "delivered"),
			TimeSpan.FromSeconds(5), "manager notification delivery");
		// Continuity can add bounded coordination reminders when the fake agent ends without disposition.
		// Original ready result and reviewer assignment still occur exactly once: no duplicate work dispatch.
		Assert.Equal(2, f.Notifications().Count(n => n.DedupeKey?.StartsWith("continuity:") != true));
		Assert.Single(f.Notifications(), n => n.Type == NotificationTypes.Assignment && n.ToAgentId == f.Reviewer.Id);
		Assert.Equal(1, f.Runtime.Db.Read(c => c.ExecuteScalar<long>("SELECT COUNT(*) FROM review_handoffs")));
	}

	[Fact]
	public void ResultCommittedBeforeManagerSessionAssignmentSurvivesRuntimeRestart() {
		using var f = new HandoffFixture(managerHasSession: false);
		Assert.Contains("queued", f.Send().Text);
		Assert.Single(f.Runtime.Store.PendingNotifications(f.Manager.Id));
		f.Restart();
		Assert.Single(f.Runtime.Store.PendingNotifications(f.Manager.Id));
		Assert.Single(f.Notifications(), n => n.ToAgentId == f.Reviewer.Id && n.Type == NotificationTypes.Assignment);
		f.AssignManagerSession();
		Assert.Single(f.Runtime.Store.PendingNotifications(f.Manager.Id));
		Assert.Equal(2, f.Notifications().Count);
	}

	[Theory]
	[InlineData("paused")]
	[InlineData("missing")]
	[InlineData("no-session")]
	public void UnavailableReviewerCreatesOneManagerStallWithoutAutoResumeOrRepeat(string mode) {
		using var f = new HandoffFixture(reviewerHasSession: mode != "no-session");
		if(mode == "paused") f.SetReviewerState(AgentStates.Paused);
		var reviewerName = mode == "missing" ? "nobody" : f.Reviewer.Name;
		Assert.Contains(mode == "paused" ? "paused" : "stalled", f.Send(reviewerName).Text);
		Assert.Contains(mode == "paused" ? "paused" : "stalled", f.Send(reviewerName).Text);
		Assert.Equal(2, f.Notifications().Count);
		Assert.Single(f.Notifications(), n => n.Type == NotificationTypes.Escalation && n.ToAgentId == f.Manager.Id);
		Assert.DoesNotContain(f.Notifications(), n => n.ToAgentId == f.Reviewer.Id);
		if(mode == "paused") Assert.Equal(AgentStates.Paused, f.Runtime.Store.GetAgent(f.Reviewer.Id)!.State);
	}

	[Fact]
	public void NamedHandoffRequiresResultAndOwnedObjective() {
		using var f = new HandoffFixture();
		Assert.Throws<ToolException>(() => ReviewHandoff.Send(f.Context, f.Manager, NotificationTypes.Decision, f.Objective.Id, f.Reviewer.Name, "done"));
		Assert.Throws<ToolException>(() => ReviewHandoff.Send(f.Context, f.Manager, NotificationTypes.Result, null, f.Reviewer.Name, "done"));
		Assert.Empty(f.Notifications());
	}

	sealed class HandoffFixture : IDisposable {
		readonly TempHome Home = new();
		readonly FakeProvider Provider = new((_, _) => FakeProvider.Text("offline"));
		public AinurRuntime Runtime { get; private set; }
		public int ReviewerProviderRequests() => Provider.Requests.Count;
		public Project Project { get; }
		public Agent Manager { get; }
		public Agent Sender { get; }
		public Agent Reviewer { get; }
		public Objective Objective { get; }
		public ToolContext Context => new() { Runtime = Runtime, Project = Project, Agent = Sender,
			Session = Runtime.Store.GetSession(Sender.PrimarySessionId!)!, InvocationId = Ids.New("inv"),
			CancellationToken = CancellationToken.None, Host = null! };

		public HandoffFixture(bool managerHasSession = true, bool reviewerHasSession = true) {
			Runtime = Home.Runtime(Provider, start: false);
			var now = Clock.Now;
			Project = new Project { Id = Ids.New("prj"), Name = "test", CreatedAt = now, UpdatedAt = now };
			Manager = Agent("manager"); Sender = Agent("sender", Manager.Id); Reviewer = Agent("reviewer", Manager.Id);
			Objective = new Objective { Id = Ids.New("obj"), ProjectId = Project.Id, OwnerId = Sender.Id,
				Title = "Produce reviewed result", State = ObjectiveStates.Active, CreatedAt = now, UpdatedAt = now };
			Runtime.Db.Write(u => {
				Runtime.Store.InsertProject(u, Project);
				Runtime.Store.InsertAgent(u, Manager);
				Runtime.Store.InsertAgent(u, Sender);
				Runtime.Store.InsertAgent(u, Reviewer);
				Runtime.Store.InsertObjective(u, Objective, Sender.Id);
				InsertSession(u, Sender);
				if(managerHasSession) InsertSession(u, Manager);
				if(reviewerHasSession) InsertSession(u, Reviewer);
			});
		}

		Agent Agent(string name, string? manager = null) => new() { Id = Ids.New("agt"), ProjectId = Project.Id,
			Name = name, ManagerId = manager, ModelId = "deepseek-v4.1-flash", State = AgentStates.Sleeping,
			CreatedAt = Clock.Now, UpdatedAt = Clock.Now };

		void InsertSession(Ainur.Core.Persistence.Db.Unit u, Agent agent) {
			var session = new Session { Id = Ids.New("ses"), ProjectId = Project.Id, AgentId = agent.Id,
				ModelId = "deepseek-v4.1-flash", State = "idle", CreatedAt = Clock.Now, UpdatedAt = Clock.Now };
			Runtime.Store.InsertSession(u, session);
			agent.PrimarySessionId = session.Id;
			Runtime.Store.UpdateAgent(u, agent);
		}

		public void AssignManagerSession() => Runtime.Db.Write(u => InsertSession(u, Manager));
		public void SetReviewerState(string state) {
			Reviewer.State = state;
			Runtime.Db.Write(u => Runtime.Store.UpdateAgent(u, Reviewer));
		}
		public ToolResult Send(string? reviewer = null) => ReviewHandoff.Send(Context, Manager, NotificationTypes.Result,
			Objective.Id, reviewer ?? Reviewer.Name, "ready for independent review");
		public List<Notification> Notifications() => Runtime.Store.ListNotifications(Project.Id);
		public void Restart() {
			Runtime.Dispose();
			Runtime = Home.Runtime(Provider, start: false);
		}
		public void Dispose() { Runtime.Dispose(); Home.Dispose(); }
	}
}
