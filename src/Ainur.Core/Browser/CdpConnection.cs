using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Ainur.Core.Browser;

public sealed class CdpException(string message, JsonNode? error) : Exception(message) {
	public JsonNode? Error { get; } = error;
}

/// <summary>
/// Minimal Chrome DevTools Protocol client: one WebSocket, flat session mode, request/response correlated by id,
/// plus one-shot event waits and an event feed. Deliberately dependency-free (System.Net.WebSockets only).
/// </summary>
public sealed class CdpConnection : IAsyncDisposable {
	readonly ClientWebSocket Ws = new();
	readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> Pending = new();
	readonly List<(string? Session, string Method, TaskCompletionSource<JsonNode?> Tcs)> Waits = [];
	readonly Lock Gate = new();
	readonly SemaphoreSlim SendGate = new(1, 1);
	readonly SemaphoreSlim RoundTrip = new(1, 1);
	readonly CancellationTokenSource Closed = new();
	int NextId;
	Exception? Failure;

	public event Action<string?, string, JsonNode?>? Event;

	public static async Task<CdpConnection> ConnectAsync(string url, CancellationToken ct = default) {
		var c = new CdpConnection();
		c.Ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
		await c.Ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
		_ = Task.Run(() => c.ReceiveLoopAsync(c.Closed.Token), CancellationToken.None);
		return c;
	}

	/// <summary>True while a request/response round trip is in flight. Callers that should not queue (the frame pump) use this to skip a tick.</summary>
	public bool IsBusy => RoundTrip.CurrentCount == 0;

	/// <summary>Sends a protocol command and awaits its result. Throws <see cref="CdpException"/> on a protocol error.</summary>
	/// <remarks>
	/// Round trips are serialised on purpose. Chrome's main thread is blocked while it answers a screenshot, so
	/// overlapping a Runtime.evaluate with a capture can starve the evaluate indefinitely (observed as tools hanging
	/// for their whole timeout while frames kept flowing). One command at a time keeps every caller answerable.
	/// </remarks>
	public async Task<JsonNode?> SendAsync(string method, JsonObject? parameters = null, string? sessionId = null, CancellationToken ct = default) {
		if (Failure is not null) throw new CdpException($"CDP connection is closed: {Failure.Message}", null);
		await RoundTrip.WaitAsync(ct).ConfigureAwait(false);
		try {
			return await SendCoreAsync(method, parameters, sessionId, ct).ConfigureAwait(false);
		} finally {
			RoundTrip.Release();
		}
	}

	async Task<JsonNode?> SendCoreAsync(string method, JsonObject? parameters, string? sessionId, CancellationToken ct) {
		if (Failure is not null) throw new CdpException($"CDP connection is closed: {Failure.Message}", null);
		var id = Interlocked.Increment(ref NextId);
		var msg = new JsonObject { ["id"] = id, ["method"] = method };
		if (parameters is not null) msg["params"] = parameters;
		if (sessionId is not null) msg["sessionId"] = sessionId;
		var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Pending[id] = tcs;
		try {
			await SendRawAsync(msg.ToJsonString(), ct).ConfigureAwait(false);
		} catch {
			Pending.TryRemove(id, out _);
			throw;
		}
		using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
		return await tcs.Task.ConfigureAwait(false);
	}

	/// <summary>Waits for the next protocol event with the given method (optionally scoped to a session).</summary>
	public async Task<JsonNode?> WaitForAsync(string method, string? sessionId, TimeSpan timeout, CancellationToken ct = default) {
		var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
		lock(Gate) Waits.Add((sessionId, method, tcs));
		using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		timeoutCts.CancelAfter(timeout);
		using var reg = timeoutCts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#}s waiting for CDP event {method}")));
		try {
			return await tcs.Task.ConfigureAwait(false);
		} finally {
			lock(Gate) Waits.RemoveAll(w => ReferenceEquals(w.Tcs, tcs));
		}
	}

	async Task SendRawAsync(string json, CancellationToken ct) {
		var bytes = Encoding.UTF8.GetBytes(json);
		await SendGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			await Ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
		} finally {
			SendGate.Release();
		}
	}

	async Task ReceiveLoopAsync(CancellationToken ct) {
		var buffer = new byte[64 * 1024];
		var ms = new MemoryStream();
		try {
			while(!ct.IsCancellationRequested && Ws.State == WebSocketState.Open) {
				var r = await Ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
				if(r.MessageType == WebSocketMessageType.Close) break;
				ms.Write(buffer, 0, r.Count);
				if(!r.EndOfMessage) continue;
				var text = Encoding.UTF8.GetString(ms.ToArray());
				ms.SetLength(0);
				Handle(text);
			}
		} catch(Exception e) {
			if(!ct.IsCancellationRequested) Failure = e;
		} finally {
			Failure ??= new IOException("CDP socket closed");
			foreach(var kv in Pending) kv.Value.TrySetException(Failure);
			Pending.Clear();
			lock(Gate) {
				foreach(var w in Waits) w.Tcs.TrySetException(Failure);
				Waits.Clear();
			}
		}
	}

	void Handle(string text) {
		JsonNode? node;
		try { node = JsonNode.Parse(text); } catch { return; }
		if(node is not JsonObject o) return;
		if(o["id"] is JsonValue idValue && idValue.TryGetValue<int>(out var id)) {
			if(!Pending.TryRemove(id, out var tcs)) return;
			if(o["error"] is JsonNode err)
				tcs.TrySetException(new CdpException($"CDP {o["method"]?.GetValue<string>() ?? "call"} failed: {err["message"]?.GetValue<string>() ?? err.ToJsonString()}", err));
			else
				tcs.TrySetResult(o["result"]);
			return;
		}
		if(o["method"]?.GetValue<string>() is not { } method) return;
		var session = o["sessionId"]?.GetValue<string>();
		var p = o["params"];
		Event?.Invoke(session, method, p);
		List<(string? Session, string Method, TaskCompletionSource<JsonNode?> Tcs)> hits;
		lock(Gate) {
			hits = Waits.Where(w => w.Method == method && (w.Session is null || w.Session == session)).ToList();
			foreach(var h in hits) Waits.Remove(h);
		}
		foreach(var h in hits) h.Tcs.TrySetResult(p);
	}

	public async ValueTask DisposeAsync() {
		try { Closed.Cancel(); } catch { }
		try {
			if(Ws.State == WebSocketState.Open)
				await Ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None).ConfigureAwait(false);
		} catch { }
		Ws.Dispose();
	}
}
