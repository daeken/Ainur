using Ainur.Core.Persistence;
using Ainur.Core.Runtime;

namespace Ainur.Server;

public static class ConversationImageApi {
	public static void Map(RouteGroupBuilder api) {
		api.MapPost("/projects/{id}/conversation/images", async (AinurRuntime rt, string id, HttpRequest request, CancellationToken ct) => {
			if(rt.Store.GetProject(id) is null) return Results.NotFound(new { error = "Project not found." });
			if(request.ContentType != "image/png") return Results.BadRequest(new { error = "Only PNG images (Content-Type: image/png) are supported." });
			if(request.ContentLength > ConversationImageValidation.MaxBytes) return Results.BadRequest(new { error = "Image exceeds 2 MiB." });
			using var buffer = new MemoryStream();
			var chunk = new byte[16 * 1024];
			while(true) {
				var count = await request.Body.ReadAsync(chunk, ct);
				if(count == 0) break;
				if(buffer.Length + count > ConversationImageValidation.MaxBytes) return Results.BadRequest(new { error = "Image exceeds 2 MiB." });
				buffer.Write(chunk, 0, count);
			}
			try { return Results.Json(rt.UploadConversationImage(id, buffer.ToArray(), request.ContentType)); }
			catch(DomainException e) { return Results.BadRequest(new { error = e.Message }); }
		});
		api.MapGet("/projects/{id}/conversation/images/{imageId}/content", (AinurRuntime rt, string id, string imageId, HttpResponse response) => {
			if(rt.Store.GetProject(id) is null) return Results.NotFound(new { error = "Project not found." });
			try {
				var bytes = rt.ReadConversationImage(id, imageId);
				if(bytes is null) return Results.NotFound(new { error = "Image not found in this project/session or expired." });
				response.Headers.XContentTypeOptions = "nosniff";
				response.Headers.CacheControl = "no-store";
				return Results.Bytes(bytes, "image/png");
			} catch(DomainException e) { return Results.BadRequest(new { error = e.Message }); }
		});
		api.MapDelete("/projects/{id}/conversation/images/{imageId}", (AinurRuntime rt, string id, string imageId) => {
			if(rt.Store.GetProject(id) is null) return Results.NotFound(new { error = "Project not found." });
			try { return rt.RemoveConversationImage(id, imageId) ? Results.NoContent() : Results.NotFound(new { error = "Image not found in this project/session or expired." }); }
			catch(ConversationConflictException e) { return Results.Conflict(new { error = e.Message }); }
			catch(DomainException e) { return Results.BadRequest(new { error = e.Message }); }
		});
	}
}
