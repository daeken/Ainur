using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Ainur.Core.Browser;

/// <summary>
/// One browser session per Ainur session, plus the live frame bus the web UI subscribes to.
/// Sessions are headless, use a throwaway profile under &lt;home&gt;/browser, and are reaped when idle.
/// </summary>
public sealed class BrowserManager : IAsyncDisposable {
	readonly string Home;
	readonly ConcurrentDictionary<string, BrowserSession> Sessions = new(StringComparer.Ordinal);
	readonly ConcurrentDictionary<string, SemaphoreSlim> SessionGates = new(StringComparer.Ordinal);
	readonly ConcurrentDictionary<string, long> StartedAt = new(StringComparer.Ordinal);
	readonly ConcurrentDictionary<string, string> AgentIds = new(StringComparer.Ordinal);
	readonly CancellationTokenSource Stop = new();
	readonly Timer Reaper;
	int Closing;
	static readonly ConditionalWeakTable<Runtime.AinurRuntime, BrowserManager> Instances = new();

	public BrowserOptions Defaults { get; init; } = new();

	public BrowserManager(string home) {
		Home = home;
		Reaper = new Timer(_ => _ = Reap(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
	}

	/// <summary>Manager bound to a runtime instance (one per runtime home).</summary>
	public static BrowserManager For(Runtime.AinurRuntime runtime) =>
		Instances.GetValue(runtime, r => new BrowserManager(Path.Combine(r.Options.Home, "browser")));

	/// <summary>Check for an already-created browser manager without allocating one on runtime/session disposal.</summary>
	public static bool TryFor(Runtime.AinurRuntime runtime, out BrowserManager? manager) =>
		Instances.TryGetValue(runtime, out manager);

	/// <summary>Live stream events for the UI: "session" (opened), "frame", "closed".</summary>
	public event Action<JsonObject>? StreamEvent;

	public BrowserSession? Get(string sessionKey) => Sessions.TryGetValue(sessionKey, out var s) ? s : null;
	public string Root => Home;

	public async Task<BrowserSession> AcquireAsync(string sessionKey, string agentId, BrowserOptions? overrides = null, CancellationToken ct = default) {
		var gate = SessionGates.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			ct.ThrowIfCancellationRequested();
			if(Volatile.Read(ref Closing) != 0) throw new ObjectDisposedException(nameof(BrowserManager));
			if(Sessions.TryGetValue(sessionKey, out var existing)) {
				if(existing.IsRunning) return existing;
				await DisposeEntryAsync(sessionKey, existing, "browser exited").ConfigureAwait(false);
			}
			var options = (overrides ?? Defaults) with { DataRoot = Home };
			var session = await BrowserSession.LaunchAsync(sessionKey, options, publish: null, ct).ConfigureAwait(false);
			// Runtime disposal may have started while launch awaited Chrome; never publish into a closing manager.
			if(Volatile.Read(ref Closing) != 0) {
				await session.DisposeAsync().ConfigureAwait(false);
				throw new ObjectDisposedException(nameof(BrowserManager));
			}
			Sessions[sessionKey] = session;
			var started = DateTimeOffset.UtcNow;
			StartedAt[sessionKey] = started.ToUnixTimeMilliseconds();
			AgentIds[sessionKey] = agentId;
			session.Frame += frame => PublishFrame(sessionKey, agentId, session, frame);
			session.Closed += _closedKey => {
				// An old process's late Exited callback must not evict a replacement with the same key.
				if(RemoveIfSame(sessionKey, session)) {
					StartedAt.TryRemove(sessionKey, out _);
					AgentIds.TryRemove(sessionKey, out _);
					Emit("closed", new JsonObject { ["id"] = sessionKey, ["agent_id"] = agentId, ["reason"] = "browser exited" });
				}
			};
			Emit("session", new JsonObject {
				["id"] = sessionKey, ["agent_id"] = agentId, ["state"] = "streaming",
				["url"] = "about:blank", ["title"] = "",
				["viewport"] = new JsonObject { ["width"] = options.Width, ["height"] = options.Height },
				["started_at"] = started.ToUnixTimeMilliseconds(),
			});
			return session;
		} finally { gate.Release(); }
	}

	bool RemoveIfSame(string key, BrowserSession session) =>
		((ICollection<KeyValuePair<string, BrowserSession>>) Sessions).Remove(new(key, session));

	/// <summary>Close only this session's browser, serialized with same-key acquisition.</summary>
	public async Task CloseSessionAsync(string sessionKey, string reason = "agent retired", CancellationToken ct = default) {
		var gate = SessionGates.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync(ct).ConfigureAwait(false);
		try {
			if(Sessions.TryGetValue(sessionKey, out var session))
				await DisposeEntryAsync(sessionKey, session, reason).ConfigureAwait(false);
		} finally { gate.Release(); }
	}

	async Task CloseIfSameAsync(string key, BrowserSession expected, string reason) {
		var gate = SessionGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
		await gate.WaitAsync().ConfigureAwait(false);
		try {
			if(Sessions.TryGetValue(key, out var current) && ReferenceEquals(expected, current))
				await DisposeEntryAsync(key, current, reason).ConfigureAwait(false);
		} finally { gate.Release(); }
	}

	async Task DisposeEntryAsync(string key, BrowserSession session, string reason) {
		if(RemoveIfSame(key, session)) {
			StartedAt.TryRemove(key, out _);
			AgentIds.TryRemove(key, out var agentId);
			Emit("closed", new JsonObject {
				["id"] = key, ["agent_id"] = agentId ?? "", ["reason"] = reason,
			});
		}
		await session.DisposeAsync().ConfigureAwait(false);
	}

	void PublishFrame(string sessionKey, string agentId, BrowserSession session, BrowserFrame frame) {
		Emit("frame", new JsonObject {
			["id"] = sessionKey,
			["agent_id"] = agentId,
			["seq"] = frame.Sequence,
			["data_url"] = "data:image/png;base64," + Convert.ToBase64String(frame.Png),
			["artifact"] = frame.ArtifactRef,
			["width"] = frame.Width,
			["height"] = frame.Height,
			["url"] = frame.Url,
			["title"] = frame.Title,
			["captured_at"] = frame.At.ToUnixTimeMilliseconds(),
		});
	}

	void Emit(string @event, JsonObject data) {
		var envelope = new JsonObject { ["event"] = @event, ["data"] = data };
		try { StreamEvent?.Invoke(envelope); } catch { }
	}

	/// <summary>Ids of browser sessions whose browser is still alive (drives the UI's session index).</summary>
	public IReadOnlyList<string> LiveIds => Sessions.Where(kv => kv.Value.IsRunning).Select(kv => kv.Key).ToList();

	/// <summary>The `session` payload for one id, per reference/browser-stream-contract §3.1. Null when unknown.</summary>
	public JsonObject? SessionSnapshot(string id) {
		if(!Sessions.TryGetValue(id, out var session)) return null;
		var frame = session.LatestFrame;
		return new JsonObject {
			["id"] = id,
			["agent_id"] = AgentIds.GetValueOrDefault(id, ""),
			["state"] = session.IsRunning ? (frame is null ? "starting" : "streaming") : "ended",
			["url"] = frame?.Url ?? "about:blank",
			["title"] = frame?.Title ?? "",
			["viewport"] = new JsonObject { ["width"] = session.Options.Width, ["height"] = session.Options.Height },
			["started_at"] = StartedAt.GetValueOrDefault(id, Clock.Now),
		};
	}

	/// <summary>The last frame captured by a session in `closed`/`frame` wire shape, or null.</summary>
	public JsonObject? LatestFrameEvent(string sessionKey) {
		var session = Get(sessionKey);
		return session?.LatestFrame is { } frame
			? new JsonObject {
				["id"] = sessionKey,
				["agent_id"] = AgentIds.GetValueOrDefault(sessionKey, ""),
				["seq"] = frame.Sequence,
				["data_url"] = "data:image/png;base64," + Convert.ToBase64String(frame.Png),
				["artifact"] = frame.ArtifactRef,
				["width"] = frame.Width,
				["height"] = frame.Height,
				["url"] = frame.Url,
				["title"] = frame.Title,
				["captured_at"] = frame.At.ToUnixTimeMilliseconds(),
			}
			: null;
	}

	async Task Reap() {
		if(Volatile.Read(ref Closing) != 0) return;
		foreach(var (key, session) in Sessions.ToList()) {
			try {
				if(!session.IsRunning) await CloseIfSameAsync(key, session, "browser exited").ConfigureAwait(false);
				else if(DateTimeOffset.UtcNow - session.LastUsed > session.Options.IdleTimeout)
					await CloseIfSameAsync(key, session, "idle timeout").ConfigureAwait(false);
			} catch { }
		}
	}

	public IReadOnlyList<BrowserSession> Active() => Sessions.Values.Where(s => s.IsRunning).ToList();

	public async ValueTask DisposeAsync() {
		if(Interlocked.Exchange(ref Closing, 1) != 0) return;
		await Reaper.DisposeAsync().ConfigureAwait(false);
		Stop.Cancel();
		// Every acquisition either finished before this snapshot (and is disposed here), or sees Closing
		// while holding its per-key gate and disposes its new browser without publishing it.
		foreach(var key in SessionGates.Keys.ToList()) {
			try { await CloseSessionAsync(key, "server shutdown").ConfigureAwait(false); } catch { }
		}
		foreach(var (key, session) in Sessions.ToList()) {
			try { await CloseSessionAsync(key, "server shutdown").ConfigureAwait(false); } catch { }
		}
		Sessions.Clear();
	}
}
