using System.Text.Json.Nodes;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Dapper;

namespace Ainur.Tests;

public class WorkbookTests {
	[Fact]
	public async Task PreCanceledRunRecordsNotStartedAndCannotWriteSideEffects() {
		using var home = new TempHome();
		using var rt = home.Runtime(start: false);
		var p = rt.CreateProject("cancel", "", home.Workspace);
		var w = new Workbooks(rt.Db);
		var b = w.Create(p.Id, "cancel");
		var c = w.Add(p.Id, b.Id, "powershell", "[IO.File]::WriteAllText((Join-Path (Get-Location) 'canceled-effect.txt'), 'EFFECT')")!;
		using var ct = new CancellationTokenSource(); ct.Cancel();
		var r = await w.ExecuteAsync(p.Id, b.Id, c.Id, 1, "test", home.Workspace, ct.Token);
		Assert.Equal("canceled", r!.Status);
		Assert.Contains("not started", r.Error);
		Assert.False(File.Exists(Path.Combine(home.Workspace, "canceled-effect.txt")));
		Assert.NotNull(r.EndedAt);
		Assert.Equal("canceled", Assert.Single(new Workbooks(rt.Db).Runs(p.Id, b.Id, c.Id)!).Status);
	}

	[Fact]
	public async Task ActiveRunIsVisibleAcrossReadersAndCannotBeRepeatedWhileOrphanIntentIsUnknown() {
		using var home = new TempHome();
		using var rt = home.Runtime(start: false);
		var p = rt.CreateProject("active", "", home.Workspace);
		var w = new Workbooks(rt.Db);
		var b = w.Create(p.Id, "active");
		var c = w.Add(p.Id, b.Id, "powershell", "[IO.File]::WriteAllText((Join-Path (Get-Location) 'started.txt'), 'active'); Start-Sleep -Seconds 10; 'done'")!;
		using var ct = new CancellationTokenSource();
		var task = w.ExecuteAsync(p.Id, b.Id, c.Id, 1, "test", home.Workspace, ct.Token);
		try {
			for(var i = 0; i < 250 && !File.Exists(Path.Combine(home.Workspace, "started.txt")); i++) await Task.Delay(20);
			Assert.True(File.Exists(Path.Combine(home.Workspace, "started.txt")));
			Assert.False(task.IsCompleted);
			var reader = new Workbooks(rt.Db);
			Assert.Equal("running", Assert.Single(reader.Open(p.Id, b.Id)!.Cells[0].Runs).Status);
			Assert.Equal("running", Assert.Single(reader.Runs(p.Id, b.Id, c.Id)!).Status);
			await Assert.ThrowsAsync<Workbooks.ConflictException>(() => reader.ExecuteAsync(p.Id, b.Id, c.Id, 1, "duplicate", home.Workspace, default));
		} finally { ct.Cancel(); await task; }
		Assert.Equal("interrupted", (await task)!.Status);
		Assert.Equal("interrupted", Assert.Single(w.Runs(p.Id, b.Id, c.Id)!).Status);
		// Simulated persisted intent with no owning process: reads must not invent live execution or retry.
		rt.Db.Write(u => { u.Execute("UPDATE workbook_runs SET status='unknown',ended_at=NULL WHERE cell_id=@id", new { id = c.Id }); return 0; });
		var orphan = Assert.Single(new Workbooks(rt.Db).Open(p.Id, b.Id)!.Cells[0].Runs);
		Assert.Equal("unknown", orphan.Status); Assert.Null(orphan.EndedAt);
		Assert.Single(w.Runs(p.Id, b.Id, c.Id)!);
	}

	[Fact]
	public async Task NativeAgentToolSharesHumanSavedRevisionAndHarmlessWorkspaceWorkflow() {
		using var home = new TempHome();
		using var rt = home.Runtime(start: false);
		var project = rt.CreateProject("native-workbook", "", home.Workspace);
		var agent = rt.CreateAgent(project.Id, new NewAgent { Name = "workbook-agent", Role = "specialist" }, project.RootAgentId);
		var session = rt.Store.GetSession(agent.PrimarySessionId!)!;
		var ctx = new ToolContext { Runtime = rt, Project = project, Agent = agent, Session = session,
			Host = null!, InvocationId = "offline-workbook", CancellationToken = default };
		var tool = new WorkbookTool();
		var created = await tool.InvokeAsync(ctx, new JsonObject { ["action"] = "create", ["title"] = "scratch workflow" });
		var book = Assert.IsType<Workbooks.Book>(created.Value);
		var added = await tool.InvokeAsync(ctx, new JsonObject { ["action"] = "add", ["workbook_id"] = book.Id,
			["source"] = "[IO.File]::WriteAllText((Join-Path (Get-Location) 'workbook-proof.txt'), 'one'); Get-Content ./workbook-proof.txt" });
		var cell = Assert.IsType<Workbooks.Cell>(added.Value);
		var first = await tool.InvokeAsync(ctx, new JsonObject { ["action"] = "run", ["workbook_id"] = book.Id, ["cell_id"] = cell.Id, ["revision"] = 1 });
		Assert.Equal("one", Assert.IsType<Workbooks.Run>(first.Value).Output.Trim());
		Assert.Equal("one", File.ReadAllText(Path.Combine(home.Workspace, "workbook-proof.txt")));
		// Human/API service edits the same record, not a parallel agent-only store.
		var human = new Workbooks(rt.Db);
		human.Edit(project.Id, book.Id, cell.Id, 1, "Get-Content ./workbook-proof.txt; Write-Output 'two'");
		var staleEdit = await tool.InvokeAsync(ctx, new JsonObject { ["action"] = "edit", ["workbook_id"] = book.Id, ["cell_id"] = cell.Id,
			["expected_revision"] = 1, ["source"] = "lost update" });
		Assert.True(staleEdit.IsError);
		var rerun = await tool.InvokeAsync(ctx, new JsonObject { ["action"] = "run", ["workbook_id"] = book.Id, ["cell_id"] = cell.Id, ["revision"] = 2 });
		var result = Assert.IsType<Workbooks.Run>(rerun.Value);
		Assert.Equal("complete", result.Status); Assert.Equal(2, result.Revision); Assert.Equal(2, result.Ordinal);
		Assert.Equal(agent.Id, result.Actor); Assert.Contains("two", result.Output);
		Assert.Equal(2, human.Runs(project.Id, book.Id, cell.Id)!.Count);
		Assert.False(human.Open(project.Id, book.Id)!.Cells[0].Stale);
	}

	[Fact]
	public async Task HumanAndAgentShareDurableRevisionedExplicitLoopWithoutImplicitRerun() {
		using var home = new TempHome();
		string projectId, otherId, bookId, cellId, firstRunId;
		using(var rt = home.Runtime(start: false)) {
			projectId = rt.CreateProject("workbook-test", "", home.Workspace).Id;
			otherId = rt.CreateProject("other-workbook-test", "", home.Workspace).Id;
			var w = new Workbooks(rt.Db);
			var book = w.Create(projectId, "demo"); bookId = book.Id;
			Assert.Empty(w.List(otherId));
			Assert.Null(w.Open(otherId, bookId));
			Assert.Throws<ArgumentException>(() => w.Add(projectId, bookId, "csharp", "1+1"));
			var cell = w.Add(projectId, bookId, "powershell", "Write-Output 'one'")!; cellId = cell.Id;
			Assert.Null(w.Edit(otherId, bookId, cellId, 1, "no"));
			Assert.Null(w.Runs(otherId, bookId, cellId));
			Assert.Empty(w.Open(projectId, bookId)!.Cells[0].Runs);
			var run = await w.ExecuteAsync(projectId, bookId, cellId, 1, "human:test", home.Workspace, CancellationToken.None);
			Assert.NotNull(run); firstRunId = run.Id;
			Assert.Equal("complete", run.Status); Assert.Equal("one", run.Output.Trim());
			Assert.Equal(1, run.Ordinal); Assert.Equal(1, run.Revision);
			Assert.Throws<Workbooks.ConflictException>(() => w.Edit(projectId, bookId, cellId, 0, "stale writer"));
			var edited = w.Edit(projectId, bookId, cellId, 1, "Write-Output 'two'; Write-Error 'expected error'");
			Assert.Equal(2, edited!.Revision);
			var pending = w.Open(projectId, bookId)!.Cells[0];
			Assert.True(pending.Stale); Assert.Equal(firstRunId, pending.Runs[0].Id);
			Assert.Throws<Workbooks.ConflictException>(() => w.Edit(projectId, bookId, cellId, 1, "lost update"));
			Assert.Throws<Workbooks.ConflictException>(() => w.ExecuteAsync(projectId, bookId, cellId, 999, "agent:test", home.Workspace, CancellationToken.None).GetAwaiter().GetResult());
			var rerun = await w.ExecuteAsync(projectId, bookId, cellId, 2, "agent:test", home.Workspace, CancellationToken.None);
			Assert.Equal("error", rerun!.Status); Assert.Contains("two", rerun.Output);
			Assert.Contains("expected error", rerun.Error); Assert.Equal(2, rerun.Ordinal);
			Assert.Equal("agent:test", rerun.Actor); Assert.Contains("fresh-runspace", rerun.Kernel);
			Assert.Null(await w.ExecuteAsync(otherId, bookId, cellId, 2, "other", home.Workspace, CancellationToken.None));
		}
		using(var rt = home.Runtime(start: false)) {
			var w = new Workbooks(rt.Db);
			var view = w.Open(projectId, bookId)!;
			Assert.Equal(2, view.Cells[0].Cell.Revision);
			Assert.False(view.Cells[0].Stale);
			Assert.Equal(new[] { 2, 1 }, view.Cells[0].Runs.Select(r => r.Ordinal));
			Assert.Equal("one", view.Cells[0].Runs[1].Output.Trim());
			Assert.Equal(firstRunId, view.Cells[0].Runs[1].Id);
			// Historical revisions remain explicitly runnable; nothing executes on reload.
			var historical = await w.ExecuteAsync(projectId, bookId, cellId, 1, "human:test", home.Workspace, CancellationToken.None);
			Assert.Equal(3, historical!.Ordinal); Assert.Equal("one", historical.Output.Trim());
			Assert.True(w.Open(projectId, bookId)!.Cells[0].Stale);
		}
	}

	[Fact]
	public async Task FreshRunspaceDoesNotCarryStateAndCanceledExecutionDoesNotRetry() {
		using var home = new TempHome();
		using var rt = home.Runtime(start: false);
		var project = rt.CreateProject("fresh-runspace", "", home.Workspace);
		var w = new Workbooks(rt.Db);
		var book = w.Create(project.Id, "fresh");
		var set = w.Add(project.Id, book.Id, "powershell", "$global:workbookState = 42; Write-Output $global:workbookState")!;
		var read = w.Add(project.Id, book.Id, "powershell", "if ($null -eq $global:workbookState) { 'reset' } else { 'leaked' }")!;
		Assert.Equal("42", (await w.ExecuteAsync(project.Id, book.Id, set.Id, 1, "test", home.Workspace, CancellationToken.None))!.Output.Trim());
		Assert.Equal("reset", (await w.ExecuteAsync(project.Id, book.Id, read.Id, 1, "test", home.Workspace, CancellationToken.None))!.Output.Trim());
		using var canceled = new CancellationTokenSource(); canceled.Cancel();
		var skipped = await w.ExecuteAsync(project.Id, book.Id, set.Id, 1, "test", home.Workspace, canceled.Token);
		Assert.Equal("canceled", skipped!.Status);
		Assert.Equal(2, skipped.Ordinal);
		Assert.Equal(2, w.Runs(project.Id, book.Id, set.Id)!.Count);
		var host = w.Add(project.Id, book.Id, "powershell", "Write-Host 'host output'")!;
		var hostResult = await w.ExecuteAsync(project.Id, book.Id, host.Id, 1, "test", home.Workspace, CancellationToken.None);
		Assert.Contains("host output", hostResult!.Output);
		var native = w.Add(project.Id, book.Id, "powershell", "& /usr/bin/false")!;
		var nativeResult = await w.ExecuteAsync(project.Id, book.Id, native.Id, 1, "test", home.Workspace, CancellationToken.None);
		Assert.Equal("error", nativeResult!.Status);
		Assert.Contains("code 1", nativeResult.Error);
	}
}
