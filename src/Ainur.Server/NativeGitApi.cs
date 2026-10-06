using System.Text.Json.Nodes;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;

namespace Ainur.Server;

/// <summary>Existing local-only control-plane authorization applies. No caller-supplied agent impersonation.</summary>
public static class NativeGitApi {
	public static void Map(RouteGroupBuilder api) {
		api.MapGet("/projects/{id}/git", (AinurRuntime rt, string id, CancellationToken ct) => {
			try { return Results.Ok(new NativeGit(rt, ct).Status(id, "human")); }
			catch(DomainException ex) { return Results.Conflict(new { error = ex.Message }); }
		});
		api.MapPost("/projects/{id}/git", (AinurRuntime rt, string id, JsonObject request, CancellationToken ct) => {
			if(rt.Store.GetProject(id) is null) return Results.NotFound();
			try { return Results.Ok(GitWorkspaceTool.Execute(new NativeGit(rt, ct), id, "human", request)); }
			catch(DomainException ex) { return Results.Conflict(new { error = ex.Message }); }
			catch(ToolException ex) { return Results.BadRequest(new { error = ex.Message }); }
		});
	}
}
