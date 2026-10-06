using System.Net;
using System.Text.Json.Nodes;
using Amazon.Runtime;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Xunit;

namespace Ainur.Tests;

/// <summary>Only scripted providers/HTTP/credentials/streams; cancellation is not proof of non-billing.</summary>
public sealed class CancellationContractTests {
	static readonly TimeSpan LiveTimer = TimeSpan.FromSeconds(10);
	static readonly TimeSpan OwnedTimer = TimeSpan.FromMilliseconds(80);
	static readonly TimeSpan TestBound = TimeSpan.FromSeconds(3);
	static ProviderRequest Request(string provider = "test", TimeSpan? full = null, TimeSpan? idle = null, Action<Task>? unsettled = null) => new() {
		Model = new ModelInfo { Id = "synthetic", Provider = provider, UpstreamModel = "synthetic", Billing = "api", Enabled = true },
		Messages = [ChatMessage.User("synthetic")], FullCallTimeout = full ?? LiveTimer,
		StreamIdleTimeout = idle ?? LiveTimer, OnUnsettled = unsettled,
	};
	static Task<ProviderResponse> Wire(HttpClient http, ProviderRequest request, CancellationToken ct = default,
		Func<HttpRequestMessage, CancellationToken, Task>? headers = null) => OpenAiResponsesProvider.SendResponsesAsync(
		http, "synthetic", "http://local.invalid/never-network", request, new JsonObject(), null, ct, headers ?? ((_, _) => Task.CompletedTask));
	static HttpResponseMessage Body(Stream stream) => new(HttpStatusCode.OK) { Content = new StreamContent(stream) };
	static ModelCall Call(string project, ModelInfo model) => new() {
		ProjectId = project, Model = model, Purpose = "cancellation-contract", Category = "synthetic", Messages = [ChatMessage.User("hi")],
	};
	static async Task ThrowIndependent(bool asynchronously) {
		if(asynchronously) await Task.Yield();
		throw new OperationCanceledException("independent scripted provider cancellation");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task GatewayIndependentCancellationPropagatesWithUnknownBillingAndNoFallback(bool asynchronously) {
		using var home = new TempHome();
		var provider = new ProbeProvider(async ct => { if(!asynchronously) throw new OperationCanceledException("independent synchronous cancellation"); await ThrowIndependent(true); return new(); });
		using var rt = home.Runtime(provider, o => o.ModelCallTimeout = LiveTimer, start: false);
		var project = rt.CreateProject("contract", "", home.Workspace);
		var primary = Request("deepseek").Model; primary.Billing = "subscription"; primary.FallbackModelId = "synthetic-fallback";
		var fallback = new ModelInfo { Id = "synthetic-fallback", Provider = "deepseek", Billing = "api", Enabled = true };
		rt.Db.Write(u => { rt.Store.UpsertModel(u, primary); rt.Store.UpsertModel(u, fallback); });
		using var caller = new CancellationTokenSource();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rt.Gateway.CallAsync(Call(project.Id, primary), caller.Token).WaitAsync(TestBound));
		Assert.False(caller.IsCancellationRequested); Assert.Equal(1, provider.Calls);
		Assert.Single(rt.Store.ModelRequestsInState("unknown")); Assert.Single(rt.Store.CostEvents(project.Id));
		Assert.DoesNotContain(rt.Store.Events(project.Id), e => e.Kind == "model.failover");
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public async Task GatewayCallerCancellationPropagatesBeforeOrDuringProviderWaitWithoutFallback(bool preCanceled) {
		using var caller = new CancellationTokenSource(); if(preCanceled) caller.Cancel();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var provider = new ProbeProvider(async ct => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return new(); });
		using var home = new TempHome(); using var rt = home.Runtime(provider, o => o.ModelCallTimeout = LiveTimer, start: false);
		var project = rt.CreateProject("caller", "", home.Workspace); var model = Request("deepseek").Model;
		var operation = rt.Gateway.CallAsync(Call(project.Id, model), caller.Token);
		if(!preCanceled) { await entered.Task.WaitAsync(TestBound); caller.Cancel(); }
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestBound));
		Assert.True(provider.Calls <= 1); Assert.DoesNotContain(rt.Store.Events(project.Id), e => e.Kind == "model.failover");
		// Four filters do NOT establish zero admission/intent/reservation/provider entry for pre-canceled callers.
		Assert.DoesNotContain(rt.Store.ModelRequestsInState("unknown"), r => r.Error?.Contains("deadline exceeded") == true);
	}

	[Fact]
	public async Task GatewayOwnedDeadlineAllowsFinderFallbackButIndependentCancellationDoesNot() {
		using var home = new TempHome(); var entered = 0;
		var provider = new ProbeProvider(async ct => { Interlocked.Increment(ref entered); await Task.Delay(Timeout.Infinite, ct); return new(); }, "deepseek");
		using var rt = home.Runtime(provider, o => o.ModelCallTimeout = OwnedTimer, start: false);
		var project = rt.CreateProject("finder deadline", "", home.Workspace); var agent = rt.Store.GetAgent(project.RootAgentId!)!;
		var session = rt.Store.GetSession(agent.PrimarySessionId!)!;
		var context = new ToolContext { Runtime = rt, Project = project, Agent = agent, Session = session,
			Host = rt.GetHost(session.Id)!, InvocationId = "owned-deadline-finder-contract", CancellationToken = default };
		var result = await new FindToolsTool().InvokeAsync(context, new JsonObject { ["need"] = "search files", ["load"] = false }).WaitAsync(TestBound);
		Assert.False(result.IsError); Assert.Contains("keyword fallback", result.Text);
		Assert.IsType<List<string>>(result.Value);
		Assert.Equal(1, entered); Assert.Equal(1, provider.Calls);
		Assert.Contains(rt.Store.ModelRequestsInState("unknown"), r => r.Error?.Contains("deadline exceeded") == true);
		Assert.Single(rt.Store.CostEvents(project.Id));
	}

	[Fact]
	public async Task GatewayCallerWinsWhenOwnedTimerAndCallerAreBothCanceled() {
		using var caller = new CancellationTokenSource();
		var pending = new TaskCompletionSource<ProviderResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		var provider = new ProbeProvider(ct => { ct.Register(caller.Cancel); return pending.Task; });
		using var home = new TempHome(); using var rt = home.Runtime(provider, o => o.ModelCallTimeout = OwnedTimer, start: false);
		var project = rt.CreateProject("gateway race", "", home.Workspace);
		try {
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rt.Gateway.CallAsync(Call(project.Id, Request("deepseek").Model), caller.Token).WaitAsync(TestBound));
			Assert.True(caller.IsCancellationRequested); Assert.Equal(1, provider.Calls);
			Assert.Single(rt.Store.ModelRequestsInState("unknown")); Assert.Single(rt.Store.CostEvents(project.Id));
		} finally { pending.TrySetResult(new()); }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SharedCredentialOrHeaderIndependentCancellationDoesNotInventDeadline(bool asynchronously) {
		var sends = 0;
		using var http = new HttpClient(new ProbeHandler((_, _) => { sends++; return Task.FromResult(Body(new MemoryStream())); }));
		using var caller = new CancellationTokenSource();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wire(http, Request(), caller.Token,
			(_, _) => asynchronously ? ThrowIndependent(true) : throw new OperationCanceledException("independent header cancellation")).WaitAsync(TestBound));
		Assert.False(caller.IsCancellationRequested); Assert.Equal(0, sends);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SharedHttpHeaderIndependentCancellationPropagatesWithoutRetry(bool asynchronously) {
		var sends = 0;
		using var http = new HttpClient(new ProbeHandler(async (_, _) => { sends++; if(!asynchronously) throw new OperationCanceledException("independent HTTP cancellation"); await ThrowIndependent(true); return Body(new MemoryStream()); }));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wire(http, Request()).WaitAsync(TestBound)); Assert.Equal(1, sends);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task IdleAndSharedBodyIndependentCancellationPropagateWhileTimersLive(bool asynchronously) {
		using var inner = new ScriptedReadStream(ct => asynchronously ? Task.FromException<int>(new OperationCanceledException("independent async read")) : throw new OperationCanceledException("independent sync read"));
		using(var idle = new IdleReadStream(inner, Request(), default))
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle.ReadAsync(new byte[8]).AsTask());
		using var body = new ScriptedReadStream(ct => asynchronously ? Task.FromException<int>(new OperationCanceledException("independent async body")) : throw new OperationCanceledException("independent sync body"));
		using var http = new HttpClient(new ProbeHandler((_, _) => Task.FromResult(Body(body))));
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wire(http, Request()).WaitAsync(TestBound)); Assert.Equal(1, body.Reads);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task OpenAiAdapterIndependentCancellationInCredentialOrTransportPropagates(bool transport) {
		var sends = 0;
		using var http = new HttpClient(new ProbeHandler((_, _) => { sends++; return Task.FromException<HttpResponseMessage>(new OperationCanceledException("independent adapter HTTP")); }));
		var provider = new OpenAiResponsesProvider(http, apiKey: () => transport ? "synthetic" : throw new OperationCanceledException("independent key callback"), codexAuthPath: () => null, routeOverride: "api");
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request("openai"), null, default).WaitAsync(TestBound));
		Assert.Equal(transport ? 1 : 0, sends);
	}

	[Fact]
	public async Task BedrockSyntheticCredentialCancellationPropagatesWithoutDispatch() {
		var sends = 0;
		using var http = new HttpClient(new ProbeHandler((_, _) => { sends++; return Task.FromResult(Body(new MemoryStream())); }));
		var provider = new BedrockResponsesProvider(http, "us-west-2", credentials: () => new CanceledCredentials());
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteAsync(Request("bedrock"), null, default).WaitAsync(TestBound));
		Assert.Equal(0, sends);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task SharedPreCanceledAndInflightCallerCancellationRemainCancellation(bool preCanceled) {
		using var caller = new CancellationTokenSource(); if(preCanceled) caller.Cancel();
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var sends = 0;
		using var http = new HttpClient(new ProbeHandler(async (_, ct) => { sends++; entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return Body(new MemoryStream()); }));
		var operation = Wire(http, Request(), caller.Token);
		if(!preCanceled) { await entered.Task.WaitAsync(TestBound); caller.Cancel(); }
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TestBound)); Assert.True(sends <= 1);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task IdleCallerOrReadTokenWinsControlledTimerRace(bool useReadToken) {
		using var caller = new CancellationTokenSource(); using var read = new CancellationTokenSource();
		using var stream = new ScriptedReadStream(ct => { ct.Register(() => { if(useReadToken) read.Cancel(); else caller.Cancel(); }); return Task.Delay(Timeout.Infinite, ct).ContinueWith<int>(_ => throw new OperationCanceledException(ct), TaskScheduler.Default); });
		using var idle = new IdleReadStream(stream, Request(idle: OwnedTimer), caller.Token);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => idle.ReadAsync(new byte[8], read.Token).AsTask().WaitAsync(TestBound));
		Assert.True(useReadToken ? read.IsCancellationRequested : caller.IsCancellationRequested); Assert.Equal(1, stream.Reads);
	}

	[Fact]
	public async Task SharedCallerWinsControlledFullTimerRaceAndReportsUnsettledOperation() {
		using var caller = new CancellationTokenSource(); var unsettled = new List<Task>();
		var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); var sends = 0;
		using var http = new HttpClient(new ProbeHandler((_, ct) => { sends++; ct.Register(caller.Cancel); return pending.Task; }));
		try {
			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Wire(http, Request(full: OwnedTimer, unsettled: unsettled.Add), caller.Token).WaitAsync(TestBound));
			Assert.True(caller.IsCancellationRequested); Assert.Equal(1, sends); Assert.Contains(unsettled, task => !task.IsCompleted);
		} finally { pending.TrySetResult(Body(new MemoryStream())); }
	}

	[Fact]
	public async Task OwnedIdleAndCredentialDeadlinesStayNonRetryableAndConservative() {
		using(var stream = new ScriptedReadStream(async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }))
		using(var idle = new IdleReadStream(stream, Request(idle: OwnedTimer), default)) {
			var error = await Assert.ThrowsAsync<ProviderException>(() => idle.ReadAsync(new byte[8]).AsTask().WaitAsync(TestBound));
			Assert.True(error.MayHaveBilled); Assert.False(error.Retryable); Assert.Contains("inactivity", error.Message);
		}
		var sends = 0; var unsettled = new List<Task>(); var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var http = new HttpClient(new ProbeHandler((_, _) => { sends++; return Task.FromResult(Body(new MemoryStream())); }));
		try {
			var error = await Assert.ThrowsAsync<ProviderException>(() => Wire(http, Request(full: OwnedTimer, unsettled: unsettled.Add), headers: (_, _) => pending.Task).WaitAsync(TestBound));
			Assert.False(error.MayHaveBilled); Assert.False(error.Retryable); Assert.Equal(0, sends); Assert.Contains(unsettled, task => !task.IsCompleted);
		} finally { pending.TrySetResult(); }
	}

	sealed class ProbeProvider(Func<CancellationToken, Task<ProviderResponse>> answer, string id = "deepseek") : IModelProvider {
		public string Id => id; public int Calls;
		public Task<ProviderResponse> CompleteAsync(ProviderRequest r, Action<StreamDelta>? delta, CancellationToken ct) { Interlocked.Increment(ref Calls); return answer(ct); }
	}
	sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => send(r, ct);
	}
	sealed class CanceledCredentials : AWSCredentials {
		public override ImmutableCredentials GetCredentials() => throw new OperationCanceledException("synthetic credential cancellation");
		public override async Task<ImmutableCredentials> GetCredentialsAsync() { await Task.Yield(); throw new OperationCanceledException("synthetic asynchronous credential cancellation"); }
	}
	sealed class ScriptedReadStream(Func<CancellationToken, Task<int>> read) : Stream {
		public int Reads;
		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { Interlocked.Increment(ref Reads); return new(read(ct)); }
		public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
		public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
		public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
		public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
		public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
		public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
	}
}
