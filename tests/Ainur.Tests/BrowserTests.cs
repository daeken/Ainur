using System.Net;
using System.Text;
using Ainur.Core.Browser;

namespace Ainur.Tests;

/// <summary>
/// Offline browser tests: a real headless Chromium (skipped when none is installed) against a local HTTP
/// fixture. No network, no model calls. Every session is disposed and every temp dir removed.
/// </summary>
[Trait("Category", "Browser")]
public sealed class BrowserTests : IAsyncLifetime {
	readonly string Home = Path.Combine(Path.GetTempPath(), "ainur-browser-test-" + Guid.NewGuid().ToString("N")[..8]);
	static HttpListener? Fixture;
	static string FixtureUrl = "";

	public async Task InitializeAsync() {
		Directory.CreateDirectory(Home);
		if(Fixture is null) {
			Fixture = new HttpListener();
			FixtureUrl = $"http://127.0.0.1:{FreePort()}/";
			Fixture.Prefixes.Add(FixtureUrl);
			Fixture.Start();
			_ = Task.Run(async () => {
				while(Fixture.IsListening) {
					HttpListenerContext ctx;
					try { ctx = await Fixture.GetContextAsync(); } catch { return; }
					var second = ctx.Request.Url!.AbsolutePath == "/next";
					var html = second
						? "<html><head><title>Second Page</title></head><body><h1>Second page</h1><p>You followed the link.</p></body></html>"
						: """
						  <html><head><title>Fixture Home</title></head><body><h1>Hello Ainur</h1>
						  <p>Locally served fixture.</p>
						  <p><a id="go" href="/next">Go to the second page</a></p><input id="q" placeholder="search box"></body></html>
						  """;
					var bytes = Encoding.UTF8.GetBytes(html);
					ctx.Response.ContentType = "text/html; charset=utf-8";
					ctx.Response.ContentLength64 = bytes.Length;
					await ctx.Response.OutputStream.WriteAsync(bytes);
					ctx.Response.Close();
				}
			});
		}
		await Task.Yield();
	}

	public Task DisposeAsync() {
		try { Directory.Delete(Home, true); } catch { }
		return Task.CompletedTask;
	}

	static int FreePort() {
		var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try { return ((IPEndPoint) listener.LocalEndpoint).Port; } finally { listener.Stop(); }
	}

	static BrowserOptions Options() => new() { Width = 640, Height = 480, FrameIntervalMs = 60, DataRoot = null, LaunchTimeout = TimeSpan.FromSeconds(30) };

	[Fact]
	public async Task Navigate_screenshot_and_history_round_trip() {
		if(BrowserSession.FindExecutable() is null) return; // no chromium on this machine: nothing to prove
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		await using var session = await BrowserSession.LaunchAsync("test-nav", Options() with { DataRoot = Home }, ct: timeout.Token);
		var home = await session.NavigateAsync(FixtureUrl, ct: timeout.Token);
		Assert.Equal("Fixture Home", home.Title);
		Assert.Contains("Hello Ainur", home.Text);
		Assert.Contains(home.Elements, e => e.Tag == "a" && e.Name.Contains("second page"));

		var frame = await session.ScreenshotAsync(ct: timeout.Token);
		Assert.True(frame.Png.Length > 1000, $"expected a real PNG, got {frame.Png.Length} bytes");
		Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, frame.Png.Take(4).ToArray());

		var clicked = await session.ClickAsync(index: home.Elements.First(e => e.Tag == "a").Index, ct: timeout.Token);
		Assert.Equal("Second Page", clicked.Title);

		var back = await session.BackAsync(timeout.Token);
		Assert.Equal("Fixture Home", back.Title);

		var typed = await session.TypeAsync("tulkas", selector: "#q", ct: timeout.Token);
		Assert.Equal("Fixture Home", typed.Title);
	}

	/// <summary>
	/// Regression for two real defects found while building the skeleton: (1) a frame pump overlapping a tool's
	/// CDP command starved it, because Chrome blocks its main thread while answering Page.captureScreenshot and
	/// the pump used to hold the socket; (2) a cancellation landing during an in-flight send blew the stack
	/// through semaphore/request cancellation ping-pong. Both are fixed by serialising round trips, skipping a
	/// pump tick while the connection is busy, and keeping the pump off cancellation tokens: this test fails on
	/// either regression by timing out instead of hanging the suite forever.
	/// </summary>
	[Fact]
	public async Task Frame_pump_never_starves_actions() {
		if(BrowserSession.FindExecutable() is null) return;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
		await using var session = await BrowserSession.LaunchAsync("test-pump", Options() with { DataRoot = Home, FrameIntervalMs = 40 }, ct: timeout.Token);
		var frames = 0;
		session.Frame += _ => Interlocked.Increment(ref frames);
		await session.NavigateAsync(FixtureUrl, ct: timeout.Token);

		var pumpDeadline = DateTime.UtcNow.AddSeconds(10);
		while(DateTime.UtcNow < pumpDeadline && Volatile.Read(ref frames) < 5) await Task.Delay(50);
		Assert.True(Volatile.Read(ref frames) >= 5, $"frame pump produced only {frames} frames while idle");

		// Every tool action must complete promptly even though the pump is capturing concurrently.
		for(var i = 0; i < 5; i++) {
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var observation = await session.NavigateAsync(FixtureUrl, TimeSpan.FromSeconds(20), timeout.Token);
			Assert.Equal("Fixture Home", observation.Title);
			Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"action {i} took {sw.Elapsed} with the pump running");
		}

		var frame = await session.ScreenshotAsync(ct: timeout.Token);
		Assert.Equal(0x89, frame.Png[0]);
	}

	[Fact]
	public async Task Manager_exposes_contract_shaped_session_and_frames() {
		if(BrowserSession.FindExecutable() is null) return;
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
		var tempHome = new TempHome();
		try {
			using var rt = tempHome.Runtime(new FakeProvider((_, _) => FakeProvider.Text("noop")), start: false);
			var manager = BrowserManager.For(rt);
			var events = new List<System.Text.Json.Nodes.JsonObject>();
			var gate = new object();
			manager.StreamEvent += e => { lock(gate) events.Add(e); };

			await using(var session = await manager.AcquireAsync("ses_contract", "agt_test", Options() with { DataRoot = manager.Root }, timeout.Token)) {
				await session.NavigateAsync(FixtureUrl, ct: timeout.Token);
				await session.ScreenshotAsync(ct: timeout.Token);
				await Wait.Until(() => { lock(gate) return events.Count(e => (string?) e["event"] == "frame") >= 1; }, TimeSpan.FromSeconds(20), "a frame event");

				Assert.Contains("ses_contract", manager.LiveIds);
				var snapshot = manager.SessionSnapshot("ses_contract");
				Assert.NotNull(snapshot);
				foreach(var key in new[] { "id", "agent_id", "state", "url", "title", "viewport", "started_at" }) Assert.True(snapshot!.ContainsKey(key), $"session snapshot missing {key}");
				Assert.Equal("agt_test", (string?) snapshot!["agent_id"]);
				Assert.Equal(640, (int) snapshot["viewport"]!["width"]!);

				var frame = manager.LatestFrameEvent("ses_contract");
				Assert.NotNull(frame);
				foreach(var key in new[] { "id", "agent_id", "seq", "data_url", "artifact", "width", "height", "url", "title", "captured_at" }) Assert.True(frame!.ContainsKey(key), $"frame payload missing {key}");
				Assert.StartsWith("data:image/png;base64,", (string?) frame!["data_url"]);
				// The bus is asynchronous: the pump captures independently of this test's own screenshots, so the
				// frame list legitimately contains earlier seqs. What the contract guarantees is that the stream is
				// monotonic and never ahead of the session's authoritative latest frame.
				await Wait.Until(() => { lock(gate) return events.Count(e => (string?) e["event"] == "frame") >= 3; }, TimeSpan.FromSeconds(20), "three frame events on the bus");
				var streamed = events.Where(e => (string?) e["event"] == "frame").Select(e => e["data"]!.AsObject()).ToList();
				var seqs = streamed.Select(f => f["seq"]!.GetValue<long>()).ToList();
				Assert.Equal(seqs.OrderBy(s => s), seqs);                       // monotonic
				var latestSeq = manager.LatestFrameEvent("ses_contract")!["seq"]!.GetValue<long>();
				Assert.True(seqs.Max() <= latestSeq, $"stream ran ahead of the session ({seqs.Max()} > {latestSeq})");
				Assert.All(streamed, f => { Assert.StartsWith("data:image/png;base64,", (string?) f["data_url"]); Assert.Equal("ses_contract", (string?) f["id"]); Assert.Equal("agt_test", (string?) f["agent_id"]); });
			}

			await Wait.Until(() => !manager.LiveIds.Contains("ses_contract"), TimeSpan.FromSeconds(20), "the closed session to leave the index");
			Assert.Contains(events, e => (string?) e["event"] == "closed");
		} finally {
			tempHome.Dispose();
		}
	}
}
