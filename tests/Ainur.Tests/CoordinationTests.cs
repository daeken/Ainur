using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Runtime;

namespace Ainur.Tests;

public class CoordinationTests {
	[Fact]
	public async Task PausedSessionWaitsUntilReleaseThenResumesWithSummary() {
		using var home = new TempHome();
		var specialistCalls = 0;
		var provider = new FakeProvider((req, n) => {
			if(req.Messages[0].Content!.Contains("You are Tulkas")) {
				Interlocked.Increment(ref specialistCalls);
				return FakeProvider.Text("ok");
			}
			return FakeProvider.Text("");
		});
		using var rt = home.Runtime(provider);
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var tulkas = rt.CreateAgent(p.Id, new NewAgent { Name = "Tulkas", Title = "Worker", ManagerId = root.Id }, root.Id);
		var pause = rt.RequestPause(root.PrimarySessionId!, tulkas.PrimarySessionId!, "src/*", "interface change", "after integration", TimeSpan.FromMinutes(5));
		await Wait.Until(() => rt.GetPause(pause.Id)!.State == "acknowledged", TimeSpan.FromSeconds(5), "ack");
		rt.Notify(p.Id, NotificationTypes.Assignment, root.Id, tulkas.Id, "do work");
		await Task.Delay(500);
		Assert.Equal(0, specialistCalls);
		rt.ReleasePause(pause.Id, root.Id, "IModelProvider gained a Name property.");
		await Wait.Until(() => specialistCalls > 0, TimeSpan.FromSeconds(10), "resume");
		var inbox = provider.Requests.Last(r => r.Messages[0].Content!.Contains("You are Tulkas")).Messages.Last(m => m.Role == "user").Content!;
		Assert.Contains("resume", inbox);
		Assert.Contains("IModelProvider gained a Name property.", inbox);
		Assert.Contains("do work", inbox);
	}

	[Fact]
	public async Task ExpiredPauseIsRevokedAndReported() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("")), o => o.AutoStartHosts = false);
		var p = rt.CreateProject("T", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var worker = rt.CreateAgent(p.Id, new NewAgent { Name = "Oromë", Title = "Worker", ManagerId = root.Id }, root.Id);
		var pause = rt.RequestPause(root.PrimarySessionId!, worker.PrimarySessionId!, "db schema", "migration", "after migration", TimeSpan.FromMilliseconds(50));
		Assert.True(rt.IsPaused(worker.PrimarySessionId!));
		await Task.Delay(100);
		rt.ExpirePauses();
		Assert.Equal("expired", rt.GetPause(pause.Id)!.State);
		Assert.False(rt.IsPaused(worker.PrimarySessionId!));
		Assert.Contains(rt.Store.ListNotifications(p.Id), n => n.ToAgentId == worker.Id && n.Type == NotificationTypes.Resume && n.Body.Contains("expired"));
	}
}
