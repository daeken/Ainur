using Ainur.Core;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Dapper;
using Xunit;

namespace Ainur.Tests;

public sealed class BoundedModelTests {
	static ProviderRequest Request(TimeSpan? full = null, TimeSpan? idle = null, Action<Task>? unsettled = null) => new() {
		Model = new ModelInfo { Id = "test", Provider = "test" }, Messages = [],
		FullCallTimeout = full ?? TimeSpan.FromMilliseconds(200), StreamIdleTimeout = idle ?? TimeSpan.FromMilliseconds(70), OnUnsettled = unsettled,
	};
	static Task<ProviderResponse> Send(HttpClient http, ProviderRequest r, CancellationToken ct = default, Func<HttpRequestMessage, CancellationToken, Task>? headers = null) =>
		OpenAiResponsesProvider.SendResponsesAsync(http, "test", "http://local.invalid/test", r, new JsonObject(), null, ct, headers ?? ((_, _) => Task.CompletedTask));
	static HttpResponseMessage Response(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };

	[Theory]
	[InlineData("")]
	[InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial")]
	public async Task BodyIdleBoundsSilenceIncludingPartialLines(string prefix) {
		using var stream = new OpenTailStream(prefix);
		using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(stream))));
		var error = await Assert.ThrowsAsync<ProviderException>(() => Send(http, Request()).WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.True(error.MayHaveBilled); Assert.False(error.Retryable);
	}
	[Theory]
	[InlineData("data: {\"type\":\"response.completed\",\"response\":{}}\n\n", true)]
	[InlineData("data: [DONE]\n\n", false)]
	public async Task TerminalOpenTailDoesNotWaitForEof(string prefix, bool completed) {
		using var stream = new OpenTailStream(prefix);
		using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(stream))));
		if(completed) Assert.Equal("stop", (await Send(http, Request()).WaitAsync(TimeSpan.FromSeconds(3))).FinishReason);
		else Assert.True((await Assert.ThrowsAsync<ProviderException>(() => Send(http, Request()).WaitAsync(TimeSpan.FromSeconds(3)))).MayHaveBilled);
		Assert.Equal(1, stream.Reads); // no tail read after terminal/completion marker
	}
	[Fact]
	public async Task HeadersIgnoringCancellationAreBoundedButNotConfirmed() {
		var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
		var abandoned = new List<Task>();
		using var http = new HttpClient(new Handler((_, _) => pending.Task));
		var e = await Assert.ThrowsAsync<ProviderException>(() => Send(http, Request(unsettled: abandoned.Add)).WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.True(e.MayHaveBilled); Assert.Contains(abandoned, t => !t.IsCompleted);
		pending.SetResult(Response(new MemoryStream()));
	}
	[Fact]
	public async Task BodyIgnoringCancellationBoundsWaitAndReportsUnsettledRead() {
		using var stream = new IgnoringReadStream(); var abandoned = new List<Task>();
		using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(stream))));
		Assert.True((await Assert.ThrowsAsync<ProviderException>(() => Send(http, Request(unsettled: abandoned.Add)).WaitAsync(TimeSpan.FromSeconds(3)))).MayHaveBilled);
		Assert.Contains(abandoned, t => !t.IsCompleted); stream.Pending.SetResult(0);
	}
	[Fact]
	public async Task CredentialsIgnoringCancellationDoNotDispatch() {
		var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		int dispatched = 0;
		using var http = new HttpClient(new Handler((_, _) => { dispatched++; return Task.FromResult(Response(new MemoryStream())); }));
		var e = await Assert.ThrowsAsync<ProviderException>(() => Send(http, Request(), headers: (_, _) => pending.Task).WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.False(e.MayHaveBilled); Assert.Equal(0, dispatched);
		pending.SetResult();
	}
	[Fact]
	public async Task CallerCancellationWinsOverLateCompletedResponse() {
		using var cts = new CancellationTokenSource();
		using var http = new HttpClient(new Handler((_, _) => {
			cts.Cancel(); return Task.FromResult(Response(new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.completed\",\"response\":{}}\n\n"))));
		}));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Send(http, Request(), cts.Token));
	}
	[Fact]
	public async Task FullDeadlineBoundsContinuouslyProgressingBody() {
		using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new DripStream()))));
		var e = await Assert.ThrowsAsync<ProviderException>(() => Send(http, Request(full: TimeSpan.FromMilliseconds(200), idle: TimeSpan.FromSeconds(1))).WaitAsync(TimeSpan.FromSeconds(3)));
		Assert.True(e.MayHaveBilled);
	}
	[Fact]
	public async Task EofBeforeTerminalIsUnknownEvenAfterPartialOutput() {
		using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Response(new MemoryStream(Encoding.UTF8.GetBytes("data: {\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}\n\n"))))));
		Assert.True((await Assert.ThrowsAsync<ProviderException>(() => Send(http, Request()))).MayHaveBilled);
	}

	[Fact]
	public async Task TargetedStopFencesNoncooperativeModelAndLateToolThenRecreatesOnlyAfterSettlement() {
		using var world = new TempHome();
		var blocked = new BlockingProvider();
		using var rt = world.Runtime(blocked, o => o.ModelStreamIdleTimeout = TimeSpan.FromMilliseconds(20));

		var p = rt.CreateProject("bounded", "test", world.Workspace, managerModelId: "gpt-6-sol"); var a = rt.Store.GetAgent(p.RootAgentId!)!; rt.PostUserMessage(p.Id, "start");
		await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
		var host = Assert.Single(rt.LiveHosts, h => h.AgentId == a.Id);
		Assert.NotNull(host.ModelWaitStartedAt); Assert.NotNull(host.LastModelProgressAt);
		await Eventually(() => host.ModelWaitAged); Assert.True(host.ModelWaitAgeMs >= 20);
		var receipt = await rt.StopSessionAsync(host.SessionId, a.Id, rt.Generation, "test", TimeSpan.FromSeconds(1));
		Assert.True(receipt.LoopExitConfirmed); Assert.False(receipt.StopConfirmed); Assert.True(receipt.UnsettledOperations > 0);
		Assert.True(receipt.UncertainModelBilling);
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(a.Id)!.State);
		Assert.Throws<InvalidOperationException>(() => rt.ResumeAgent(a.Id, "test"));
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(host.SessionId, a.Id, rt.Generation, receipt.ReceiptId, "test", true));
		blocked.Pending.SetResult(new ProviderResponse { ToolCalls = [new("late", "powershell", "{\"script\":\"throw 'must not run'\"}")] });
		await Eventually(() => host.StopConfirmed);
		Assert.Empty(rt.Db.Read(c => c.Query<string>("SELECT id FROM tool_invocations WHERE session_id=@sid", new { sid = host.SessionId }).AsList()));
		Assert.DoesNotContain(rt.Store.Items(host.SessionId), i => i.Kind == ItemKinds.Assistant);
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(host.SessionId, a.Id, rt.Generation + 1, receipt.ReceiptId, "test", true));
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(host.SessionId, a.Id, rt.Generation, "wrong", "test", true));
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(host.SessionId, a.Id, rt.Generation, receipt.ReceiptId, "test"));
		Assert.True(rt.ResumeStoppedSession(host.SessionId, a.Id, rt.Generation, receipt.ReceiptId, "test", true).Resumed);
		Assert.NotSame(host, Assert.Single(rt.LiveHosts, h => h.AgentId == a.Id));
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(host.SessionId, a.Id, rt.Generation, receipt.ReceiptId, "test", true));
	}
	[Fact]
	public async Task UnknownToolBlocksResumeAndHostAbsenceIsNotStopProof() {
		using var world = new TempHome(); using var rt = world.Runtime(new FakeProvider((_, _) => FakeProvider.Text("done"), "openai"));
		var p = rt.CreateProject("unknown", "test", world.Workspace, managerModelId: "gpt-6-sol"); var a = rt.Store.GetAgent(p.RootAgentId!)!;
		var sid = a.PrimarySessionId!; var host = rt.GetHost(sid);
		var receipt = await rt.StopSessionAsync(sid, a.Id, rt.Generation, "test", TimeSpan.FromSeconds(1));
		rt.Db.Write(u => rt.Store.InsertInvocation(u, new ToolInvocation { Id = Ids.New("inv"), ProjectId = p.Id, SessionId = sid, AgentId = a.Id, CallId = "unknown", ToolName = "test", ToolVersion = "test@1", Arguments = "{}", State = InvocationStates.Unknown, CreatedAt = Clock.Now }));
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(sid, a.Id, rt.Generation, receipt.ReceiptId, "test", true));
		await Assert.ThrowsAsync<InvalidOperationException>(() => rt.StopSessionAsync("missing", a.Id, rt.Generation, "test", TimeSpan.Zero));
	}
	[Theory]
	[InlineData(true, 1)]
	[InlineData(false, 2)]
	public async Task SessionRetryOnlyWhenProvenNoBilling(bool mayHaveBilled, int expectedCalls) {
		using var world = new TempHome(); var failing = new FailingProvider(mayHaveBilled);
		using var rt = world.Runtime(failing);
		var p = rt.CreateProject("retry", "test", world.Workspace, managerModelId: "gpt-6-sol"); var a = rt.Store.GetAgent(p.RootAgentId!)!; rt.PostUserMessage(p.Id, "start");
		await Eventually(() => failing.Calls >= expectedCalls, TimeSpan.FromSeconds(6));
		if(mayHaveBilled) await Eventually(() => rt.IsPaused(a.PrimarySessionId!));
		Assert.Equal(expectedCalls, failing.Calls);
	}
	[Fact]
	public async Task GatewayFullDeadlinePreservesUnknownBillingAndMaintenanceUntilSettlement() {
		using var world = new TempHome(); var pending = new BlockingProvider();
		using var rt = world.Runtime(pending, o => o.ModelCallTimeout = TimeSpan.FromMilliseconds(150));
		 var p = rt.CreateProject("timeout", "test", world.Workspace, managerModelId: "gpt-6-sol"); var a = rt.Store.GetAgent(p.RootAgentId!)!; rt.PostUserMessage(p.Id, "start");
		await Eventually(() => rt.IsPaused(a.PrimarySessionId!));
		Assert.True(rt.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM model_requests WHERE state='unknown'")) > 0);
		Assert.NotEmpty(rt.Db.Read(c => c.Query<string>("SELECT id FROM maintenance_activities WHERE state='running'").AsList()));
		pending.Pending.SetResult(new ProviderResponse());
		await Eventually(() => rt.Db.Read(c => c.Query<string>("SELECT id FROM maintenance_activities WHERE state='running'").AsList()).Count == 0);
	}
	[Fact]
	public async Task RecreatePreexistingPausedHostDoesNotUnpauseIt() {
		var calls = 0;
		using var world = new TempHome(); using var rt = world.Runtime(new FakeProvider((_, _) => { Interlocked.Increment(ref calls); return FakeProvider.Text("done"); }, "openai"));
		var p = rt.CreateProject("paused", "test", world.Workspace, managerModelId: "gpt-6-sol");
		var a = rt.Store.GetAgent(p.RootAgentId!)!; var sid = a.PrimarySessionId!;
		rt.PauseAgent(a.Id, "test", "existing pause"); rt.GetHost(sid);
		var receipt = await rt.StopSessionAsync(sid, a.Id, rt.Generation, "test", TimeSpan.FromSeconds(1));
		Assert.True(receipt.WasPaused);
		Assert.True(rt.ResumeStoppedSession(sid, a.Id, rt.Generation, receipt.ReceiptId, "test").Resumed);
		Assert.True(rt.IsPaused(sid)); Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(a.Id)!.State);
		var replacement = rt.GetHost(sid); replacement.Wake();
		await Eventually(() => replacement.Status == "paused" && replacement.AtBoundary);
		Assert.Equal(0, Volatile.Read(ref calls));
		rt.ResumeAgent(a.Id, "test");
		rt.PostUserMessage(p.Id, "work after authorized release"); // new user input resumes by design
		await Eventually(() => rt.Store.Items(sid).Any(i => i.Kind == ItemKinds.Assistant));
	}
	[Fact]
	public async Task NewerOrdinaryPauseAfterConfirmedStopIsPreservedUntilSupportedResume() {
		var calls = 0;
		using var world = new TempHome(); using var rt = world.Runtime(new FakeProvider((_, _) => { Interlocked.Increment(ref calls); return FakeProvider.Text("done"); }, "openai"));
		var p = rt.CreateProject("newer hold", "test", world.Workspace, managerModelId: "gpt-6-sol");
		var a = rt.Store.GetAgent(p.RootAgentId!)!; var sid = a.PrimarySessionId!;
		rt.GetHost(sid);
		var receipt = await rt.StopSessionAsync(sid, a.Id, rt.Generation, "test", TimeSpan.FromSeconds(1));
		Assert.True(receipt.StopConfirmed); Assert.False(receipt.WasPaused);
		rt.PauseAgent(a.Id, null, "new deliberate hold after stop");
		Assert.True(rt.ResumeStoppedSession(sid, a.Id, rt.Generation, receipt.ReceiptId, "test").Resumed);
		Assert.True(rt.IsPaused(sid)); Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(a.Id)!.State);
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(sid, a.Id, rt.Generation, receipt.ReceiptId, "test"));
		var replacement = rt.GetHost(sid); replacement.Wake();
		await Eventually(() => replacement.Status == "paused" && replacement.AtBoundary);
		Assert.Equal(0, Volatile.Read(ref calls));
		rt.ResumeAgent(a.Id, "test");
		rt.PostUserMessage(p.Id, "work after separately authorized release");
		await Eventually(() => rt.Store.Items(sid).Any(i => i.Kind == ItemKinds.Assistant));
	}
	[Fact]
	public async Task CoordinatedPauseSurvivesReceiptAndRequiresItsRelease() {
		using var world = new TempHome(); using var rt = world.Runtime(new FakeProvider((_, _) => FakeProvider.Text("done"), "openai"));
		var p = rt.CreateProject("coordinated", "test", world.Workspace, managerModelId: "gpt-6-sol");
		var a = rt.Store.GetAgent(p.RootAgentId!)!; var sid = a.PrimarySessionId!;
		var pause = rt.RequestPause(sid, sid, "test", "deliberate coordinated hold", "explicit release", TimeSpan.FromMinutes(5));
		var receipt = await rt.StopSessionAsync(sid, a.Id, rt.Generation, "test", TimeSpan.FromSeconds(1));
		Assert.True(receipt.StopConfirmed); Assert.True(receipt.WasPaused);
		Assert.True(rt.ResumeStoppedSession(sid, a.Id, rt.Generation, receipt.ReceiptId, "test").Resumed);
		rt.PostUserMessage(p.Id, "held work");
		rt.ResumeAgent(a.Id, "test"); // ordinary release cannot bypass the independent durable token.
		Assert.NotNull(rt.ActivePause(sid)); Assert.True(rt.IsPaused(sid));
		Assert.DoesNotContain(rt.Store.Items(sid), i => i.Kind == ItemKinds.Assistant);
		rt.ReleasePause(pause.Id, a.Id, "authorized release");
		await Eventually(() => rt.Store.Items(sid).Any(i => i.Kind == ItemKinds.Assistant));
	}
	[Fact]
	public async Task NewerCoordinatedPauseBlocksReceiptUntilItsSupportedRelease() {
		using var world = new TempHome(); using var rt = world.Runtime(new FakeProvider((_, _) => FakeProvider.Text("done"), "openai"));
		var p = rt.CreateProject("newer coordinated", "test", world.Workspace, managerModelId: "gpt-6-sol");
		var a = rt.Store.GetAgent(p.RootAgentId!)!; var sid = a.PrimarySessionId!;
		rt.GetHost(sid);
		var receipt = await rt.StopSessionAsync(sid, a.Id, rt.Generation, "test", TimeSpan.FromSeconds(1));
		Assert.True(receipt.StopConfirmed); Assert.False(receipt.WasPaused);
		var pause = rt.RequestPause(sid, sid, "test", "new coordinated hold", "explicit release", TimeSpan.FromMinutes(5));
		var stoppedGuard = Assert.Throws<InvalidOperationException>(() => rt.PostUserMessage(p.Id, "must remain held"));
		Assert.Equal("Stopped sessions require the targeted receipt resume path", stoppedGuard.Message);
		var committed = Assert.Single(rt.Store.Conversation(p.Id), c => c.Author == "user" && c.Body == "must remain held");
		var notification = Assert.Single(rt.Store.ListNotifications(p.Id), n => n.Type == NotificationTypes.UserMessage && n.CausalParentId == committed.Id);
		Assert.Equal("conv:" + committed.Id, notification.DedupeKey);
		Assert.Throws<InvalidOperationException>(() => rt.ResumeStoppedSession(sid, a.Id, rt.Generation, receipt.ReceiptId, "test"));
		Assert.NotNull(rt.ActivePause(sid));
		Assert.DoesNotContain(rt.Store.Items(sid), i => i.Kind == ItemKinds.Assistant);
		rt.ReleasePause(pause.Id, a.Id, "authorized release");
		Assert.True(rt.ResumeStoppedSession(sid, a.Id, rt.Generation, receipt.ReceiptId, "test").Resumed);
		await Eventually(() => rt.Store.Items(sid).Any(i => i.Kind == ItemKinds.Assistant));
		Assert.Equal(committed.Id, Assert.Single(rt.Store.Conversation(p.Id), c => c.Author == "user" && c.Body == "must remain held").Id);
		Assert.Equal(notification.Id, Assert.Single(rt.Store.ListNotifications(p.Id), n => n.Type == NotificationTypes.UserMessage && n.CausalParentId == committed.Id).Id);
	}
	static async Task Eventually(Func<bool> f, TimeSpan? timeout = null) {
		var end = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
		while(!f() && DateTime.UtcNow < end) await Task.Delay(10);
		Assert.True(f(), "Condition did not settle within bounded test wait");
	}
	sealed class BlockingProvider : IModelProvider {
		public string Id => "openai";
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<ProviderResponse> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		int calls;
		public Task<ProviderResponse> CompleteAsync(ProviderRequest r, Action<StreamDelta>? d, CancellationToken ct) {
			if(Interlocked.Increment(ref calls) > 1) return Task.FromResult(new ProviderResponse { Content = "done" });
			Entered.TrySetResult(); return Pending.Task;
		}
	}
	sealed class FailingProvider(bool mayHaveBilled) : IModelProvider {
		public string Id => "openai"; public int Calls;
		public Task<ProviderResponse> CompleteAsync(ProviderRequest r, Action<StreamDelta>? d, CancellationToken ct) {
			Interlocked.Increment(ref Calls); throw new ProviderException("failure", retryable: true, mayHaveBilled: mayHaveBilled);
		}
	}
	sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct);
	}
	/// <summary>One prefix then a never-ending cooperative tail; deterministic no-EOF fixture.</summary>
	sealed class OpenTailStream(string prefix) : Stream {
		readonly byte[] bytes = Encoding.UTF8.GetBytes(prefix); int offset; public int Reads;
		public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) {
			Reads++;
			if(offset < bytes.Length) { var n = Math.Min(b.Length, bytes.Length - offset); bytes.AsMemory(offset, n).CopyTo(b); offset += n; return n; }
			await Task.Delay(Timeout.Infinite, ct); return 0;
		}
		public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
		public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
		public override void SetLength(long l) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
	}
	sealed class IgnoringReadStream : Stream {
		public TaskCompletionSource<int> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) => new(Pending.Task);
		public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
		public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
		public override void SetLength(long l) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
	}
	sealed class DripStream : Stream {
		public override async ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct = default) { await Task.Delay(10, ct); b.Span[0] = (byte)' '; return 1; }
		public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
		public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
		public override void SetLength(long l) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int n) => throw new NotSupportedException();
	}
}
