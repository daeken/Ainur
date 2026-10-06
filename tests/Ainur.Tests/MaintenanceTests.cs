using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Dapper;
using System.Text.Json.Nodes;

namespace Ainur.Tests;

public class MaintenanceTests {
	static async Task Until(Func<bool> condition) {
		var end = DateTime.UtcNow.AddSeconds(20);
		while(!condition() && DateTime.UtcNow < end) await Task.Delay(15);
		Assert.True(condition());
	}
	[Fact]
	public async Task StaggeredBoundariesAccumulateAcrossArdasAndRetainIncomingWorkAndPolicies() {
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => o.AutoStartHosts = false);
		var p1 = rt.CreateProject("one", "", null, managerModelId: "deepseek-v4.1-flash");
		var p2 = rt.CreateProject("two", "", null, managerModelId: "deepseek-v4.1-flash");
		var s1 = rt.Store.GetAgent(p1.RootAgentId!)!.PrimarySessionId!;
		var s2 = rt.Store.GetAgent(p2.RootAgentId!)!.PrimarySessionId!;
		var a1 = rt.Maintenance.Admit("long_tool", s1, "owned-one");
		var a2 = rt.Maintenance.Admit("long_tool", s2, "owned-two");
		rt.PauseAgent(p2.RootAgentId!, null, "intentional user pause");
		rt.RequestPause(s2, s2, "workspace", "intentional pause", "operator release", TimeSpan.FromMinutes(30));
		var pause = rt.ActivePause(s2)!;
		var policy = rt.Store.GetProject(p2.Id)!;
		var op = rt.Maintenance.Begin("upgrade", TimeSpan.FromMilliseconds(20));
		Assert.Equal(2, op.Holds.Count(h => h.State == "running_to_checkpoint"));
		Assert.False(op.VerifiedQuiescent);
		Assert.Null(rt.GetHost(s1));
		rt.PostUserMessage(p1.Id, "retained message");
		rt.Notify(p1.Id, "result", p2.RootAgentId!, p1.RootAgentId!, "owed result");
		Assert.NotEmpty(rt.Store.PendingNotifications(p1.RootAgentId!));
		Assert.Throws<MaintenanceAdmissionException>(() => rt.CreateAgent(p1.Id, new NewAgent { Name = "blocked", ManagerId = p1.RootAgentId!, ModelId = "deepseek-v4.1-flash" }, p1.RootAgentId));
		Assert.Throws<MaintenanceAdmissionException>(() => rt.StartServiceSession(p1.RootAgentId!, "knowledge", "new work", null));
		Assert.Throws<MaintenanceAdmissionException>(() => rt.ResumeAgent(p2.RootAgentId!, null));
		a1.Dispose();
		var partial = rt.Maintenance.Status();
		Assert.Equal("held", partial.Holds.Single(h => h.SessionId == s1).State);
		Assert.Equal("running_to_checkpoint", partial.Holds.Single(h => h.SessionId == s2).State);
		await Task.Delay(30);
		Assert.True(rt.Maintenance.Status().DeadlineElapsed);
		Assert.False(rt.Maintenance.Status().VerifiedQuiescent);
		Assert.Contains(rt.Maintenance.Status().Blockers, b => b.Id == "owned-two");
		Assert.Throws<DomainException>(() => rt.Maintenance.PrepareHandoff(op.Operation!.Id));
		await Assert.ThrowsAsync<DomainException>(() => rt.DrainWithStatusAsync(TimeSpan.FromMilliseconds(1), CancellationToken.None));
		Assert.False(rt.Draining);
		a2.Dispose();
		Assert.True(rt.Maintenance.Status().VerifiedQuiescent);
		rt.Maintenance.Release(op.Operation!.Id, false);
		Assert.False(rt.Maintenance.Fenced);
		Assert.Equal(pause.Id, rt.ActivePause(s2)!.Id);
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(p2.RootAgentId!)!.State);
		Assert.Equal(policy.CashCeilingNanos, rt.Store.GetProject(p2.Id)!.CashCeilingNanos);
		Assert.Equal(policy.EffectiveBudgetNanos, rt.Store.GetProject(p2.Id)!.EffectiveBudgetNanos);
		Assert.Equal(policy.WorkspacePath, rt.Store.GetProject(p2.Id)!.WorkspacePath);
		Assert.NotEmpty(rt.Store.PendingNotifications(p1.RootAgentId!));
	}
	[Fact]
	public async Task PaidResponseIsKeptButUnadmittedCallsAndNextTurnWaitUntilAbort() {
		using var home = new TempHome();
		using var rt = home.Runtime(start: true);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		rt.Providers.Register(new AsyncProvider(async (_, _) => {
			Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task;
			return new ProviderResponse { Content = "paid durable", ToolCalls = [new ToolCall("pending-tool", "costs", "{}")], Usage = new Usage { InputTokens = 10, OutputTokens = 10 } };
		}));
		var p = rt.CreateProject("p", "", null, managerModelId: "deepseek-v4.1-flash");
		rt.PostUserMessage(p.Id, "start");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
		var sid = rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!;
		var op = rt.Maintenance.Begin("hold stream", TimeSpan.FromSeconds(2));
		rt.PostUserMessage(p.Id, "arrived while model running");
		release.TrySetResult();
		await Until(() => rt.Maintenance.Status().VerifiedQuiescent);
		Assert.Contains(rt.Store.Items(sid), i => i.Kind == ItemKinds.Assistant && i.Payload.Contains("paid durable"));
		Assert.Equal(0, rt.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM tool_invocations WHERE call_id='pending-tool'")));
		Assert.Equal(1, calls);
		Assert.NotEmpty(rt.Store.PendingNotifications(p.RootAgentId!));
		rt.Providers.Register(new FakeProvider((_, _) => FakeProvider.Text("finish after abort")));
		rt.Maintenance.Release(op.Operation!.Id, true);
		await Until(() => rt.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM tool_invocations WHERE call_id='pending-tool'")) == 1);
		Assert.Equal(1, rt.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM tool_invocations WHERE call_id='pending-tool'")));
	}
	[Fact]
	public async Task MaintenanceDoesNotCancelLongNonCooperativeToolAndDeadlineIsOnlyObservation() {
		using var home = new TempHome();
		using var rt = home.Runtime(start: true);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var canceled = false;
		rt.Tools.Register(new BlockingTool(entered, release, () => canceled = true));
		rt.Providers.Register(new FakeProvider((_, n) => n == 1 ? FakeProvider.Call("test_maintenance_block", "{}") : FakeProvider.Text("should wait")));
		var p = rt.CreateProject("p", "", null, managerModelId: "deepseek-v4.1-flash");
		rt.PostUserMessage(p.Id, "start");
		await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
		var op = rt.Maintenance.Begin("long tool", TimeSpan.FromMilliseconds(1));
		await Task.Delay(30);
		var status = rt.Maintenance.Status();
		Assert.True(status.DeadlineElapsed);
		Assert.False(status.VerifiedQuiescent);
		Assert.Contains(status.Blockers, b => b.Kind == "tool_invocation" && b.State == "running");
		Assert.False(canceled);
		release.TrySetResult();
		await Until(() => rt.Maintenance.Status().VerifiedQuiescent);
		Assert.False(canceled);
		Assert.Single(rt.Store.Items(rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!), i => i.Kind == ItemKinds.Assistant);
		rt.Maintenance.Release(op.Operation!.Id, true);
	}
	[Fact]
	public void RestartKeepsFenceAndUnknownOwnerWithoutReplayEvenAfterAbort() {
		using var home = new TempHome();
		string sid, opid;
		using(var rt = home.Runtime()) {
			var p = rt.CreateProject("p", "", null, managerModelId: "deepseek-v4.1-flash");
			sid = rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!;
			_ = rt.Maintenance.Admit("noncooperative", sid, "uncertain-action");
			opid = rt.Maintenance.Begin("crash", TimeSpan.FromSeconds(1)).Operation!.Id;
		}
		using(var rt = home.Runtime()) {
			var status = rt.Maintenance.Status();
			Assert.True(status.AdmissionFenced);
			Assert.False(status.VerifiedQuiescent);
			Assert.Contains(status.Blockers, b => b.Id == "uncertain-action" && b.State == "unknown");
			Assert.Equal("unresolved", status.Holds.Single(h => h.SessionId == sid).State);
			rt.Maintenance.Release(opid, true);
			Assert.False(rt.Maintenance.Fenced);
			Assert.Null(rt.GetHost(sid));
			Assert.Throws<MaintenanceAdmissionException>(() => rt.Maintenance.Admit("tool", sid));
		}
	}
	[Fact]
	public void RestartOfIdleCollectingOperationDoesNotInventSuccessfulHandoff() {
		using var home = new TempHome();
		string id;
		using(var rt = home.Runtime(configure: o => o.AutoStartHosts = false)) {
			var p = rt.CreateProject("p", "", null, managerModelId: "deepseek-v4.1-flash");
			id = rt.Maintenance.Begin("unattested restart", TimeSpan.FromSeconds(1)).Operation!.Id;
			Assert.True(rt.Maintenance.Status().VerifiedQuiescent);
		}
		using(var rt = home.Runtime(configure: o => o.AutoStartHosts = false)) {
			Assert.True(rt.Maintenance.Fenced);
			Assert.Equal(id, rt.Maintenance.Status().Operation!.Id);
			Assert.Equal("collecting", rt.Maintenance.Status().Operation!.State);
			rt.Maintenance.Release(id, true);
		}
	}
	[Fact]
	public void ExplicitCleanHandoffReleasesOnlyMaintenanceHoldsOnSuccessfulStartup() {
		using var home = new TempHome();
		string sid, pauseid;
		using(var rt = home.Runtime()) {
			var p = rt.CreateProject("p", "", null, managerModelId: "deepseek-v4.1-flash");
			sid = rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!;
			rt.PauseAgent(p.RootAgentId!, null, "keep intentional pause");
			rt.RequestPause(sid, sid, "workspace", "intentional pause", "operator release", TimeSpan.FromMinutes(30));
			pauseid = rt.ActivePause(sid)!.Id;
			var op = rt.Maintenance.Begin("clean upgrade", TimeSpan.FromSeconds(1));
			Assert.True(rt.Maintenance.PrepareHandoff(op.Operation!.Id).VerifiedQuiescent);
		}
		using(var rt = home.Runtime()) {
			Assert.False(rt.Maintenance.Fenced);
			Assert.Equal(pauseid, rt.ActivePause(sid)!.Id);
			Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(rt.Store.GetSession(sid)!.AgentId)!.State);
		}
	}
	[Fact]
	public async Task DirectModelsConsultationsWorkbooksAndInboxAreFencedWithoutSideEffects() {
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => o.AutoStartHosts = false);
		var p = rt.CreateProject("p", "", null, managerModelId: "deepseek-v4.1-flash");
		var a = rt.CreateAgent(p.Id, new NewAgent { Name = "sub", ManagerId = p.RootAgentId!, ModelId = "deepseek-v4.1-flash" }, p.RootAgentId!);
		var sid = rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!;
		var host = rt.GetHost(sid)!;
		var book = new Workbooks(rt.Db) { Admission = id => rt.Maintenance.Admit("workbook", referenceId: id) };
		var b = book.Create(p.Id, "book");
		var c = book.Add(p.Id, b.Id, "powershell", "1+1")!;
		var op = rt.Maintenance.Begin("fence all", TimeSpan.FromSeconds(1));
		Assert.Throws<MaintenanceAdmissionException>(() => rt.StartConsultation(p.RootAgentId!, a.Id, "question", null));
		Assert.Throws<MaintenanceAdmissionException>(() => host.DeliverInbox());
		await Assert.ThrowsAsync<MaintenanceAdmissionException>(() => book.ExecuteAsync(p.Id, b.Id, c.Id, 1, "test", null, CancellationToken.None));
		await Assert.ThrowsAsync<MaintenanceAdmissionException>(() => rt.Gateway.CallAsync(new ModelCall { Category = "test", Purpose = "test", ProjectId = p.Id, SessionId = sid, AgentId = p.RootAgentId, Model = rt.Store.GetModel("deepseek-v4.1-flash")!, Messages = [ChatMessage.User("blocked")] }, CancellationToken.None));
		Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
		Assert.Equal(0, rt.Db.Read(x => x.ExecuteScalar<int>("SELECT COUNT(*) FROM workbook_runs")));
		Assert.True(rt.Maintenance.Status().VerifiedQuiescent);
		rt.Maintenance.Release(op.Operation!.Id, true);
	}
	sealed class AsyncProvider(Func<ProviderRequest, CancellationToken, Task<ProviderResponse>> handler) : IModelProvider {
		public string Id => "deepseek";
		public Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? delta, CancellationToken ct) => handler(request, ct);
	}
	sealed class BlockingTool(TaskCompletionSource entered, TaskCompletionSource release, Action cancellation) : BuiltinTool {
		public override string Name => "test_maintenance_block";
		public override string Description => "offline long noncooperative tool";
		public override JsonObject InputSchema => Schema.Object();
		public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
			using var registration = ctx.CancellationToken.Register(cancellation);
			entered.TrySetResult(); await release.Task;
			return ToolResult.Ok("complete");
		}
	}
}
