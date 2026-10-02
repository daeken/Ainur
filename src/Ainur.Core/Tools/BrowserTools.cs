using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core.Browser;

namespace Ainur.Core.Tools;

/// <summary>
/// Browser tools. Each call drives one headless Chromium owned by the calling Ainur session (never the user's
/// browser profile) and returns a text observation plus a screenshot artifact reference.
/// </summary>
public abstract class BrowserTool : BuiltinTool {
	public override TimeSpan Timeout => TimeSpan.FromMinutes(3);
	public override IReadOnlyList<string> Tags => ["browser", "web", "chrome", "computer", "screenshot", "navigate", "ui", "page"];

	protected static async Task<BrowserSession> SessionAsync(ToolContext ctx) {
		var manager = BrowserManager.For(ctx.Runtime);
		return await manager.AcquireAsync(ctx.Session.Id, ctx.Agent.Id, null, ctx.CancellationToken).ConfigureAwait(false);
	}

	/// <summary>Stores the frame in the artifact store so it is recorded like any other tool result.</summary>
	protected static string Publish(ToolContext ctx, BrowserFrame frame) {
		if(Runtime.ConversationImageValidation.Validate(frame.Png, "image/png") != (frame.Width, frame.Height))
			throw new InvalidOperationException("Screenshot dimensions differ from decoded image.");
		return ctx.Runtime.Artifacts.Put(frame.Png);
	}

	protected static async Task<ToolResult> Observed(ToolContext ctx, BrowserSession session, BrowserObservation observation, string prefix = "") {
		var frame = await session.ScreenshotAsync(ct: ctx.CancellationToken).ConfigureAwait(false);
		var reference = Publish(ctx, frame);
		var text = new StringBuilder();
		if(prefix.Length > 0) text.AppendLine(prefix);
		text.Append(BrowserSession.FormatObservation(observation));
		text.AppendLine().AppendLine($"screenshot artifact: {reference} ({frame.Width}x{frame.Height} png)");
		return new ToolResult {
			Text = text.ToString().TrimEnd(), Value = new { observation.Url, observation.Title, Artifact = reference, Elements = observation.Elements },
			Description = $"browser: {observation.Title} {observation.Url}", Images = [new ToolImage(reference, "image/png", frame.Width, frame.Height)],
		};
	}
}

public sealed class BrowserNavigateTool : BrowserTool {
	public override string Name => "browser_navigate";
	public override string Description => """
		Open a URL in your own headless Chromium (isolated profile: it never touches the user's browser, cookies, or logins).
		Waits for load, then returns the page title, URL, visible text snippet, and an indexed list of interactive elements
		you can click by index. A screenshot is recorded as a tool artifact.
		""";
	public override JsonObject InputSchema => Schema.Object(
		("url", Schema.String("Absolute URL to open, e.g. https://example.com."), true),
		("timeout_seconds", Schema.Integer("Navigation timeout in seconds (default 30)."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var url = Str(args, "url");
		var timeout = TimeSpan.FromSeconds(Math.Clamp(OptInt(args, "timeout_seconds") ?? 30, 1, 120));
		var session = await SessionAsync(ctx);
		var observation = await session.NavigateAsync(url, timeout, ctx.CancellationToken);
		return await Observed(ctx, session, observation, $"navigated to {url}");
	}
}

public sealed class BrowserReadTool : BrowserTool {
	public override string Name => "browser_read";
	public override string Description => """
		Read the current page without acting: "text" (visible text), "ax" (accessibility tree), "links" (all links with targets),
		or "interactive" (indexed clickable/typable elements). Cheaper than a screenshot for understanding a page.
		""";
	public override JsonObject InputSchema => Schema.Object(
		("mode", Schema.String("What to read.", "text", "ax", "links", "interactive"), false),
		("max_chars", Schema.Integer("Maximum characters to return (default 8000)."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var mode = (OptStr(args, "mode") ?? "text").ToLowerInvariant();
		var max = Math.Clamp(OptInt(args, "max_chars") ?? 8000, 200, 100_000);
		var session = await SessionAsync(ctx);
		var text = await session.ReadAsync(mode, max, ctx.CancellationToken);
		return ToolResult.Ok(text, null, $"browser_read({mode}) {session.LatestFrame?.Url}");
	}
}

public sealed class BrowserClickTool : BrowserTool {
	public override string Name => "browser_click";
	public override string Description => """
		Click on the page. Target one of: "selector" (CSS selector), "index" (the [n] from browser_navigate/browser_read
		interactive elements), or "x"/"y" viewport pixels. Returns the page after the click plus a screenshot artifact.
		""";
	public override JsonObject InputSchema => Schema.Object(
		("selector", Schema.String("CSS selector to click."), false),
		("index", Schema.Integer("Interactive element index from a previous observation."), false),
		("x", Schema.Integer("Viewport x coordinate."), false),
		("y", Schema.Integer("Viewport y coordinate."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var session = await SessionAsync(ctx);
		var observation = await session.ClickAsync(OptStr(args, "selector"), OptInt(args, "index"),
			(double?) OptInt(args, "x"), (double?) OptInt(args, "y"), ctx.CancellationToken);
		return await Observed(ctx, session, observation, "clicked");
	}
}

public sealed class BrowserTypeTool : BrowserTool {
	public override string Name => "browser_type";
	public override string Description => """
		Type text into the page. Provide "selector" or "index" to focus an element first (otherwise the focused element receives
		the text). Set "submit" true to press Enter afterwards, e.g. for a search box.
		""";
	public override JsonObject InputSchema => Schema.Object(
		("text", Schema.String("Text to type."), true),
		("selector", Schema.String("CSS selector of the input to focus."), false),
		("index", Schema.Integer("Interactive element index of the input to focus."), false),
		("submit", Schema.Boolean("Press Enter after typing (default false)."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var text = Str(args, "text");
		var session = await SessionAsync(ctx);
		var observation = await session.TypeAsync(text, OptStr(args, "selector"), OptInt(args, "index"), OptBool(args, "submit") ?? false, ctx.CancellationToken);
		return await Observed(ctx, session, observation, $"typed {text.Length} chars");
	}
}

public sealed class BrowserWaitTool : BrowserTool {
	public override string Name => "browser_wait";
	public override string Description => """
		Wait for a fixed time, or for a CSS selector to appear (whichever is given), then return the page state.
		Use after an action that triggers slow loading.
		""";
	public override JsonObject InputSchema => Schema.Object(
		("milliseconds", Schema.Integer("Milliseconds to wait (default 1000 when no selector is given)."), false),
		("selector", Schema.String("Wait until this CSS selector matches an element."), false),
		("timeout_seconds", Schema.Integer("Maximum wait in seconds (default 30)."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var session = await SessionAsync(ctx);
		var observation = await session.WaitAsync(OptInt(args, "milliseconds") ?? 1000, OptStr(args, "selector"),
			TimeSpan.FromSeconds(Math.Clamp(OptInt(args, "timeout_seconds") ?? 30, 1, 300)), ctx.CancellationToken);
		return await Observed(ctx, session, observation, "waited");
	}
}

public sealed class BrowserBackTool : BrowserTool {
	public override string Name => "browser_back";
	public override string Description => "Go back one entry in the page history and return the resulting page state.";
	public override JsonObject InputSchema => Schema.Object();

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var session = await SessionAsync(ctx);
		var observation = await session.BackAsync(ctx.CancellationToken);
		return await Observed(ctx, session, observation, "went back");
	}
}

public sealed class BrowserScreenshotTool : BrowserTool {
	public override string Name => "browser_screenshot";
	public override string Description => """
		Capture the current viewport as a PNG artifact and return its reference. Also returns the URL, title and visible text.
		Use it to look at visual state, or to hand an image reference to another tool.
		""";
	public override JsonObject InputSchema => Schema.Object();

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var session = await SessionAsync(ctx);
		var frame = await session.ScreenshotAsync(ct: ctx.CancellationToken);
		var reference = Publish(ctx, frame);
		var observation = await session.ObserveAsync(reference, ctx.CancellationToken);
		var text = new StringBuilder()
			.AppendLine($"screenshot artifact: {reference} ({frame.Width}x{frame.Height} png)")
			.Append(BrowserSession.FormatObservation(observation));
		return new ToolResult {
			Text = text.ToString().TrimEnd(), Value = new { observation.Url, observation.Title, Artifact = reference },
			Description = $"browser screenshot {observation.Title}", Images = [new ToolImage(reference, "image/png", frame.Width, frame.Height)],
		};
	}
}

/// <summary>
/// The "computer use" loop for any model: one screenshot + one action per call. Native computer-use tools are not
/// accepted by the subscription endpoint we drive, so this function tool is the portable protocol.
/// </summary>
public sealed class ComputerTool : BrowserTool {
	public override string Name => "computer";
	public override string Description => """
		Drive the screen one action at a time, like a human at a computer. Each call returns the state after the action
		(URL, title, visible text, indexed interactive elements) and records a PNG screenshot artifact. Actions:
		"screenshot" (observe only), "click" (needs x and y in viewport pixels, or index), "type" (needs text; optional index/x/y
		to focus first, optional submit), "key" (needs key, e.g. Enter/Tab/Escape/ArrowDown), "scroll" (needs delta_y),
		"navigate" (needs url), "wait" (optional milliseconds). Coordinates are viewport pixels at 1280x800 unless changed.
		""";
	public override JsonObject InputSchema => Schema.Object(
		("action", Schema.String("Action to perform.", "screenshot", "click", "type", "key", "scroll", "navigate", "wait"), true),
		("x", Schema.Integer("Viewport x for action=click."), false),
		("y", Schema.Integer("Viewport y for action=click."), false),
		("index", Schema.Integer("Interactive element index for action=click or action=type focus."), false),
		("text", Schema.String("Text for action=type."), false),
		("key", Schema.String("Key name for action=key."), false),
		("delta_y", Schema.Integer("Scroll delta in pixels for action=scroll."), false),
		("url", Schema.String("URL for action=navigate."), false),
		("milliseconds", Schema.Integer("Wait time for action=wait."), false),
		("submit", Schema.Boolean("Press Enter after typing."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var action = Str(args, "action").ToLowerInvariant();
		var session = await SessionAsync(ctx);
		var ct = ctx.CancellationToken;
		BrowserObservation observation;
		switch(action) {
			case "screenshot":
				observation = await session.ObserveAsync(ct: ct);
				break;
			case "click":
				observation = await session.ClickAsync(index: OptInt(args, "index"), x: (double?) OptInt(args, "x"), y: (double?) OptInt(args, "y"), ct: ct);
				break;
			case "type":
				observation = await session.TypeAsync(Str(args, "text"), index: OptInt(args, "index"), submit: OptBool(args, "submit") ?? false, ct: ct);
				break;
			case "key":
				observation = await session.KeyAsync(Str(args, "key"), ct);
				break;
			case "scroll":
				observation = await session.ScrollAsync(OptInt(args, "delta_y") ?? 400, ct);
				break;
			case "navigate":
				observation = await session.NavigateAsync(Str(args, "url"), ct: ct);
				break;
			case "wait":
				observation = await session.WaitAsync(OptInt(args, "milliseconds") ?? 1000, ct: ct);
				break;
			default:
				throw new ToolException($"Unknown computer action '{action}'. Use screenshot, click, type, key, scroll, navigate, or wait.");
		}
		return await Observed(ctx, session, observation, $"computer action: {action}");
	}
}
