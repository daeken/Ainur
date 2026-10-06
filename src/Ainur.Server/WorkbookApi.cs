using Ainur.Core.Runtime;

namespace Ainur.Server;

public sealed record CreateWorkbookRequest(string Title);
public sealed record AddWorkbookCellRequest(string Language, string Source);
public sealed record EditWorkbookCellRequest(string Source, int ExpectedRevision);
public sealed record RunWorkbookCellRequest(int Revision);

/// <summary>Local-only HTTP facade on the same service exposed to agents as the workbook native tool.</summary>
public static class WorkbookApi {
	public static void Map(RouteGroupBuilder api) {
		var books = api.MapGroup("/projects/{id}/workbooks");
		books.MapGet("", (AinurRuntime rt, string id) => rt.Store.GetProject(id) is null ? Results.NotFound() : Results.Ok(new Workbooks(rt.Db).List(id)));
		books.MapPost("", (AinurRuntime rt, string id, CreateWorkbookRequest req) => {
			if(rt.Store.GetProject(id) is null) return Results.NotFound();
			try { return Results.Ok(new Workbooks(rt.Db).Create(id, req.Title)); }
			catch(ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
		});
		books.MapGet("/{bookId}", (AinurRuntime rt, string id, string bookId) => new Workbooks(rt.Db).Open(id, bookId) is { } view ? Results.Ok(view) : Results.NotFound());
		books.MapPost("/{bookId}/cells", (AinurRuntime rt, string id, string bookId, AddWorkbookCellRequest req) => {
			try { return new Workbooks(rt.Db).Add(id, bookId, req.Language, req.Source) is { } cell ? Results.Ok(cell) : Results.NotFound(); }
			catch(ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
		});
		books.MapPut("/{bookId}/cells/{cellId}", (AinurRuntime rt, string id, string bookId, string cellId, EditWorkbookCellRequest req) => {
			try { return new Workbooks(rt.Db).Edit(id, bookId, cellId, req.ExpectedRevision, req.Source) is { } cell ? Results.Ok(cell) : Results.NotFound(); }
			catch(Workbooks.ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
			catch(ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
		});
		books.MapGet("/{bookId}/cells/{cellId}/runs", (AinurRuntime rt, string id, string bookId, string cellId) => new Workbooks(rt.Db).Runs(id, bookId, cellId) is { } runs ? Results.Ok(runs) : Results.NotFound());
		books.MapPost("/{bookId}/cells/{cellId}/runs", async (AinurRuntime rt, string id, string bookId, string cellId, RunWorkbookCellRequest req, CancellationToken ct) => {
			try {
				var project = rt.Store.GetProject(id);
				if(project is null) return Results.NotFound();
				// Explicit script execution is a local-control-plane operation, identical in authority to existing project tools.
				return await new Workbooks(rt.Db) { Admission = cell => rt.Maintenance.Admit("workbook", referenceId: cell) }.ExecuteAsync(id, bookId, cellId, req.Revision, "human:local-api", project.WorkspacePath, ct) is { } run ? Results.Ok(run) : Results.NotFound();
			} catch(Workbooks.ConflictException ex) { return Results.Conflict(new { error = ex.Message }); }
			catch(MaintenanceAdmissionException ex) { return Results.Conflict(new { error = ex.Message }); }
		});
	}
}
