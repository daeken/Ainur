using System.Net;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Accounting;
using Ainur.Core.Browser;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;

namespace Ainur.Tests;

public sealed class MultimodalTests {
	[Fact]
	[Trait("Category", "Browser")]
	public async Task ActualHostLoopExecutesBrowserScreenshotAndDispatchesUserAndToolPixels() {
		if(BrowserSession.FindExecutable() is null) return; // Chrome-specific local proof; no model/network calls.
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
		using var home = new TempHome(); var handler = new ScriptedHandler(); var requests = 0;
		handler.Add(_ => true, _ => Interlocked.Increment(ref requests) == 1 ? ScriptedHandler.Sse("""
			data: {"type":"response.output_item.done","output_index":0,"item":{"type":"function_call","id":"fc_local","call_id":"call_local","name":"browser_screenshot","arguments":"{}"}}

			data: {"type":"response.completed","response":{"usage":{"input_tokens":50,"output_tokens":10}}}

			""") : Success());
		using var rt = Runtime(home, handler); var (p, host) = Project(rt, home);
		rt.Db.Write(u => u.Execute("UPDATE agents SET reasoning_effort='high' WHERE id=@id", new { id = host.AgentId }));
		host.Cache.Load(["browser_screenshot"], 14000);
		var manager = BrowserManager.For(rt);
		await using var browser = await manager.AcquireAsync(host.SessionId, host.AgentId, new BrowserOptions { Width = 640, Height = 480 }, timeout.Token);
		await browser.NavigateAsync("data:text/html,<canvas id=c width=640 height=480></canvas><script>var x=c.getContext('2d');x.fillStyle='blue';x.fillRect(20,20,150,100)</script>", ct: timeout.Token);
		var image = rt.UploadConversationImage(p.Id, ConversationImageTests.Png(), "image/png");
		rt.PostUserMessage(p.Id, "", [image.Id], "actual-host-loop");
		rt.Draining = false; host.Wake();
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Author == "manager" && c.Body == "offline answer"), TimeSpan.FromSeconds(60), "scripted host to finish after browser screenshot");
		Assert.Equal(2, handler.Seen.Count);
		var result = JsonUtil.Deserialize<ToolResultPayload>(Assert.Single(rt.Store.Items(host.SessionId), i => i.Kind == ItemKinds.ToolResult).Payload)!;
		Assert.False(result.IsError, result.Text); var screenshot = Assert.Single(result.Images);
		Assert.Equal((640, 480), ConversationImageValidation.Validate(rt.Artifacts.Get(screenshot.Artifact), screenshot.MimeType));
		var first = JsonNode.Parse(handler.Seen[0].Body)!; var second = JsonNode.Parse(handler.Seen[1].Body)!;
		Assert.Equal(1, first["input"]!.AsArray().Sum(n => n?["content"] is JsonArray a ? a.Count(part => (string?) part?["type"] == "input_image") : 0));
		Assert.Equal(2, second["input"]!.AsArray().Sum(n => n?["content"] is JsonArray a ? a.Count(part => (string?) part?["type"] == "input_image") : 0));
		Assert.Contains(second["input"]!.AsArray().Where(n => n?["content"] is JsonArray).SelectMany(n => n!["content"]!.AsArray()), part => (string?) part?["image_url"] == "data:image/png;base64," + Convert.ToBase64String(rt.Artifacts.Get(screenshot.Artifact)));
		Assert.Contains("UNTRUSTED TOOL OUTPUT", handler.Seen[1].Body);
		Assert.All(handler.Seen, s => { Assert.Equal(OpenAiResponsesProvider.SubscriptionEndpoint, s.Uri); Assert.Equal("high", (string?) JsonNode.Parse(s.Body)!["reasoning"]!["effort"]); });
		Assert.All(rt.Store.ModelRequestsInState("succeeded"), r => Assert.DoesNotContain("data:image/png;base64,", rt.Artifacts.GetText(r.RequestArtifact!)));
		Assert.Equal(0, rt.Ledger.Summary(p.Id).CashKnownNanos);
	}
	static string Auth(TempHome home) {
		var path = Path.Combine(home.Path, "synthetic-auth.json");
		File.WriteAllText(path, JsonUtil.Serialize(new { tokens = new { access_token = "synthetic-offline-access", refresh_token = "synthetic-offline-refresh", account_id = "offline-account", expires_at = Clock.Now + 3_600_000 } }));
		return path;
	}
	static HttpResponseMessage Success() => ScriptedHandler.Sse("data: {\"type\":\"response.output_text.delta\",\"delta\":\"offline answer\"}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":50,\"output_tokens\":2}}}\n\n");
	static AinurRuntime Runtime(TempHome home, ScriptedHandler handler) => home.Runtime(new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => throw new InvalidOperationException("API key must never be read on subscription route"), codexAuthPath: () => Auth(home), routeOverride: "auto"), configure: o => o.AutoStartHosts = false, start: false);
	static (Project Project, SessionHost Host) Project(AinurRuntime rt, TempHome home) {
		rt.Draining = true; // Suppress automatic wake; exercise delivery and gateway explicitly with scripted HTTP.
		var p = rt.CreateProject("images offline", "", home.Workspace, managerModelId: "gpt-6-astra", noEffectiveLimit: true, cashCeilingDollars: 50);
		return (p, rt.GetHost(rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!)!);
	}
	static ModelCall Call(AinurRuntime rt, Project p, SessionHost host, List<ChatMessage>? messages = null, ModelInfo? model = null) => new() {
		ProjectId = p.Id, AgentId = host.AgentId, SessionId = host.SessionId, Model = model ?? rt.Store.GetModel("gpt-6-astra")!,
		Messages = messages ?? rt.BuildContext(host, rt.PolicyFor(host.Agent, host.Session)).Messages, Purpose = "offline-image-test", Category = "direct", ReasoningEffort = "high",
	};
	static ConversationImage Send(AinurRuntime rt, Project p, SessionHost host, int width = 2) {
		var image = rt.UploadConversationImage(p.Id, ConversationImageTests.Png(width, 2), "image/png");
		rt.PostUserMessage(p.Id, "", [image.Id], Guid.NewGuid().ToString()); host.DeliverInbox(); return image;
	}
	static ToolImage Tool(AinurRuntime rt, SessionHost host, int width = 4) {
		var bytes = ConversationImageTests.Png(width, 2); var image = new ToolImage(rt.Artifacts.Put(bytes), "image/png", width, 2);
		var call = new ToolCall(Ids.New("call"), "browser_screenshot", "{}");
		rt.Db.Write(u => rt.Store.AppendItem(u, host.SessionId, ItemKinds.Assistant, new AssistantPayload { ToolCalls = [call] }, 20));
		host.RecordResult(call, new ToolInvocation { Id = Ids.New("inv") }, null, new ToolResult { Text = "UNTRUSTED page text", Images = [image] });
		return image;
	}

	[Fact]
	public async Task NormalInboxAndRecordedToolResultReachWireWithDistinctProvenanceAndRedactedPersistence() {
		using var home = new TempHome(); var handler = new ScriptedHandler(); handler.Add(_ => true, _ => Success());
		using var rt = Runtime(home, handler); var (p, host) = Project(rt, home);
		var user = Send(rt, p, host); var screenshot = Tool(rt, host);
		var built = rt.BuildContext(host, rt.PolicyFor(host.Agent, host.Session));
		Assert.Equal(2, built.Messages.Sum(m => m.Images.Count));
		Assert.Equal("", Assert.Single(built.Messages, m => m.Role == "user" && m.Images.Count > 0).Content);
		var result = await rt.Gateway.CallAsync(Call(rt, p, host), default);
		var seen = Assert.Single(handler.Seen); Assert.Equal(OpenAiResponsesProvider.SubscriptionEndpoint, seen.Uri);
		var input = JsonNode.Parse(seen.Body)!["input"]!.AsArray();
		var uploadMessage = Assert.Single(input, n => n?["type"]?.GetValue<string>() == "message" && n["role"]?.GetValue<string>() == "user" && n["content"]!.AsArray().Any(part => part?["image_url"]?.GetValue<string>() == "data:image/png;base64," + Convert.ToBase64String(rt.Artifacts.Get(user.Artifact))));
		Assert.Equal("", uploadMessage!["content"]![0]!["text"]!.GetValue<string>());
		var toolIndex = input.ToList().FindIndex(n => n?["type"]?.GetValue<string>() == "function_call_output");
		Assert.True(toolIndex >= 0); Assert.Equal("UNTRUSTED page text", input[toolIndex]!["output"]!.GetValue<string>());
		Assert.Contains("UNTRUSTED TOOL OUTPUT", input[toolIndex + 1]!["content"]![0]!["text"]!.GetValue<string>());
		Assert.Equal("data:image/png;base64," + Convert.ToBase64String(rt.Artifacts.Get(screenshot.Artifact)), input[toolIndex + 1]!["content"]![1]!["image_url"]!.GetValue<string>());
		Assert.Equal("high", JsonNode.Parse(seen.Body)!["reasoning"]!["effort"]!.GetValue<string>());
		Assert.DoesNotContain("data:image/png;base64,", result.Response.RawRequest);
		foreach(var image in new[] { user.Artifact, screenshot.Artifact }) {
			var encoded = Convert.ToBase64String(rt.Artifacts.Get(image));
			Assert.DoesNotContain(encoded, result.Response.RawRequest);
			Assert.DoesNotContain(encoded, JsonUtil.Serialize(rt.Store.Items(host.SessionId)));
			Assert.DoesNotContain(encoded, JsonUtil.Serialize(rt.Store.Events(p.Id)));
		}
		var completed = Assert.Single(rt.Store.ModelRequestsInState("succeeded"));
		Assert.DoesNotContain("data:image/png;base64,", rt.Artifacts.GetText(completed.RequestArtifact!));
		Assert.Equal(0, rt.Ledger.Summary(p.Id).CashKnownNanos);
	}

	[Fact]
	public async Task DurableReplayProjectionCompactionAndNewUploadsNeverWedgeOrSilentlyDropHistory() {
		using var home = new TempHome(); string projectId, sessionId, latestArtifact;
		using(var rt = home.Runtime(configure: o => o.AutoStartHosts = false, start: false)) {
			var (p, host) = Project(rt, home); projectId = p.Id; sessionId = host.SessionId;
			for(var i = 2; i < 8; i++) Send(rt, p, host, i);
			latestArtifact = rt.Store.Conversation(p.Id).Last().Attachments.Single().Artifact;
			Tool(rt, host, 9);
			var built = rt.BuildContext(rt.GetHost(sessionId)!, rt.PolicyFor(rt.GetHost(sessionId)!.Agent, rt.GetHost(sessionId)!.Session)); Assert.Equal(4, built.Messages.Sum(m => m.Images.Count));
			Assert.Contains(built.Messages, m => m.Content?.Contains("Older user image pixels omitted") == true);
			Assert.Contains(built.Messages.SelectMany(m => m.Images), i => i.Artifact == latestArtifact);
			BrowserImageInput.EnsureAuthorized(rt.Store, p.Id, sessionId, built.Messages);
			Assert.Equal(4, BrowserImageInput.Hydrate(rt.Artifacts, built.Messages.SelectMany(m => m.Images).ToList()).Count);
			Assert.Contains("pixels not included", ContextBuilder.RenderTranscript(rt.Store.Items(sessionId)));
			var fake = new FakeProvider((_, _) => FakeProvider.Text("Text compaction: user images exist; do not infer their contents."), "openai"); rt.Providers.Register(fake);
			await rt.Compactor.CompactAsync(host.Session, host.Agent, rt.CurrentView(sessionId), CompactionModes.Full, rt.PolicyFor(host.Agent, host.Session), default);
			var compacted = rt.BuildContext(rt.GetHost(sessionId)!, rt.PolicyFor(rt.GetHost(sessionId)!.Agent, rt.GetHost(sessionId)!.Session));
			Assert.Equal(latestArtifact, Assert.Single(compacted.Messages.SelectMany(m => m.Images)).Artifact);
			Assert.Contains(compacted.Messages, m => m.Content?.Contains("retained across text compaction") == true);
			Assert.All(fake.Requests, r => Assert.Empty(r.ImageData));
			BrowserImageInput.EnsureAuthorized(rt.Store, p.Id, sessionId, compacted.Messages);
		}
		using(var rt = home.Runtime(configure: o => o.AutoStartHosts = false, start: false)) {
			var messages = rt.BuildContext(rt.GetHost(sessionId)!, rt.PolicyFor(rt.GetHost(sessionId)!.Agent, rt.GetHost(sessionId)!.Session)).Messages;
			Assert.Equal(latestArtifact, Assert.Single(messages.SelectMany(m => m.Images)).Artifact);
			BrowserImageInput.EnsureAuthorized(rt.Store, projectId, sessionId, messages);
			Assert.Single(BrowserImageInput.Hydrate(rt.Artifacts, messages.SelectMany(m => m.Images).ToList()));
		}
	}

	[Fact]
	public async Task TamperedSourceRoleProjectUnboundUploadMissingAndMalformedArtifactsFailBeforeAdmission() {
		using var home = new TempHome(); var handler = new ScriptedHandler(); handler.Add(_ => true, _ => Success());
		using var rt = Runtime(home, handler); var (p, host) = Project(rt, home); var image = Send(rt, p, host);
		var legitimate = Assert.Single(rt.BuildContext(host, rt.PolicyFor(host.Agent, host.Session)).Messages, m => m.Images.Count > 0);
		var invalid = new ChatMessage { Role = "user", Content = "", Images = legitimate.Images.ToList() };
		await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(rt, p, host, [invalid]), default));
		invalid.SourceItemId = legitimate.SourceItemId; invalid.Role = "tool"; invalid.ToolCallId = "forged";
		await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(rt, p, host, [invalid]), default));
		var other = rt.CreateProject("foreign", "", home.Workspace, managerModelId: "gpt-6-astra");
		Assert.Throws<ProviderException>(() => BrowserImageInput.EnsureAuthorized(rt.Store, other.Id, host.SessionId, [legitimate]));
		var draft = rt.UploadConversationImage(p.Id, ConversationImageTests.Png(3), "image/png");
		var forged = rt.Db.Write(u => rt.Store.AppendItem(u, host.SessionId, ItemKinds.User, new UserPayload { Images = [new(draft.Artifact, "image/png", 3, 2)] }, 10));
		invalid = new ChatMessage { Role = "user", SourceItemId = forged.Id, Images = [new(draft.Artifact, "image/png", 3, 2)] };
		await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(rt, p, host, [invalid]), default));
		Assert.Throws<ProviderException>(() => BrowserImageInput.Hydrate(rt.Artifacts, Enumerable.Repeat(legitimate.Images[0], 5).ToList()));
		File.Delete(Path.Combine(rt.Artifacts.Root, image.Artifact[7..9], image.Artifact[9..]));
		await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(rt, p, host, [legitimate]), default));
		var malformed = new ToolImage(rt.Artifacts.Put(new byte[100]), "image/png", 2, 2);
		Assert.Throws<ProviderException>(() => BrowserImageInput.Hydrate(rt.Artifacts, [malformed]));
		Assert.Empty(handler.Seen); Assert.Empty(rt.Store.ModelRequestsInState("dispatched")); Assert.Empty(rt.Store.CostEvents(p.Id));
		Assert.Equal(0, rt.Ledger.Summary(p.Id).ReservedEffectiveNanos);
	}

	[Fact]
	public async Task UnsupportedDirectAndFallbackNeverDispatchTextOnlyAndCashUnknownApiFallbackRemainsBlocked() {
		using var home = new TempHome(); var handler = new ScriptedHandler();
		handler.Add(_ => true, _ => ScriptedHandler.Json("{\"error\":\"quota\"}", HttpStatusCode.TooManyRequests));
		using var rt = Runtime(home, handler); var (p, host) = Project(rt, home); Send(rt, p, host);
		var unsupported = rt.Store.GetModel("deepseek-v4.1-flash")!;
		await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(rt, p, host, model: unsupported), default)); Assert.Empty(handler.Seen);
		await Assert.ThrowsAsync<BudgetExhaustedException>(() => rt.Gateway.CallAsync(Call(rt, p, host), default));
		Assert.Equal(OpenAiResponsesProvider.SubscriptionEndpoint, Assert.Single(handler.Seen).Uri);
		Assert.Single(rt.Store.ModelRequestsInState("failed")); Assert.Empty(rt.Store.CostEvents(p.Id));
		rt.Providers.Register(new FakeProvider((_, _) => throw new InvalidOperationException("Unsupported fallback dispatched")));
		rt.Db.Write(u => u.Execute("UPDATE models SET fallback_model_id=@id WHERE id='gpt-6-astra'", new { id = unsupported.Id }));
		var error = await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(rt, p, host), default));
		Assert.Contains("requires an OpenAI Responses", error.Message); Assert.Single(handler.Seen);
		Assert.Single(rt.Store.ModelRequestsInState("failed")); Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
		Assert.Equal(0, rt.Ledger.Summary(p.Id).ReservedCashNanos);
	}

	[Fact]
	public async Task ProviderRejectsUnhydratedImagesAndRedactsEchoedErrorBeforeGatewayJournal() {
		using var home = new TempHome(); var handler = new ScriptedHandler();
		var png = ConversationImageTests.Png(); var encoded = Convert.ToBase64String(png);
		handler.Add(_ => true, _ => ScriptedHandler.Json(JsonUtil.Serialize(new { error = "echo data:image/png;base64," + encoded }), HttpStatusCode.BadRequest));
		using var rt = Runtime(home, handler); var (p, host) = Project(rt, home); Send(rt, p, host);
		var error = await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(Call(rt, p, host), default));
		Assert.DoesNotContain(encoded, error.Message); Assert.DoesNotContain(encoded, JsonUtil.Serialize(rt.Store.Events(p.Id)));
		Assert.DoesNotContain(encoded, Assert.Single(rt.Store.ModelRequestsInState("failed")).Error!);
		var messages = rt.BuildContext(host, rt.PolicyFor(host.Agent, host.Session)).Messages;
		var provider = new OpenAiResponsesProvider(new HttpClient(handler));
		Assert.Throws<ProviderException>(() => provider.BuildRequest(new ProviderRequest { Model = rt.Store.GetModel("gpt-6-astra")!, Messages = messages }));
		var unsupported = new ChatCompletionsProvider("offline", new HttpClient(handler), () => "fake", "http://invalid.local", effort => effort ?? "high");
		Assert.Throws<ProviderException>(() => unsupported.BuildRequest(new ProviderRequest { Model = rt.Store.GetModel("gpt-6-astra")!, Messages = messages }));
		Assert.Single(handler.Seen);
	}
}
