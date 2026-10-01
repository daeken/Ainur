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
	readonly ConcurrentDictionary<string, long> StartedAt = new(StringComparer.Ordinal);
	readonly CancellationTokenSource Stop = new();
	readonly Timer Reaper;
	static readonly ConditionalWeakTable<Runtime.AinurRuntime, BrowserManager> Instances = new();

	public BrowserOptions Defaults { get; init; } = new();

	public BrowserManager(string home) {
		Home = home;
		Reaper = new Timer(_ => _ = Reap(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
	}

	/// <summary>Manager bound to a runtime instance (one per runtime home).</summary>
	public static BrowserManager For(Runtime.AinurRuntime runtime) =>
		Instances.GetValue(runtime, r => new BrowserManager(Path.Combine(r.Options.Home, "browser")));

	/// <summary>Live stream events for the UI: "session" (opened), "frame", "closed".</summary>
	public event Action<JsonObject>? StreamEvent;

	public BrowserSession? Get(string sessionKey) => Sessions.TryGetValue(sessionKey, out var s) ? s : null;
	public string Root => Home;

	public async Task<BrowserSession> AcquireAsync(string sessionKey, string agentId, BrowserOptions? overrides = null, CancellationToken ct = default) {
		if(Sessions.TryGetValue(sessionKey, out var existing) && existing.IsRunning) return existing;
		var options = (overrides ?? Defaults) with { DataRoot = Home };
		var session = await BrowserSession.LaunchAsync(sessionKey, options, publish: null, ct).ConfigureAwait(false);
		Sessions[sessionKey] = session;
		var started = DateTimeOffset.UtcNow;
		StartedAt[sessionKey] = started.ToUnixTimeMilliseconds();
		session.Frame += frame => PublishFrame(sessionKey, agentId, session, frame);
		session.Closed += closedKey => {
			if(Sessions.TryRemove(closedKey, out _)) {
				StartedAt.TryRemove(closedKey, out _);
				Emit("closed", new JsonObject { ["id"] = closedKey, ["agent_id"] = agentId, ["reason"] = "browser exited" });
			}
		};
		Emit("session", new JsonObject {
			["id"] = sessionKey,
			["agent_id"] = agentId,
			["state"] = "streaming",
			["url"] = "about:blank",
			["title"] = "",
			["viewport"] = new JsonObject { ["width"] = options.Width, ["height"] = options.Height },
			["started_at"] = started.ToUnixTimeMilliseconds(),
		});
		return session;
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

	/// <summary>The last frame captured by a session, if any (for a late-subscribing UI).</summary>
	public JsonObject? LatestFrameEvent(string sessionKey) {
		var session = Get(sessionKey);
		return session?.LatestFrame is { } frame
			? new JsonObject {
				["id"] = sessionKey,
				["seq"] = frame.Sequence,
				["data_url"] = "data:image/png;base64," + Convert.ToBase64String(frame.Png),
				["width"] = frame.Width,
				["height"] = frame.Height,
				["url"] = frame.Url,
				["title"] = frame.Title,
				["captured_at"] = frame.At.ToUnixTimeMilliseconds(),
			}
			: null;
	}

	async Task Reap() {
		foreach(var (key, session) in Sessions.ToList()) {
			try {
				if(!session.IsRunning) {
					if(Sessions.TryRemove(key, out _)) Emit("closed", new JsonObject { ["id"] = key, ["reason"] = "browser exited" });
					continue;
				}
				if(DateTimeOffset.UtcNow - session.LastUsed > session.Options.IdleTimeout) {
					if(Sessions.TryRemove(key, out _)) {
						StartedAt.TryRemove(key, out _);
						Emit("closed", new JsonObject { ["id"] = key, ["reason"] = "idle timeout" });
					}
					await session.DisposeAsync().ConfigureAwait(false);
				}
			} catch { }
		}
	}

	public IReadOnlyList<BrowserSession> Active() => Sessions.Values.Where(s => s.IsRunning).ToList();

	public async ValueTask DisposeAsync() {
		await Reaper.DisposeAsync().ConfigureAwait(false);
		Stop.Cancel();
		foreach(var session in Sessions.Values.ToList()) {
			try { await session.DisposeAsync().ConfigureAwait(false); } catch { }
		}
		Sessions.Clear();
	}
}
