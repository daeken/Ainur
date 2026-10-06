using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using Ainur.Core.Persistence;
using Dapper;

namespace Ainur.Core.Runtime;

/// <summary>Durable, explicit, one-shot PowerShell cells. Every run starts a fresh runspace; no notebook kernel exists.</summary>
public sealed class Workbooks(Db db) {
	public Func<string, IDisposable>? Admission { get; init; }
	public sealed record Book {
		public string Id { get; init; } = ""; public string ProjectId { get; init; } = ""; public string Title { get; init; } = ""; public long CreatedAt { get; init; }
		public Book() { }
		public Book(string id, string projectId, string title, long createdAt) => (Id, ProjectId, Title, CreatedAt) = (id, projectId, title, createdAt);
	}
	public sealed record Cell {
		public string Id { get; init; } = ""; public string WorkbookId { get; init; } = ""; public int Position { get; init; }
		public string Language { get; init; } = ""; public int Revision { get; init; } public string Source { get; init; } = ""; public long UpdatedAt { get; init; }
		public Cell() { }
		public Cell(string id, string workbookId, int position, string language, int revision, string source, long updatedAt) =>
			(Id, WorkbookId, Position, Language, Revision, Source, UpdatedAt) = (id, workbookId, position, language, revision, source, updatedAt);
	}
	public sealed record Run {
		public string Id { get; init; } = ""; public string CellId { get; init; } = ""; public int Revision { get; init; } public int Ordinal { get; init; }
		public string Status { get; init; } = ""; public string Output { get; init; } = ""; public string Error { get; init; } = "";
		public long StartedAt { get; init; } public long? EndedAt { get; init; } public string Actor { get; init; } = ""; public string Kernel { get; init; } = "";
		public Run() { }
		public Run(string id, string cellId, int revision, int ordinal, string status, string output, string error, long startedAt, long? endedAt, string actor, string kernel) =>
			(Id, CellId, Revision, Ordinal, Status, Output, Error, StartedAt, EndedAt, Actor, Kernel) =
			(id, cellId, revision, ordinal, status, output, error, startedAt, endedAt, actor, kernel);
	}
	public sealed record CellView(Cell Cell, IReadOnlyList<Run> Runs, bool Stale);
	public sealed record View(Book Workbook, IReadOnlyList<CellView> Cells, string ExecutionModel);
	public sealed class ConflictException : Exception { public ConflictException(string message) : base(message) { } }
	const string Kernel = "powershell:fresh-runspace:no-shared-state";
	const int MaxSource = 64_000;
	const int MaxOutput = 64_000;
	static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(2);
	// Durable intent is unknown after process loss; only this process can attest live execution.
	static readonly ConcurrentDictionary<string, string> Active = new();
	static Run Visible(Run run) => run.Status == "unknown" && Active.TryGetValue(run.Id, out var status) ? run with { Status = status } : run;

	static string Id(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N");
	static void ValidateSource(string source) {
		if(source.Length > MaxSource) throw new ArgumentException($"Cell source exceeds {MaxSource} characters.");
	}
	public IReadOnlyList<Book> List(string projectId) => db.Read(c => c.Query<Book>(
		"SELECT * FROM workbooks WHERE project_id=@projectId ORDER BY created_at,id", new { projectId }).AsList());
	public View? Open(string projectId, string bookId) => db.Read(c => {
		var book = c.QuerySingleOrDefault<Book>("SELECT * FROM workbooks WHERE id=@bookId AND project_id=@projectId", new { bookId, projectId });
		if(book is null) return null;
		var cells = c.Query<Cell>("SELECT * FROM workbook_cells WHERE workbook_id=@bookId ORDER BY position,id", new { bookId }).AsList();
		var views = cells.Select(cell => {
			var runs = c.Query<Run>("SELECT * FROM workbook_runs WHERE cell_id=@id ORDER BY ordinal DESC", new { id = cell.Id }).AsList();
			return new CellView(cell, runs.Select(Visible).ToList(), runs.FirstOrDefault()?.Revision != cell.Revision && runs.Count > 0);
		}).ToList();
		return new View(book, views, Kernel);
	});
	public Book Create(string projectId, string title) {
		if(string.IsNullOrWhiteSpace(title) || title.Length > 200) throw new ArgumentException("Title must be 1–200 characters.");
		var b = new Book(Id("wb"), projectId, title, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		db.Write(u => { u.Execute("INSERT INTO workbooks(id,project_id,title,created_at) VALUES(@Id,@ProjectId,@Title,@CreatedAt)", b); return 0; });
		return b;
	}
	public Cell? Add(string projectId, string bookId, string language, string source) {
		if(language != "powershell") throw new ArgumentException("Only 'powershell' cells are supported; C# is not yet available.");
		ValidateSource(source);
		return db.Write(u => {
			if(u.Scalar<int>("SELECT COUNT(*) FROM workbooks WHERE id=@bookId AND project_id=@projectId", new { bookId, projectId }) == 0) return null;
			var cell = new Cell(Id("cell"), bookId, u.Scalar<int>("SELECT COALESCE(MAX(position),0)+1 FROM workbook_cells WHERE workbook_id=@bookId", new { bookId }), language, 1, source, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			u.Execute("INSERT INTO workbook_cells(id,workbook_id,position,language,revision,source,updated_at) VALUES(@Id,@WorkbookId,@Position,@Language,@Revision,@Source,@UpdatedAt)", cell);
			u.Execute("INSERT INTO workbook_revisions(cell_id,revision,source,created_at) VALUES(@Id,1,@Source,@UpdatedAt)", cell);
			return cell;
		});
	}
	public Cell? Edit(string projectId, string bookId, string cellId, int expectedRevision, string source) {
		ValidateSource(source);
		return db.Write(u => {
			var cell = u.Single<Cell>("""
				SELECT c.* FROM workbook_cells c JOIN workbooks w ON w.id=c.workbook_id
				WHERE c.id=@cellId AND w.id=@bookId AND w.project_id=@projectId
				""", new { cellId, bookId, projectId });
			if(cell is null) return null;
			if(cell.Revision != expectedRevision) throw new ConflictException($"Expected revision {expectedRevision}; current revision is {cell.Revision}.");
			var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			u.Execute("UPDATE workbook_cells SET source=@source, revision=revision+1, updated_at=@now WHERE id=@cellId", new { source, now, cellId });
			u.Execute("INSERT INTO workbook_revisions(cell_id,revision,source,created_at) VALUES(@cellId,@revision,@source,@now)", new { cellId, revision = expectedRevision + 1, source, now });
			return cell with { Revision = expectedRevision + 1, Source = source, UpdatedAt = now };
		});
	}
	public IReadOnlyList<Run>? Runs(string projectId, string bookId, string cellId) => db.Read(c => {
		if(c.ExecuteScalar<int>("SELECT COUNT(*) FROM workbook_cells c JOIN workbooks w ON w.id=c.workbook_id WHERE c.id=@cellId AND w.id=@bookId AND w.project_id=@projectId", new { cellId, bookId, projectId }) == 0) return null;
		return (IReadOnlyList<Run>)c.Query<Run>("SELECT * FROM workbook_runs WHERE cell_id=@cellId ORDER BY ordinal DESC", new { cellId }).Select(Visible).ToList();
	});
	public async Task<Run?> ExecuteAsync(string projectId, string bookId, string cellId, int revision, string actor, string? workspace, CancellationToken ct) {
		using var admission = Admission?.Invoke(cellId);
		// Persist intent before execution; a process crash leaves 'unknown' rather than claiming failure or retrying effects.
		(Run? Run, string? Source) claim = db.Write<(Run? Run, string? Source)>(u => {
			var cell = u.Single<Cell>("""
				SELECT c.* FROM workbook_cells c JOIN workbooks w ON w.id=c.workbook_id
				WHERE c.id=@cellId AND w.id=@bookId AND w.project_id=@projectId
				""", new { cellId, bookId, projectId });
			if(cell is null) return (Run: (Run?)null, Source: (string?)null);
			if(u.Query<Run>("SELECT * FROM workbook_runs WHERE cell_id=@cellId AND status='unknown'", new { cellId }).Any(r => Active.ContainsKey(r.Id)))
				throw new ConflictException("This cell already has an active run; wait for its outcome before running again.");
			var source = u.Single<string>("SELECT source FROM workbook_revisions WHERE cell_id=@cellId AND revision=@revision", new { cellId, revision });
			if(source is null) throw new ConflictException($"Revision {revision} does not exist. Choose a saved revision explicitly.");
			var run = new Run(Id("run"), cellId, revision, u.Scalar<int>("SELECT COALESCE(MAX(ordinal),0)+1 FROM workbook_runs WHERE cell_id=@cellId", new { cellId }), "unknown", "", "",
				DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null, actor, Kernel);
			Active[run.Id] = "queued";
			try { u.Execute("""
				INSERT INTO workbook_runs(id,cell_id,revision,ordinal,status,output,error,started_at,ended_at,actor,kernel)
				VALUES(@Id,@CellId,@Revision,@Ordinal,@Status,@Output,@Error,@StartedAt,@EndedAt,@Actor,@Kernel)
				""", run); }
			catch { Active.TryRemove(run.Id, out _); throw; }
			return (Run: run, Source: source);
		});
		if(claim.Run is null) return null;
		var started = claim.Run;
		// No ambient Ainur bridge or session variables. Existing project workspace is a starting directory, not a sandbox.
		using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
		deadline.CancelAfter(RunLimit);
		try {
			var outcome = await Task.Run(() => RunOnce(claim.Source!, workspace, deadline.Token, () => Active[started.Id] = "running"));
			var ended = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			db.Write(u => { u.Execute("UPDATE workbook_runs SET status=@status,output=@output,error=@error,ended_at=@ended WHERE id=@id AND status='unknown'",
				new { status = outcome.Status, output = outcome.Output, error = outcome.Error, ended, id = started.Id }); return 0; });
			return started with { Status = outcome.Status, Output = outcome.Output, Error = outcome.Error, EndedAt = ended };
		} finally { Active.TryRemove(started.Id, out _); }
	}
	static (string Status, string Output, string Error) RunOnce(string source, string? workspace, CancellationToken ct, Action admitted) {
		static (string, string, string) NotStarted() => ("canceled", "", "Canceled before execution admission; cell code was not started.");
		if(ct.IsCancellationRequested) return NotStarted();
		try {
			using var runspace = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
			PowerShellInitialization.Open(runspace);
			using var ps = PowerShell.Create();
			ps.Runspace = runspace;
			if(string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
				return ("error", "", "Project workspace is not configured or does not exist; execution was not started.");
			runspace.SessionStateProxy.Path.SetLocation(workspace);
			ps.AddScript("$global:LASTEXITCODE = 0;\n" + source);
			if(ct.IsCancellationRequested) return NotStarted();
			// Begin before registering Stop: stopping a NotStarted pipeline is reset by Invoke.
			// Cancellation after this admission may race cell side effects; never claim rollback.
			admitted();
			var invocation = ps.BeginInvoke();
			using var registration = ct.Register(() => { try { ps.Stop(); } catch { /* interruption cannot prove rollback */ } });
			var output = ps.EndInvoke(invocation);
			var stdout = string.Join(Environment.NewLine, output.Select(x => x?.ToString())
				.Concat(ps.Streams.Information.Select(x => x.MessageData?.ToString()))
				.Concat(ps.Streams.Warning.Select(x => "WARNING: " + x)));
			var exitCode = runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
			var stderr = string.Join(Environment.NewLine, ps.Streams.Error.Select(x => x.ToString()));
			if(exitCode is int code && code != 0) stderr += $"\nNative command exited with code {code}.";
			var failed = ps.HadErrors || (exitCode is int nativeExit && nativeExit != 0);
			return (ct.IsCancellationRequested ? "interrupted" : failed ? "error" : "complete", Truncate(stdout), Truncate(stderr));
		} catch(OperationCanceledException) { return ("interrupted", "", "Execution was interrupted; effects may have occurred."); }
		catch(Exception ex) { return (ct.IsCancellationRequested ? "interrupted" : "error", "", Truncate(ex.ToString())); }
	}
	static string Truncate(string value) => value.Length <= MaxOutput ? value : value[..MaxOutput] + "\n[output truncated]";
}
