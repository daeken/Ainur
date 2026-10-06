using Ainur.Core.Runtime;

namespace Ainur.Tests;

public class WorkbookInitializationTests {
	[Fact]
	public async Task ConcurrentWorkbookAndSessionInitializationPreservesEachWorkspace() {
		using var start = new ManualResetEventSlim();
		var tasks = Enumerable.Range(0, 16).Select(i => Task.Run(async () => {
			using var home = new TempHome();
			using var rt = home.Runtime(start: false);
			var p = rt.CreateProject("cold-init-" + i, "", home.Workspace);
			start.Wait();
			if(i % 4 == 0) {
				var agent = rt.Store.GetAgent(p.RootAgentId!)!;
				using var host = new SessionHost(rt, rt.Store.GetSession(agent.PrimarySessionId!)!);
				var output = await host.Dispatcher.InvokeAsync(() => Task.FromResult(host.PowerShell.Run("(Get-Location).Path", TimeSpan.FromSeconds(30), default)));
				Assert.False(output.HadErrors, output.Text);
				Assert.Contains(home.Workspace, output.Text);
			} else {
				var w = new Workbooks(rt.Db);
				var b = w.Create(p.Id, "workspace");
				var c = w.Add(p.Id, b.Id, "powershell", "[IO.File]::WriteAllText((Join-Path (Get-Location) 'proof.txt'), 'local'); (Get-Location).Path; Get-Content ./proof.txt")!;
				var r = await w.ExecuteAsync(p.Id, b.Id, c.Id, 1, "test", home.Workspace, default);
				Assert.True(r!.Status == "complete", $"status={r.Status}, output={r.Output}, error={r.Error}, expected workspace={home.Workspace}");
				Assert.Contains(home.Workspace, r.Output); Assert.Contains("local", r.Output);
				Assert.Equal("local", File.ReadAllText(Path.Combine(home.Workspace, "proof.txt")));
			}
		})).ToArray();
		start.Set();
		await Task.WhenAll(tasks);
	}

	[Fact]
	public async Task InitializationGateDoesNotSerializeCellExecution() {
		using var home = new TempHome();
		using var rt = home.Runtime(start: false);
		var p = rt.CreateProject("parallel", "", home.Workspace);
		var w = new Workbooks(rt.Db); var b = w.Create(p.Id, "parallel");
		string Source(string own, string other) => $"[IO.File]::WriteAllText((Join-Path (Get-Location) '{own}'), 'ready'); $end=[DateTime]::UtcNow.AddSeconds(10); while(-not (Test-Path './{other}')) {{ if([DateTime]::UtcNow -gt $end) {{ throw 'execution serialized' }}; Start-Sleep -Milliseconds 20 }}; 'overlap'";
		var a = w.Add(p.Id, b.Id, "powershell", Source("a.txt", "b.txt"))!;
		var c = w.Add(p.Id, b.Id, "powershell", Source("b.txt", "a.txt"))!;
		var results = await Task.WhenAll(w.ExecuteAsync(p.Id, b.Id, a.Id, 1, "test", home.Workspace, default),
			w.ExecuteAsync(p.Id, b.Id, c.Id, 1, "test", home.Workspace, default));
		foreach(var r in results) { Assert.True(r!.Status == "complete", r.Error); Assert.Contains("overlap", r.Output); }
	}
}
