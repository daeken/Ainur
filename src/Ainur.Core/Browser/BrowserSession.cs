using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ainur.Core.Browser;

/// <summary>Options for launching a browser session. Headless and an isolated profile are the defaults, always.</summary>
public sealed record BrowserOptions {
	public bool Headless { get; init; } = true;
	public string? ExecutablePath { get; init; }
	public int Width { get; init; } = 1280;
	public int Height { get; init; } = 800;
	public int FrameIntervalMs { get; init; } = 500;
	public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(30);
	public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(10);
	/// <summary>Root for per-session profile directories. Defaults to the system temp dir; the runtime points it at &lt;home&gt;/browser.</summary>
	public string? DataRoot { get; init; }
}

/// <summary>One captured viewport frame: bytes for the live stream plus the content-addressed reference when stored.</summary>
public sealed record BrowserFrame(string Key, string Url, string Title, byte[] Png, string? ArtifactRef, int Width, int Height, long Sequence, DateTimeOffset At);

/// <summary>A compact, model-readable description of what is on the page right now.</summary>
public sealed record BrowserObservation(string Url, string Title, string Text, IReadOnlyList<BrowserElement> Elements, string? ScreenshotRef, int ScrollY, int PageHeight);

public sealed record BrowserElement(int Index, string Tag, string Name, int X, int Y, bool Disabled);

public sealed class BrowserException(string message) : Exception(message);

/// <summary>
/// A single headless Chromium driven over the DevTools Protocol. Owns its own process and its own throwaway
/// profile directory: it never touches the user's real Chrome profile, cookies, or extensions.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable {
	public readonly string Key;
	public readonly BrowserOptions Options;
	public readonly string ProfileDirectory;
	public readonly int Port;
	public readonly int ProcessId;

	readonly Process Process;
	readonly CdpConnection Cdp;
	readonly string CdpSession;
	readonly CancellationTokenSource Stop = new();
	readonly SemaphoreSlim CaptureGate = new(1, 1);
	long FrameSeq;
	volatile BrowserFrame? Latest;
	readonly Queue<BrowserFrame> RecentFrames = new();
	readonly Lock FrameGate = new();
	Action<BrowserFrame>? FrameHandlers;
	int Pumping;
	volatile bool StopRequested;
	Task? PumpTask;
	long LastUsedTicks = Environment.TickCount64;

	public event Action<BrowserFrame>? Frame {
		add { lock(FrameGate) FrameHandlers += value; EnsureFramePump(); }
		remove { lock(FrameGate) FrameHandlers -= value; }
	}
	public event Action<string>? Closed;

	public BrowserFrame? LatestFrame => Latest;
	public bool IsRunning => !Process.HasExited;
	public DateTimeOffset LastUsed => DateTimeOffset.UtcNow.AddMilliseconds(-(Environment.TickCount64 - LastUsedTicks));
	public IReadOnlyList<BrowserFrame> Recent() { lock(FrameGate) return RecentFrames.ToList(); }

	BrowserSession(string key, BrowserOptions options, string executable, string profileDirectory, int port, Process process, CdpConnection cdp, string cdpSession) {
		Key = key; Options = options; ProfileDirectory = profileDirectory; Port = port; Process = process; Cdp = cdp; CdpSession = cdpSession; ProcessId = process.Id;
	}

	/// <summary>Locates a Chrome/Chromium binary. Returns null when none is installed.</summary>
	public static string? FindExecutable(string? explicitPath = null) {
		if(!string.IsNullOrWhiteSpace(explicitPath) && File.Exists(explicitPath)) return explicitPath;
		if(Environment.GetEnvironmentVariable("AINUR_BROWSER_EXECUTABLE") is { Length: > 0 } env && File.Exists(env)) return env;
		List<string> candidates = OperatingSystem.IsMacOS()
			? ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Chromium.app/Contents/MacOS/Chromium",
			   "/Applications/Google Chrome Canary.app/Contents/MacOS/Google Chrome Canary", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
			   "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser"]
			: OperatingSystem.IsWindows()
				? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
				   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
				   Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Chromium", "Application", "chrome.exe")]
				: ["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/snap/bin/chromium"];
		return candidates.FirstOrDefault(File.Exists);
	}

	public static async Task<BrowserSession> LaunchAsync(string key, BrowserOptions options, Func<byte[], string>? publish = null, CancellationToken ct = default) {
		var executable = FindExecutable(options.ExecutablePath) ?? throw new BrowserException(
			"No Chrome/Chromium executable found. Install one or set AINUR_BROWSER_EXECUTABLE.");
		var root = options.DataRoot ?? Path.Combine(Path.GetTempPath(), "ainur-browser");
		var safeKey = new string(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c is ':' or '/' or '\\' ? '_' : c).ToArray());
		var profile = Path.Combine(root, safeKey, "profile");
		Directory.CreateDirectory(profile);

		var port = FreePort();
		var args = new List<string> {
			options.Headless ? "--headless=new" : "--headless",
			"--disable-gpu", "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-sync",
			"--disable-background-networking", "--disable-component-update", "--disable-default-apps", "--disable-translate",
			"--disable-features=Translate,MediaRouter,OptimizationHints", "--mute-audio", "--no-service-autorun",
			"--remote-allow-origins=*", "--enable-unsafe-swiftshader",
			$"--remote-debugging-port={port}", $"--user-data-dir={profile}", $"--window-size={options.Width},{options.Height}",
			"about:blank",
		};
		var psi = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach(var a in args) psi.ArgumentList.Add(a);
		var process = Process.Start(psi) ?? throw new BrowserException($"Failed to start {executable}");
		// Drain the pipes so a full stderr buffer can never block the renderer, and keep the noise out of our logs.
		_ = Task.Run(() => DrainAsync(process.StandardOutput));
		_ = Task.Run(() => DrainAsync(process.StandardError));

		var wsUrl = await WaitForEndpointAsync(port, process, options.LaunchTimeout, ct).ConfigureAwait(false);
		var cdp = await CdpConnection.ConnectAsync(wsUrl, ct).ConfigureAwait(false);
		JsonNode? created;
		try {
			created = await cdp.SendAsync("Target.createTarget", new JsonObject { ["url"] = "about:blank" }, null, ct).ConfigureAwait(false);
		} catch {
			await cdp.DisposeAsync();
			KillProcess(process);
			throw;
		}
		var targetId = created?["targetId"]?.GetValue<string>() ?? throw new BrowserException("Chrome did not return a target id");
		var attached = await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = targetId, ["flatten"] = true }, null, ct).ConfigureAwait(false);
		var cdpSession = attached?["sessionId"]?.GetValue<string>() ?? throw new BrowserException("Chrome did not return a session id for the new target");
		var session = new BrowserSession(key, options, executable, profile, port, process, cdp, cdpSession);
		await session.ConfigurePageAsync(ct).ConfigureAwait(false);
		process.EnableRaisingEvents = true;
		process.Exited += (_, _) => session.Closed?.Invoke(key);
		return session;
	}

	async Task ConfigurePageAsync(CancellationToken ct) {
		await Cdp.SendAsync("Page.enable", null, CdpSession, ct).ConfigureAwait(false);
		await Cdp.SendAsync("Runtime.enable", null, CdpSession, ct).ConfigureAwait(false);
		await Cdp.SendAsync("DOM.enable", null, CdpSession, ct).ConfigureAwait(false);
		await Cdp.SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject {
			["width"] = Options.Width, ["height"] = Options.Height, ["deviceScaleFactor"] = 1, ["mobile"] = false,
		}, CdpSession, ct).ConfigureAwait(false);
	}

	static async Task DrainAsync(StreamReader reader) {
		try { while(await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } } catch { }
	}

	static int FreePort() {
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try { return ((IPEndPoint) listener.LocalEndpoint).Port; } finally { listener.Stop(); }
	}

	static async Task<string> WaitForEndpointAsync(int port, Process process, TimeSpan timeout, CancellationToken ct) {
		var deadline = DateTime.UtcNow + timeout;
		using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
		Exception? last = null;
		while(DateTime.UtcNow < deadline) {
			if(process.HasExited) throw new BrowserException($"Chrome exited during startup with code {process.ExitCode}");
			try {
				var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", ct).ConfigureAwait(false);
				var ws = JsonNode.Parse(json)?["webSocketDebuggerUrl"]?.GetValue<string>();
				if(!string.IsNullOrEmpty(ws)) return ws;
			} catch(Exception e) { last = e; }
			await Task.Delay(150, ct).ConfigureAwait(false);
		}
		throw new BrowserException($"Chrome did not expose a DevTools endpoint on port {port} within {timeout.TotalSeconds:0}s ({last?.Message})");
	}

	static void KillProcess(Process process) {
		try { if(!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
	}

	void Touch() => LastUsedTicks = Environment.TickCount64;

	// ---- Page commands ----

	public async Task<BrowserObservation> NavigateAsync(string url, TimeSpan? timeout = null, CancellationToken ct = default) {
		Touch();
		var t = timeout ?? TimeSpan.FromSeconds(30);
		var loaded = Cdp.WaitForAsync("Page.loadEventFired", CdpSession, t, ct);
		var result = await Cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = url }, CdpSession, ct).ConfigureAwait(false);
		if(result?["errorText"]?.GetValue<string>() is { Length: > 0 } error)
			throw new BrowserException($"Navigation to {url} failed: {error}");
		try {
			await loaded.ConfigureAwait(false);
		} catch(TimeoutException) {
			var ready = await EvalAsync("document.readyState", ct).ConfigureAwait(false) as JsonValue;
			if(ready?.GetValue<string>() is not "complete" and not "interactive")
				throw new BrowserException($"Navigation to {url} did not load within {t.TotalSeconds:0}s");
		}
		await SettleAsync(ct).ConfigureAwait(false);
		return await ObserveAsync(ct: ct).ConfigureAwait(false);
	}

	public async Task<BrowserObservation> BackAsync(CancellationToken ct = default) {
		Touch();
		var loaded = Cdp.WaitForAsync("Page.loadEventFired", CdpSession, TimeSpan.FromSeconds(5), ct);
		await EvalAsync("history.back()", ct).ConfigureAwait(false);
		try { await loaded.ConfigureAwait(false); } catch(TimeoutException) { }
		await SettleAsync(ct).ConfigureAwait(false);
		return await ObserveAsync(ct: ct).ConfigureAwait(false);
	}

	public async Task<BrowserObservation> WaitAsync(int milliseconds, string? selector = null, TimeSpan? timeout = null, CancellationToken ct = default) {
		Touch();
		var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
		if(selector is null) {
			await Task.Delay(Math.Clamp(milliseconds, 0, 60_000), ct).ConfigureAwait(false);
			return await ObserveAsync(ct: ct).ConfigureAwait(false);
		}
		while(DateTime.UtcNow < deadline) {
			var found = await EvalAsync($"!!document.querySelector({Js(selector)})", ct).ConfigureAwait(false) as JsonValue;
			if(found?.GetValue<bool>() == true) return await ObserveAsync(ct: ct).ConfigureAwait(false);
			await Task.Delay(200, ct).ConfigureAwait(false);
		}
		throw new BrowserException($"Selector {selector} did not appear within {(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds:0}s");
	}

	public async Task<BrowserObservation> ClickAsync(string? selector = null, int? index = null, double? x = null, double? y = null, CancellationToken ct = default) {
		Touch();
		double px, py;
		if(selector is not null) (px, py) = await PointForSelectorAsync(selector, ct).ConfigureAwait(false);
		else if(index is not null) (px, py) = await PointForIndexAsync(index.Value, ct).ConfigureAwait(false);
		else if(x is not null && y is not null) (px, py) = (x.Value, y.Value);
		else throw new BrowserException("browser_click needs one of selector, index, or x/y");
		await DispatchMouseAsync(px, py, ct).ConfigureAwait(false);
		await SettleAsync(ct).ConfigureAwait(false);
		return await ObserveAsync(ct: ct).ConfigureAwait(false);
	}

	public async Task<BrowserObservation> TypeAsync(string text, string? selector = null, int? index = null, bool submit = false, CancellationToken ct = default) {
		Touch();
		if(selector is not null || index is not null) {
			var (px, py) = selector is not null ? await PointForSelectorAsync(selector, ct).ConfigureAwait(false) : await PointForIndexAsync(index!.Value, ct).ConfigureAwait(false);
			await DispatchMouseAsync(px, py, ct).ConfigureAwait(false);
		}
		await Cdp.SendAsync("Input.insertText", new JsonObject { ["text"] = text }, CdpSession, ct).ConfigureAwait(false);
		if(submit) await KeyAsync("Enter", ct).ConfigureAwait(false);
		await SettleAsync(ct).ConfigureAwait(false);
		return await ObserveAsync(ct: ct).ConfigureAwait(false);
	}

	public async Task<BrowserObservation> KeyAsync(string key, CancellationToken ct = default) {
		Touch();
		var (code, vk, text) = key switch {
			"Enter" => ("Enter", 13, "\r"),
			"Tab" => ("Tab", 9, "\t"),
			"Escape" => ("Escape", 27, ""),
			"Backspace" => ("Backspace", 8, ""),
			"ArrowDown" => ("ArrowDown", 40, ""),
			"ArrowUp" => ("ArrowUp", 38, ""),
			_ => (key, 0, ""),
		};
		await Cdp.SendAsync("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyDown", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk, ["text"] = text }, CdpSession, ct).ConfigureAwait(false);
		await Cdp.SendAsync("Input.dispatchKeyEvent", new JsonObject { ["type"] = "keyUp", ["key"] = key, ["code"] = code, ["windowsVirtualKeyCode"] = vk }, CdpSession, ct).ConfigureAwait(false);
		await SettleAsync(ct).ConfigureAwait(false);
		return await ObserveAsync(ct: ct).ConfigureAwait(false);
	}

	public async Task<BrowserObservation> ScrollAsync(int deltaY, CancellationToken ct = default) {
		Touch();
		await EvalAsync($"window.scrollBy(0,{deltaY}); true", ct).ConfigureAwait(false);
		await Task.Delay(150, ct).ConfigureAwait(false);
		return await ObserveAsync(ct: ct).ConfigureAwait(false);
	}

	/// <summary>Captures the viewport as PNG. The caller decides whether to persist it as an artifact.</summary>
	public Task<BrowserFrame> ScreenshotAsync(Func<byte[], string>? publish = null, CancellationToken ct = default) =>
		CaptureAsync(touch: true, publish, ct);

	async Task<BrowserFrame> CaptureAsync(bool touch, Func<byte[], string>? publish, CancellationToken ct) {
		if(touch) Touch();
		await CaptureGate.WaitAsync(ct).ConfigureAwait(false);
		try {
			var result = await Cdp.SendAsync("Page.captureScreenshot", new JsonObject { ["format"] = "png", ["fromSurface"] = true, ["captureBeyondViewport"] = false }, CdpSession, ct).ConfigureAwait(false);
			var png = Convert.FromBase64String(result?["data"]?.GetValue<string>() ?? throw new BrowserException("Chrome returned an empty screenshot"));
			var (url, title) = await UrlAndTitleAsync(ct).ConfigureAwait(false);
			var frame = new BrowserFrame(Key, url, title, png, publish?.Invoke(png), Options.Width, Options.Height, Interlocked.Increment(ref FrameSeq), DateTimeOffset.UtcNow);
			Latest = frame;
			lock(FrameGate) {
				RecentFrames.Enqueue(frame);
				while(RecentFrames.Count > 30) RecentFrames.Dequeue();
			}
			return frame;
		} finally {
			CaptureGate.Release();
		}
	}

	/// <summary>Reads the page: "text" (visible text), "ax" (accessibility tree), "links", or "interactive".</summary>
	public async Task<string> ReadAsync(string mode, int maxChars = 8000, CancellationToken ct = default) {
		Touch();
		var (url, title) = await UrlAndTitleAsync(ct).ConfigureAwait(false);
		var header = $"url: {url}\ntitle: {title}\n";
		var body = mode switch {
			"ax" => await ReadAccessibilityAsync(ct).ConfigureAwait(false),
			"links" => await ReadLinksAsync(ct).ConfigureAwait(false),
			"interactive" => FormatElements(await ElementsAsync(ct).ConfigureAwait(false)),
			_ => await EvalStringAsync("document.body ? document.body.innerText : ''", ct).ConfigureAwait(false),
		};
		return header + Truncate(body, maxChars);
	}

	public async Task<BrowserObservation> ObserveAsync(string? screenshotRef = null, CancellationToken ct = default) {
		var (url, title) = await UrlAndTitleAsync(ct).ConfigureAwait(false);
		var text = await EvalStringAsync("document.body ? document.body.innerText : ''", ct).ConfigureAwait(false);
		var elements = await ElementsAsync(ct).ConfigureAwait(false);
		var scroll = await EvalAsync("Math.round(window.scrollY)", ct).ConfigureAwait(false) as JsonValue;
		var height = await EvalAsync("Math.round(document.documentElement.scrollHeight)", ct).ConfigureAwait(false) as JsonValue;
		return new BrowserObservation(url, title, Truncate(text, 4000), elements, screenshotRef,
			scroll?.GetValue<int>() ?? 0, height?.GetValue<int>() ?? Options.Height);
	}

	public async Task<IReadOnlyList<BrowserElement>> ElementsAsync(CancellationToken ct = default) {
		var raw = await EvalAsync(InteractiveElementsJs, ct).ConfigureAwait(false) as JsonArray;
		var list = new List<BrowserElement>();
		if(raw is null) return list;
		foreach(var item in raw.OfType<JsonObject>()) {
			list.Add(new BrowserElement(
				item["index"]?.GetValue<int>() ?? 0,
				item["tag"]?.GetValue<string>() ?? "",
				item["name"]?.GetValue<string>() ?? "",
				item["x"]?.GetValue<int>() ?? 0,
				item["y"]?.GetValue<int>() ?? 0,
				item["disabled"]?.GetValue<bool>() ?? false));
		}
		return list;
	}

	public static string FormatObservation(BrowserObservation o) {
		var sb = new StringBuilder();
		sb.AppendLine($"url: {o.Url}");
		sb.AppendLine($"title: {o.Title}");
		sb.AppendLine($"scroll: {o.ScrollY}px of {o.PageHeight}px");
		if(o.Elements.Count > 0) {
			sb.AppendLine($"interactive elements ({o.Elements.Count}):");
			foreach(var e in o.Elements) sb.AppendLine($"  [{e.Index}] {e.Tag}{(e.Disabled ? " (disabled)" : "")} \"{e.Name}\" at ({e.X},{e.Y})");
		}
		sb.AppendLine("visible text:");
		sb.AppendLine(o.Text);
		return sb.ToString();
	}

	// ---- Internals ----

	async Task SettleAsync(CancellationToken ct) {
		for(var i = 0; i < 20; i++) {
			var ready = (await EvalAsync("document.readyState", ct).ConfigureAwait(false) as JsonValue)?.GetValue<string>();
			if(ready == "complete") break;
			await Task.Delay(100, ct).ConfigureAwait(false);
		}
		await Task.Delay(120, ct).ConfigureAwait(false);
	}

	async Task<(string Url, string Title)> UrlAndTitleAsync(CancellationToken ct) {
		var url = await EvalStringAsync("location.href", ct).ConfigureAwait(false);
		var title = await EvalStringAsync("document.title", ct).ConfigureAwait(false);
		return (url, title);
	}

	async Task<string> EvalStringAsync(string expression, CancellationToken ct) {
		var node = await EvalAsync(expression, ct).ConfigureAwait(false);
		return node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
	}

	async Task<JsonNode?> EvalAsync(string expression, CancellationToken ct) {
		var result = await Cdp.SendAsync("Runtime.evaluate", new JsonObject {
			["expression"] = expression, ["returnByValue"] = true, ["awaitPromise"] = true,
		}, CdpSession, ct).ConfigureAwait(false);
		if(result?["exceptionDetails"] is JsonObject ex)
			throw new BrowserException($"Page script failed: {ex["exception"]?["description"]?.GetValue<string>() ?? ex.ToJsonString()}");
		return result?["result"]?["value"];
	}

	async Task<(double X, double Y)> PointForSelectorAsync(string selector, CancellationToken ct) {
		var point = await EvalAsync($$"""
			(() => { const el = document.querySelector({{Js(selector)}}); if (!el) return null;
			  el.scrollIntoView({ block: 'center', inline: 'center' });
			  const r = el.getBoundingClientRect();
			  return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()
			""", ct).ConfigureAwait(false);
		if(point is not JsonObject p) throw new BrowserException($"No element matches selector {selector}");
		return (p["x"]!.GetValue<double>(), p["y"]!.GetValue<double>());
	}

	async Task<(double X, double Y)> PointForIndexAsync(int index, CancellationToken ct) {
		var point = await EvalAsync($$"""
			(() => { const el = window.__ainurElements && window.__ainurElements[{{index.ToString(CultureInfo.InvariantCulture)}}];
			  if (!el) return null;
			  el.scrollIntoView({ block: 'center', inline: 'center' });
			  const r = el.getBoundingClientRect();
			  return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; })()
			""", ct).ConfigureAwait(false);
		if(point is not JsonObject p) throw new BrowserException($"No interactive element with index {index}; call the tool with action=screenshot first");
		return (p["x"]!.GetValue<double>(), p["y"]!.GetValue<double>());
	}

	async Task DispatchMouseAsync(double x, double y, CancellationToken ct) {
		var point = new JsonObject { ["x"] = x, ["y"] = y };
		await Cdp.SendAsync("Input.dispatchMouseEvent", Merge(point, new JsonObject { ["type"] = "mouseMoved" }), CdpSession, ct).ConfigureAwait(false);
		await Cdp.SendAsync("Input.dispatchMouseEvent", Merge(point, new JsonObject { ["type"] = "mousePressed", ["button"] = "left", ["clickCount"] = 1 }), CdpSession, ct).ConfigureAwait(false);
		await Cdp.SendAsync("Input.dispatchMouseEvent", Merge(point, new JsonObject { ["type"] = "mouseReleased", ["button"] = "left", ["clickCount"] = 1 }), CdpSession, ct).ConfigureAwait(false);
	}

	async Task<string> ReadAccessibilityAsync(CancellationToken ct) {
		var result = await Cdp.SendAsync("Accessibility.getFullAXTree", null, CdpSession, ct).ConfigureAwait(false);
		if(result?["nodes"] is not JsonArray nodes) return "";
		var byId = new Dictionary<string, JsonObject>();
		foreach(var node in nodes.OfType<JsonObject>()) if(node["nodeId"]?.GetValue<string>() is { } id) byId[id] = node;
		var isChild = new HashSet<string>();
		foreach(var node in byId.Values) if(node["childIds"] is JsonArray kids) foreach(var kid in kids) if(kid?.GetValue<string>() is { } cid) isChild.Add(cid);
		var sb = new StringBuilder();
		void Render(string id, int depth) {
			if(sb.Length > 6000 || !byId.TryGetValue(id, out var node)) return;
			if(node["ignored"]?.GetValue<bool>() != true) {
				var role = node["role"]?["value"]?.GetValue<string>() ?? "";
				var name = node["name"]?["value"]?.GetValue<string>() ?? "";
				if(name.Length > 0 || role.Length > 0) sb.AppendLine($"{new string(' ', Math.Min(depth, 24) * 2)}{role} \"{Truncate(name, 120)}\"");
			}
			if(node["childIds"] is JsonArray kids) foreach(var kid in kids) if(kid?.GetValue<string>() is { } cid) Render(cid, depth + 1);
		}
		foreach(var root in byId.Keys.Where(k => !isChild.Contains(k)).ToList()) Render(root, 0);
		return sb.ToString();
	}

	async Task<string> ReadLinksAsync(CancellationToken ct) {
		var links = await EvalAsync("""
			[...document.querySelectorAll('a[href]')].slice(0, 200).map(a => ({ text: (a.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 120), href: a.href }))
			""", ct).ConfigureAwait(false) as JsonArray;
		if(links is null) return "";
		var sb = new StringBuilder();
		foreach(var link in links.OfType<JsonObject>())
			sb.AppendLine($"- {Truncate(link["text"]?.GetValue<string>() ?? "", 120)} -> {link["href"]?.GetValue<string>()}");
		return sb.ToString();
	}

	static string FormatElements(IReadOnlyList<BrowserElement> elements) {
		var sb = new StringBuilder();
		foreach(var e in elements) sb.AppendLine($"[{e.Index}] {e.Tag}{(e.Disabled ? " (disabled)" : "")} \"{e.Name}\" at ({e.X},{e.Y})");
		return sb.ToString();
	}

	static JsonObject Merge(JsonObject a, JsonObject b) {
		var copy = (JsonObject) JsonNode.Parse(a.ToJsonString())!;
		foreach(var kv in b) copy[kv.Key] = kv.Value?.DeepClone();
		return copy;
	}

	static string Js(string value) => JsonSerializer.Serialize(value);

	static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + $"\n… (+{text.Length - max} chars truncated)";

	const string InteractiveElementsJs = """
		(() => {
		  const selector = 'a[href],button,input,textarea,select,[role=button],[role=link],[role=checkbox],[role=tab],[contenteditable=true],[onclick]';
		  window.__ainurElements = [];
		  const out = [];
		  for (const el of document.querySelectorAll(selector)) {
		    const r = el.getBoundingClientRect();
		    const st = getComputedStyle(el);
		    if (r.width < 2 || r.height < 2 || st.visibility === 'hidden' || st.display === 'none' || Number(st.opacity) === 0) continue;
		    if (el.getAttribute('type') === 'hidden') continue;
		    window.__ainurElements.push(el);
		    out.push({
		      index: out.length,
		      tag: el.tagName.toLowerCase(),
		      name: (el.getAttribute('aria-label') || el.getAttribute('placeholder') || el.innerText || el.value || el.getAttribute('name') || '').replace(/\s+/g, ' ').trim().slice(0, 80),
		      x: Math.round(r.left + r.width / 2),
		      y: Math.round(r.top + r.height / 2),
		      disabled: !!el.disabled
		    });
		    if (out.length >= 100) break;
		  }
		  return out;
		})()
		""";

	void EnsureFramePump() {
		if(Interlocked.CompareExchange(ref Pumping, 1, 0) == 0) PumpTask = PumpAsync();
	}

	async Task PumpAsync() {
		try {
			while(!StopRequested) {
				Action<BrowserFrame>? handlers;
				lock(FrameGate) handlers = FrameHandlers;
				if(handlers is null) break;
				// Deliberately not cancellable: cancelling an in-flight CDP send while the semaphore wait and the
				// pending request both unwind resumes every awaiter inline and blew the stack (observed). The pump
				// is stopped by this flag and by the socket closing instead.
				if(!Cdp.IsBusy) {
					try {
						handlers(await CaptureAsync(touch: false, publish: null, CancellationToken.None).ConfigureAwait(false));
					} catch when(StopRequested) { break; } catch { }
				}
				try { await Task.Delay(Options.FrameIntervalMs, CancellationToken.None).ConfigureAwait(false); } catch { }
			}
		} finally {
			Interlocked.Exchange(ref Pumping, 0);
			bool again;
			lock(FrameGate) again = FrameHandlers is not null && !StopRequested;
			if(again) EnsureFramePump();
		}
	}

	public async ValueTask DisposeAsync() {
		StopRequested = true;
		var pump = PumpTask;
		if(pump is not null) {
			try { await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
		}
		Stop.Cancel();
		try {
			await Cdp.SendAsync("Browser.close", null, null, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
		} catch { }
		await Cdp.DisposeAsync().ConfigureAwait(false);
		KillProcess(Process);
		try { Directory.Delete(ProfileDirectory, recursive: true); } catch { }
		Closed?.Invoke(Key);
	}
}
