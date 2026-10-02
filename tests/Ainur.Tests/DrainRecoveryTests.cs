using System.Text.Json.Nodes;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;

namespace Ainur.Tests;

public class DrainRecoveryTests {
	sealed class HeldTool : BuiltinTool {
		public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public override string Name => "powershell";
		public override string Version => "drain-test-held-tool";
		public override string Description => "Offline test barrier";
		public override JsonObject InputSchema => new() { ["type"] = "object" };
		public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
			Entered.TrySetResult();
			await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), ctx.CancellationToken);
			return ToolResult.Ok("tool checkpoint completed");
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task FailedDrainRestoresDeliveryWithoutDuplicatingHeldTool(bool cancel) {
		using var home = new TempHome();
		var provider = new FakeProvider((_, n) => n == 1 ? FakeProvider.Call("powershell", "{}") : FakeProvider.Text("recovered"));
		using var rt = home.Runtime(provider);
		var held = new HeldTool();
		rt.Tools.Register(held);
		var project = rt.CreateProject("Drain", "test", home.Workspace);
		var session = rt.Store.GetAgent(project.RootAgentId!)!.PrimarySessionId!;
		rt.PostUserMessage(project.Id, "first");
		await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		try {
			using var cts = new CancellationTokenSource();
			var drain = rt.DrainWithStatusAsync(cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(150), cts.Token);
			Assert.True(rt.Draining);
			rt.PostUserMessage(project.Id, "queued while draining");
			rt.PauseAgent(project.RootAgentId!, null, "test operator pause");
			rt.ResumeAgent(project.RootAgentId!, null); // Wake is suppressed until failed-drain cleanup.
			if(cancel) {
				cts.Cancel();
				await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drain);
			} else {
				var outcome = await drain;
				Assert.False(outcome.Drained);
				Assert.Contains(session, outcome.Running);
			}
			Assert.False(rt.Draining);
			Assert.Single(provider.Requests);
			rt.Undrain();
			rt.Undrain();
		} finally { held.Release.TrySetResult(); }
		await Wait.Until(() => rt.Store.Conversation(project.Id).Any(m => m.Body == "recovered") && !rt.GetHost(session)!.IsRunning,
			TimeSpan.FromSeconds(10), "recovered reply");
		Assert.Equal(2, provider.Requests.Count);
		Assert.Empty(rt.Store.PendingNotifications(project.RootAgentId!));
		Assert.Equal(2, rt.Store.CostEvents(project.Id).Count);
	}

	[Fact]
	public async Task SuccessfulDrainWaitsForToolCheckpointAndKeepsNewWorkQuiescent() {
		using var home = new TempHome();
		var provider = new FakeProvider((_, n) => n == 1 ? FakeProvider.Call("powershell", "{}") : FakeProvider.Text("after undrain"));
		using var rt = home.Runtime(provider);
		var held = new HeldTool();
		rt.Tools.Register(held);
		var project = rt.CreateProject("Drain", "test", home.Workspace);
		var session = rt.Store.GetAgent(project.RootAgentId!)!.PrimarySessionId!;
		rt.PostUserMessage(project.Id, "first");
		await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		try {
			var drain = rt.DrainWithStatusAsync(TimeSpan.FromSeconds(10));
			await Assert.ThrowsAsync<DomainException>(() => rt.DrainAsync(TimeSpan.Zero));
			Assert.True(rt.Draining);
			held.Release.TrySetResult();
			Assert.True((await drain).Drained);
			Assert.False(rt.GetHost(session)!.IsRunning);
			Assert.True(rt.Draining);
			rt.PostUserMessage(project.Id, "queued after successful drain");
			await Task.Delay(150);
			Assert.Single(provider.Requests);
			rt.Undrain();
			await Wait.Until(() => rt.Store.Conversation(project.Id).Any(m => m.Body == "after undrain") && !rt.GetHost(session)!.IsRunning, TimeSpan.FromSeconds(10), "resumed reply");
			Assert.Equal(2, provider.Requests.Count);
		} finally { held.Release.TrySetResult(); }
	}

	[Fact]
	public async Task SupersededDrainCannotUndoNewSuccessfulDrain() {
		using var home = new TempHome();
		var provider = new FakeProvider((_, n) => n == 1 ? FakeProvider.Call("powershell", "{}") : FakeProvider.Text("done"));
		using var rt = home.Runtime(provider);
		var held = new HeldTool();
		rt.Tools.Register(held);
		var project = rt.CreateProject("Drain", "test", home.Workspace);
		rt.PostUserMessage(project.Id, "first");
		await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
		try {
			var first = rt.DrainWithStatusAsync(TimeSpan.FromSeconds(10));
			rt.Undrain();
			var second = rt.DrainWithStatusAsync(TimeSpan.FromSeconds(10));
			held.Release.TrySetResult();
			Assert.False((await first).Drained);
			Assert.True((await second).Drained);
			Assert.True(rt.Draining);
			Assert.Single(provider.Requests);
		} finally { held.Release.TrySetResult(); }
	}

	[Fact]
	public async Task ExceptionDuringDrainRestoresAdmission() {
		using var home = new TempHome();
		using var rt = home.Runtime();
		await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rt.DrainAsync(TimeSpan.MaxValue));
		Assert.False(rt.Draining);
		Assert.True(await rt.DrainAsync(TimeSpan.Zero));
	}

	[Fact]
	public async Task AlreadyCanceledDrainRestoresAdmissionBeforeQuiescence() {
		using var home = new TempHome();
		using var rt = home.Runtime();
		using var cts = new CancellationTokenSource();
		cts.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rt.DrainAsync(TimeSpan.Zero, cts.Token));
		Assert.False(rt.Draining);
		Assert.True(await rt.DrainAsync(TimeSpan.Zero));
		Assert.True(rt.Draining);
	}

	[Fact]
	public async Task UndrainReconcilesHostlessWorkAndPreservesOperatorPause() {
		using var home = new TempHome();
		var provider = new FakeProvider((_, _) => FakeProvider.Text("delivered"));
		using var rt = home.Runtime(provider);
		Assert.True(await rt.DrainAsync(TimeSpan.Zero));
		var project = rt.CreateProject("Drain", "test", home.Workspace);
		rt.PostUserMessage(project.Id, "queued without a host");
		rt.PauseAgent(project.RootAgentId!, null, "test operator pause");
		rt.Undrain();
		rt.Undrain();
		await Task.Delay(150);
		Assert.Empty(provider.Requests);
		Assert.NotEmpty(rt.Store.PendingNotifications(project.RootAgentId!));
		rt.ResumeAgent(project.RootAgentId!, null);
		await Wait.Until(() => rt.Store.Conversation(project.Id).Any(m => m.Body == "delivered"), TimeSpan.FromSeconds(10), "delivered reply");
		Assert.Single(provider.Requests);
		Assert.Empty(rt.Store.PendingNotifications(project.RootAgentId!));
	}

	[Fact]
	public async Task ImageReceiptSurvivesDrainTitleCasAndOperatorPauseWithoutDuplicateDispatch() {
		using var home = new TempHome();
		using var rt = new AinurRuntime(new RuntimeOptions { Home = home.Path, AutoStartHosts = false }, OfflineUiProviders.Create(home.Path));
		rt.Start("offline-composition-test");
		Assert.True(await rt.DrainAsync(TimeSpan.Zero));
		var project = rt.CreateProject("image-drain", "", home.Workspace, managerModelId: "gpt-6-astra", noEffectiveLimit: true, cashCeilingDollars: 50);
		var agent = rt.Store.GetAgent(project.RootAgentId!)!;
		var image = rt.UploadConversationImage(project.Id, ConversationImageTests.Png(), "image/png");
		var receipt = rt.PostUserMessage(project.Id, "", [image.Id], "drained-image");
		rt.SetAgentTitle(null, agent.Id, "Image operator", agent.Title);
		Assert.Throws<DomainException>(() => rt.SetAgentTitle(null, agent.Id, "stale", agent.Title));
		rt.PauseAgent(agent.Id, null, "operator pause after accepted image");
		// Idempotent replay must not repeat the fresh-message resume/wake side effect.
		Assert.Equal(receipt.Id, rt.PostUserMessage(project.Id, "", [image.Id], "drained-image").Id);
		rt.Undrain(); rt.Undrain();
		await Task.Delay(150);
		Assert.True(rt.IsPaused(agent.PrimarySessionId!));
		Assert.Empty(rt.Store.CostEvents(project.Id));
		Assert.Single(rt.Store.PendingNotifications(agent.Id), n => n.Type == "user_message");
		rt.ResumeAgent(agent.Id, null);
		await Wait.Until(() => rt.Store.Conversation(project.Id).Any(m => m.Body.Contains("received 1 image input(s)")) && !rt.GetHost(agent.PrimarySessionId!)!.IsRunning,
			TimeSpan.FromSeconds(10), "synthetic image reply after undrain");
		Assert.Equal("Image operator", rt.Store.GetAgent(agent.Id)!.Title);
		Assert.Single(rt.Store.CostEvents(project.Id));
		Assert.Equal(0, rt.Ledger.Summary(project.Id).CashKnownNanos);
		Assert.Empty(rt.Store.PendingNotifications(agent.Id));
		Assert.Equal(image.Id, Assert.Single(rt.Store.Conversation(project.Id).Single(m => m.Id == receipt.Id).Attachments).Id);
	}

	[Fact]
	public async Task UndrainStartsUnpausedHostlessWorkExactlyOnce() {
		using var home = new TempHome();
		var provider = new FakeProvider((_, _) => FakeProvider.Text("delivered"));
		using var rt = home.Runtime(provider);
		Assert.True(await rt.DrainAsync(TimeSpan.Zero));
		var project = rt.CreateProject("Drain", "test", home.Workspace);
		rt.PostUserMessage(project.Id, "hostless");
		Parallel.For(0, 8, _ => rt.Undrain());
		await Wait.Until(() => rt.Store.Conversation(project.Id).Any(m => m.Body == "delivered"), TimeSpan.FromSeconds(10), "delivered reply");
		Assert.Single(provider.Requests);
		Assert.Single(rt.LiveHosts);
	}
}
