using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Runtime;

namespace Ainur.Tests;

public class WorktreeTests {
	static void Git(string cwd, params string[] args) {
		var r = Worktrees.Git(cwd, args);
		Assert.True(r.ExitCode == 0, r.Output);
	}

	static void InitRepo(string ws) {
		Git(ws, "init", "-q");
		Git(ws, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "--allow-empty", "-m", "root");
		File.WriteAllText(Path.Combine(ws, "a.txt"), "one\ntwo\nthree\n");
		Git(ws, "add", "a.txt");
		Git(ws, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "a");
		// Uncommitted and untracked state must be part of the fork's snapshot.
		File.WriteAllText(Path.Combine(ws, "a.txt"), "one\ntwo\nthree\nfour (uncommitted)\n");
		File.WriteAllText(Path.Combine(ws, "notes.txt"), "untracked notes\n");
	}

	static async Task<(AinurRuntime Rt, Project P, Agent Expert)> Fork(TempHome home, Func<string, Task>? duringFork = null, string edit = "two") {
		InitRepo(home.Workspace);
		var gate = new TaskCompletionSource();
		var provider = new FakeProvider((req, n) => {
			var system = req.Messages[0].Content!;
			if(!system.Contains("You are Este")) return FakeProvider.Text("");
			var last = req.Messages[^1];
			if(last.Role == "user" && last.Content!.Contains("[Consultation request]"))
				return FakeProvider.Call("powershell", "{\"script\":\"(Get-Location).Path; Get-Content notes.txt\"}");
			if(last.Role == "tool" && req.Messages.Count(m => m.Role == "tool") == 1)
				return FakeProvider.Call("multi_edit", $$"""{"path":"a.txt","edits":[{"old_text":"{{edit}}","new_text":"TWO (fork)"}]}""");
			if(last.Role == "tool") {
				gate.Task.Wait(TimeSpan.FromSeconds(10));
				return FakeProvider.Text("Changed two to TWO.");
			}
			return FakeProvider.Text("");
		});
		var rt = home.Runtime(provider);
		var p = rt.CreateProject("WT", "d", home.Workspace);
		var expert = rt.CreateAgent(p.Id, new NewAgent { Name = "Este", Title = "Editor", ManagerId = p.RootAgentId }, p.RootAgentId);
		var fork = rt.StartConsultation(p.RootAgentId!, expert.Id, "Change two to TWO.", null);
		await Wait.Until(() => rt.Store.Items(fork.Id).Count(i => i.Kind == "tool_result") >= 2, TimeSpan.FromSeconds(15), "fork edit");
		var forkSession = rt.Store.GetSession(fork.Id)!;
		Assert.NotNull(forkSession.WorkspacePath);
		Assert.Contains("TWO (fork)", File.ReadAllText(Path.Combine(forkSession.WorkspacePath!, "a.txt")));
		// The original workspace is untouched while the fork works.
		Assert.DoesNotContain("TWO (fork)", File.ReadAllText(Path.Combine(home.Workspace, "a.txt")));
		if(duringFork is not null) await duringFork(home.Workspace);
		gate.SetResult();
		await Wait.Until(() => rt.Store.GetSession(fork.Id)!.State == "finished", TimeSpan.FromSeconds(15), "fork finish");
		Assert.False(Directory.Exists(forkSession.WorkspacePath));
		return (rt, p, expert);
	}

	[Fact]
	public async Task ForkWorksInIsolatedWorktreeAndIntegrates() {
		using var home = new TempHome();
		var (rt, p, _) = await Fork(home);
		using var _ = rt;
		Assert.Equal("one\nTWO (fork)\nthree\nfour (uncommitted)\n", File.ReadAllText(Path.Combine(home.Workspace, "a.txt")));
		var pwd = rt.Store.Items(rt.Store.SessionsForAgent(rt.Store.ListAgents(p.Id).Single(a => a.Name == "Este").Id).Single(s => s.Kind == "consultation").Id)
			.First(i => i.Kind == "tool_result").Payload;
		Assert.Contains("untracked notes", pwd);
		Assert.Contains(rt.Store.ListNotifications(p.Id), n => n.Type == NotificationTypes.Result && n.Body.Contains("Integrated the fork's changes"));
	}

	[Fact]
	public async Task ConflictingForkIsRejectedWithoutTouchingWorkspace() {
		using var home = new TempHome();
		var (rt, p, _) = await Fork(home, async ws => {
			File.WriteAllText(Path.Combine(ws, "a.txt"), "one\ntwo changed by original\nthree\nfour (uncommitted)\n");
			await Task.CompletedTask;
		});
		using var _ = rt;
		Assert.Equal("one\ntwo changed by original\nthree\nfour (uncommitted)\n", File.ReadAllText(Path.Combine(home.Workspace, "a.txt")));
		Assert.Contains(rt.Store.ListNotifications(p.Id), n => n.Type == NotificationTypes.Result && n.Body.Contains("NOT applied"));
	}
}
