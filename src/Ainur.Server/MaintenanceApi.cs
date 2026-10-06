using Ainur.Core.Persistence;
using Ainur.Core.Runtime;

namespace Ainur.Server;

public sealed record BeginMaintenanceRequest(string Reason, int DeadlineSeconds = 120);
public static class MaintenanceApi {
	public static void MapMaintenance(this WebApplication app) {
		var group = app.MapGroup("/api/v1/control/maintenance");
		// Same pinned loopback bearer key as the process receipt. No agent session or paid model is needed.
		group.AddEndpointFilter(async (ctx, next) => {
			var options = ctx.HttpContext.RequestServices.GetRequiredService<ServerOptions>();
			return RouteReceipt.Authenticate(ctx.HttpContext, options) is { } denied ? denied : await next(ctx);
		});
		group.MapGet("", (AinurRuntime rt) => Results.Ok(rt.Maintenance.Status()));
		group.MapPost("", (AinurRuntime rt, BeginMaintenanceRequest req) => {
			try { return Results.Ok(rt.Maintenance.Begin(req.Reason, TimeSpan.FromSeconds(req.DeadlineSeconds))); }
			catch(DomainException e) { return Results.Conflict(new { error = e.Message }); }
		});
		group.MapPost("/{id}/handoff", (AinurRuntime rt, string id) => {
			try { return Results.Ok(rt.Maintenance.PrepareHandoff(id)); }
			catch(DomainException e) { return Results.Conflict(new { error = e.Message }); }
		});
		group.MapPost("/{id}/abort", (AinurRuntime rt, string id) => Release(rt, id, true));
		group.MapPost("/{id}/release", (AinurRuntime rt, string id) => Release(rt, id, false));
	}
	static IResult Release(AinurRuntime rt, string id, bool abort) {
		try { rt.Maintenance.Release(id, abort); return Results.Ok(rt.Maintenance.Status()); }
		catch(DomainException e) { return Results.Conflict(new { error = e.Message }); }
	}
}
