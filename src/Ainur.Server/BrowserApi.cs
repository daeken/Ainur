using System.Text.Json.Nodes;
using System.Threading.Channels;
using Ainur.Core.Browser;
using Ainur.Core.Runtime;

namespace Ainur.Server;

/// <summary>
/// Read-only HTTP surface for live browser sessions, per `reference/browser-stream-contract`:
/// the index, a one-shot latest frame, and the SSE stream the web UI embeds.
/// </summary>
public static class BrowserApi {
	/// <summary>Base path the UI builds against (VITE_BROWSER_STREAM default).</summary>
	public const string BasePath = "/api/v1/browser/sessions";

	public static void Map(RouteGroupBuilder api) {
		var group = api.MapGroup("/browser/sessions");

		// Index: live sessions only, and deliberately without frame bytes — the UI polls this while empty.
		group.MapGet("", (AinurRuntime rt) => Index(rt));

		// One-shot latest frame: lets a switching panel paint the current picture without opening a stream.
		group.MapGet("/{id}/frame", (AinurRuntime rt, string id) => {
			var manager = BrowserManager.For(rt);
			if(manager.Get(id) is null) return Results.NotFound(new { error = "unknown browser session" });
			if(manager.LatestFrameEvent(id) is not { } frame) return Results.NoContent();
			return Results.Text($"event: frame\ndata: {frame.ToJsonString()}\n\n", "text/event-stream");
		});

		// Live stream: replay current state, then stream until the browser session ends or the client leaves.
		group.MapGet("/{id}/stream", async (HttpContext ctx, AinurRuntime rt, string id) => {
			var manager = BrowserManager.For(rt);
			if(manager.Get(id) is null) {
				ctx.Response.StatusCode = 404;
				await ctx.Response.WriteAsJsonAsync(new { error = "unknown browser session" });
				return;
			}
			ctx.Response.Headers.ContentType = "text/event-stream";
			ctx.Response.Headers.CacheControl = "no-cache";
			ctx.Response.Headers["X-Accel-Buffering"] = "no";
			var ct = ctx.RequestAborted;
			var gate = new SemaphoreSlim(1, 1);
			var queue = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
			// The manager is the single source of truth; this subscription is removed with the request.
			Action<JsonObject> handler = envelope => {
				if((string?) envelope["data"]?["id"] != id) return;
				queue.Writer.TryWrite(envelope);
			};
			manager.StreamEvent += handler;
			try {
				async Task WriteAsync(string text) {
					await gate.WaitAsync(ct);
					try {
						await ctx.Response.WriteAsync(text, ct);
						await ctx.Response.Body.FlushAsync(ct);
					} finally { gate.Release(); }
				}
				await WriteAsync(": connected\n\n");
				// Replay: the session snapshot plus the latest frame, so a late subscriber paints immediately.
				if(manager.SessionSnapshot(id) is { } snapshot) await WriteAsync(Event("session", snapshot));
				if(manager.LatestFrameEvent(id) is { } frame) await WriteAsync(Event("frame", frame));
				// Keepalive: proxies and browsers drop idle connections, and an idle browser session sends nothing.
				var keepalive = Task.Run(async () => {
					try {
						while(!ct.IsCancellationRequested) {
							await Task.Delay(TimeSpan.FromSeconds(15), ct);
							await WriteAsync(":ka\n\n");
						}
					} catch(OperationCanceledException) { } catch { }
				}, CancellationToken.None);
				try {
					await foreach(var envelope in queue.Reader.ReadAllAsync(ct))
						await WriteAsync(Event((string?) envelope["event"] ?? "frame", envelope["data"]!.AsObject()));
				} catch(OperationCanceledException) { }
				await keepalive.WaitAsync(TimeSpan.FromSeconds(1)).ContinueWith(_ => { });
			} finally {
				manager.StreamEvent -= handler;
			}
		});
	}

	static IResult Index(AinurRuntime rt) {
		var manager = BrowserManager.For(rt);
		return Results.Json(manager.LiveIds.Select(id => manager.SessionSnapshot(id)).Where(s => s is not null));
	}

	static string Event(string name, JsonObject data) => $"event: {name}\ndata: {data.ToJsonString()}\n\n";
}
