using System.Text.Json.Nodes;
using Dapper;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Context;
using Ainur.Core.Tools;

namespace Ainur.Tests;

/// <summary>Provider-free barriers; no network, child process, or live runtime.</summary>
public class LifecycleTruthTests {
	sealed class HeldProvider : IModelProvider {
		public string Id => "openai";
		public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public int Calls;
		public async Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) {
			var call = Interlocked.Increment(ref Calls);
			Entered.TrySetResult();
			await Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); // deliberately ignore cancellation: late provider response
			if(call > 1) return new ProviderResponse { Content = "handled after resume", Usage = new Usage { InputTokens = 12, OutputTokens = 6 } }; 
			return new ProviderResponse { ToolCalls = [new ToolCall("late-call", "write_file", "{\"path\":\"late.txt\",\"content\":\"oops\"}")], Usage = new Usage { InputTokens = 12, OutputTokens = 6 } };
		}
	}

	sealed class InterruptedTool : BuiltinTool {
		public int Calls;
		public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override string Name => "powershell";
		public override string Version => "truth-interrupted-tool";
		public override string Description => "Test only: outcome of interrupted admitted code cannot be assumed.";
		public override JsonObject InputSchema => new() { ["type"] = "object" };
		public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
			Interlocked.Increment(ref Calls);
			Entered.TrySetResult();
			await Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); // deliberately ignores cancellation
			return ToolResult.Ok("side effect possibly completed");
		}
	}

	[Fact]
	public async Task LateProviderResponseAfterStopIsBilledButNeverAdmitsToolOrAssistant() {
		using var home = new TempHome();
		var provider = new HeldProvider();
		using var rt = home.Runtime(provider);
		var project = rt.CreateProject("Stop", "test", home.Workspace, managerModelId: "gpt-6-sol");
		var agent = rt.Store.GetAgent(project.RootAgentId!)!;
		rt.PostUserMessage(project.Id, "start");
		await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		var host = rt.GetHost(agent.PrimarySessionId!)!;
		host.Stop();
		Assert.True(host.StopRequested);
		Assert.False(host.StopConfirmed); // request != confirmed loop exit
		provider.Release.TrySetResult();
		await Wait.Until(() => host.StopConfirmed, TimeSpan.FromSeconds(10), "host stop confirmation");
		Assert.False(File.Exists(Path.Combine(home.Workspace, "late.txt")));
		Assert.DoesNotContain(rt.Store.Items(host.SessionId), item => item.Kind == ItemKinds.Assistant);
		Assert.True(rt.Store.CostEvents(project.Id).Any());
		Assert.Contains(rt.Store.Items(host.SessionId), item => item.Kind == ItemKinds.User);
		Assert.Contains(rt.Store.Events(project.Id), e => e.Kind == "session.response_discarded" || e.Kind == "model.failed"); // bounded cancel may win before late response
		Assert.Empty(rt.Db.Read(c => c.Query<string>("SELECT id FROM tool_invocations WHERE session_id=@sid", new { sid = host.SessionId }).AsList()));
	}

	[Fact]
	public async Task PauseResumeInvalidatesLateProviderResponseWithoutDuplicateToolRun() {
		using var home = new TempHome();
		var provider = new HeldProvider();
		using var rt = home.Runtime(provider);
		var project = rt.CreateProject("Pause", "test", home.Workspace, managerModelId: "gpt-6-sol");
		var agent = rt.Store.GetAgent(project.RootAgentId!)!;
		rt.PostUserMessage(project.Id, "start");
		await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		rt.PauseAgent(agent.Id, null, "test");
		rt.ResumeAgent(agent.Id, null);
		provider.Release.TrySetResult();
		await Wait.Until(() => rt.Store.Events(project.Id).Any(e => e.Kind == "session.response_discarded"), TimeSpan.FromSeconds(10), "discard late step");
		Assert.False(File.Exists(Path.Combine(home.Workspace, "late.txt")));
		Assert.DoesNotContain(rt.Store.Items(agent.PrimarySessionId!), item => item.Kind == ItemKinds.ToolResult);
		await Wait.Until(() => provider.Calls >= 2, TimeSpan.FromSeconds(10), "resumed durable inbox");
		Assert.Equal(2, provider.Calls); // resumed durable inbox once; discarded tool never dispatched
	}

	[Fact]
	public void RecoveryDistinguishesQueuedNotExecutedFromRunningUnknownWithoutReplay() {
		using var home = new TempHome();
		string session, queuedId, runningId;
		using(var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), o => o.AutoStartHosts = false)) {
			var project = rt.CreateProject("Recovery", "test", home.Workspace);
			var agent = rt.Store.GetAgent(project.RootAgentId!)!;
			session = agent.PrimarySessionId!;
			queuedId = "inv_queued";
			runningId = "inv_running";
			rt.Db.Write(u => {
				rt.Store.AppendItem(u, session, ItemKinds.User, new UserPayload { Text = "work" }, 4);
				rt.Store.AppendItem(u, session, ItemKinds.Assistant, new AssistantPayload { ToolCalls = [
					new ToolCall("queued", "read_file", "{}"), new ToolCall("running", "powershell", "{}"),
				] }, 15);
				rt.Store.InsertInvocation(u, new ToolInvocation { Id = queuedId, ProjectId = project.Id, SessionId = session, AgentId = agent.Id,
					CallId = "queued", ToolName = "read_file", ToolVersion = "test", State = InvocationStates.Queued, CreatedAt = 1 });
				rt.Store.InsertInvocation(u, new ToolInvocation { Id = runningId, ProjectId = project.Id, SessionId = session, AgentId = agent.Id,
					CallId = "running", ToolName = "powershell", ToolVersion = "test", State = InvocationStates.Running, CreatedAt = 2 });
			});
		}
		using var recovered = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), o => o.AutoStartHosts = false);
		Assert.Equal(InvocationStates.Canceled, recovered.Store.GetInvocation(queuedId)!.State);
		Assert.Equal(InvocationStates.Unknown, recovered.Store.GetInvocation(runningId)!.State);
		var results = recovered.Store.Items(session).Where(i => i.Kind == ItemKinds.ToolResult)
			.Select(i => Ainur.Core.JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!).ToArray();
		Assert.Equal(2, results.Length);
		Assert.Contains(results, r => r.CallId == "queued" && r.Text.StartsWith("NOT EXECUTED"));
		Assert.Contains(results, r => r.CallId == "running" && r.Text.StartsWith("OUTCOME UNKNOWN"));
	}

	[Theory]
	[InlineData(InvocationStates.Succeeded)]
	[InlineData(InvocationStates.Failed)]
	public void RecoveryOfAdmittedTerminalInvocationMissingToolResultNeverClaimsNoEffect(string priorState) {
		using var home = new TempHome();
		string session;
		using(var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), o => o.AutoStartHosts = false)) {
			var project = rt.CreateProject("Terminal tool result crash window", "test", home.Workspace);
			var agent = rt.Store.GetAgent(project.RootAgentId!)!;
			session = agent.PrimarySessionId!;
			rt.Db.Write(u => {
				rt.Store.AppendItem(u, session, ItemKinds.User, new UserPayload { Text = "work" }, 4);
				rt.Store.AppendItem(u, session, ItemKinds.Assistant, new AssistantPayload {
					ToolCalls = [new ToolCall("admitted-without-result", "write_file", "{}")],
				}, 9);
				rt.Store.InsertInvocation(u, new ToolInvocation { Id = "inv_admitted", ProjectId = project.Id, SessionId = session, AgentId = agent.Id,
					CallId = "admitted-without-result", ToolName = "write_file", ToolVersion = "test", State = priorState,
					CreatedAt = 1, StartedAt = 2, FinishedAt = 3 });
			});
		}
		using var recovered = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), o => o.AutoStartHosts = false);
		Assert.Equal(priorState, recovered.Store.GetInvocation("inv_admitted")!.State); // terminal state remains recorded
		var result = recovered.Store.Items(session).Where(i => i.Kind == ItemKinds.ToolResult)
			.Select(i => Ainur.Core.JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!).Single();
		Assert.Equal("inv_admitted", result.InvocationId);
		Assert.StartsWith("OUTCOME UNKNOWN", result.Text);
		Assert.DoesNotContain("NOT EXECUTED", result.Text);
		Assert.DoesNotContain("It had no effect", result.Text);
	}

	[Fact]
	public async Task CanceledToolAdmissionNeverRunsCodeOrCreatesInvocation() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("unused")), o => o.AutoStartHosts = false);
		var tool = new InterruptedTool();
		rt.Tools.Register(tool);
		var project = rt.CreateProject("Admission", "test", home.Workspace);
		var session = rt.Store.GetAgent(project.RootAgentId!)!.PrimarySessionId!;
		var host = rt.GetHost(session)!;
		using var canceled = new CancellationTokenSource();
		canceled.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.InvokeNestedAsync("powershell", new JsonObject(), null, canceled.Token));
		Assert.Equal(0, tool.Calls);
		Assert.Empty(rt.Store.InvocationsInState(InvocationStates.Running, InvocationStates.Unknown));
	}

	[Fact]
	public async Task CanceledAdmittedToolReportsUnknownSideEffectInsteadOfSuccess() {
		using var home = new TempHome();
		var provider = new FakeProvider((_, n) => n == 1 ? FakeProvider.Call("powershell", "{}") : FakeProvider.Text("done"));
		using var rt = home.Runtime(provider);
		var held = new InterruptedTool();
		rt.Tools.Register(held);
		var project = rt.CreateProject("Tool", "test", home.Workspace, managerModelId: "deepseek-v4.1-flash");
		var agent = rt.Store.GetAgent(project.RootAgentId!)!;
		rt.PostUserMessage(project.Id, "start");
		await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		var host = rt.GetHost(agent.PrimarySessionId!)!;
		host.Stop();
		Assert.False(host.StopConfirmed);
		held.Release.TrySetResult();
		await Wait.Until(() => host.StopConfirmed, TimeSpan.FromSeconds(10), "stopped after tool");
		Assert.Equal(1, held.Calls);
		var result = rt.Store.Items(host.SessionId).Where(i => i.Kind == ItemKinds.ToolResult).Select(i => i.Payload).Last();
		Assert.Contains("OUTCOME UNKNOWN", result);
		Assert.Contains(rt.Store.InvocationsInState(InvocationStates.Unknown), i => i.SessionId == host.SessionId);
	}
}
