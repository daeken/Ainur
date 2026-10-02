using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Ainur.Core;
using Ainur.Core.Accounting;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;

namespace Ainur.Tests;

public sealed class ImageBoundaryTests {
	// Regression supplied independently by Námo against frozen 478b46e (both cases failed).
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task EscapedDiagnosticEchoNeverPersistsToRequestErrorJournalOrResponseArtifact(bool successfulStream) {
		using var home = new TempHome(); var handler = new ScriptedHandler();
		var png = ConversationImageTests.Png(); var encoded = Convert.ToBase64String(png);
		var escaped = "data\\u003aimage/png;base64," + encoded;
		handler.Add(_ => true, _ => successfulStream
			? ScriptedHandler.Sse("data: {\"type\":\"response.created\",\"response\":{\"model\":\"" + escaped + "\"}}\n\ndata: {\"type\":\"response.completed\",\"response\":{\"debug_echo\":\"" + escaped + "\",\"usage\":{\"input_tokens\":2,\"output_tokens\":1}}}\n\n")
			: ScriptedHandler.Json("{\"error\":\"" + escaped + "\"}", HttpStatusCode.BadRequest));
		var auth = Path.Combine(home.Path, "synthetic-auth.json");
		File.WriteAllText(auth, JsonUtil.Serialize(new { tokens = new { access_token = "offline-only", account_id = "offline" } }));
		using var rt = home.Runtime(new OpenAiResponsesProvider(new HttpClient(handler), apiKey: () => throw new InvalidOperationException("No paid access"), codexAuthPath: () => auth, routeOverride: "auto"), configure: o => o.AutoStartHosts = false, start: false);
		rt.Draining = true;
		var project = rt.CreateProject("offline", "", home.Workspace, managerModelId: "gpt-6-astra", noEffectiveLimit: true, cashCeilingDollars: 50);
		var host = rt.GetHost(rt.Store.GetAgent(project.RootAgentId!)!.PrimarySessionId!)!;
		var image = rt.UploadConversationImage(project.Id, png, "image/png");
		rt.PostUserMessage(project.Id, "", [image.Id], "diagnostic-regression"); host.DeliverInbox();
		var call = new ModelCall { ProjectId = project.Id, AgentId = host.AgentId, SessionId = host.SessionId, Model = rt.Store.GetModel("gpt-6-astra")!, Purpose = "offline-test", Category = "direct", ReasoningEffort = "high", Messages = rt.BuildContext(host, rt.PolicyFor(host.Agent, host.Session)).Messages };
		if(successfulStream) {
			var result = await rt.Gateway.CallAsync(call, default);
			Assert.Equal("[response diagnostics omitted for image request]", rt.Artifacts.GetText(result.Record.ResponseArtifact!));
			Assert.Equal("[request diagnostics omitted for image request]", rt.Artifacts.GetText(Assert.Single(rt.Store.ModelRequestsInState("succeeded")).RequestArtifact!));
			Assert.Null(result.Response.UpstreamModel);
			Assert.Equal(2, result.Response.Usage.InputTokens); Assert.Equal(1, result.Response.Usage.OutputTokens);
		} else {
			await Assert.ThrowsAsync<ProviderException>(() => rt.Gateway.CallAsync(call, default));
			Assert.Equal("openai HTTP 400: [provider diagnostics omitted for image request]", Assert.Single(rt.Store.ModelRequestsInState("failed")).Error);
		}
		Assert.DoesNotContain(encoded, JsonUtil.Serialize(rt.Store.Events(project.Id)));
		Assert.Equal(0, rt.Ledger.Summary(project.Id).CashKnownNanos);
	}
	static byte[] Chunk(string type, byte[] data) {
		var result = new byte[data.Length + 12];
		BinaryPrimitives.WriteInt32BigEndian(result, data.Length);
		Encoding.ASCII.GetBytes(type).CopyTo(result, 4); data.CopyTo(result, 8);
		uint crc = 0xffffffff;
		foreach(var b in result.AsSpan(4, data.Length + 4)) {
			crc ^= b;
			for(var i = 0; i < 8; i++) crc = (crc >> 1) ^ (0xedb88320u & (uint) -(int) (crc & 1));
		}
		BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(result.Length - 4), ~crc); return result;
	}
	static byte[] Compressed(byte[] bytes, CompressionLevel level = CompressionLevel.Optimal) {
		using var result = new MemoryStream();
		using(var zlib = new ZLibStream(result, level, true)) zlib.Write(bytes);
		return result.ToArray();
	}
	static byte[] Image(byte[] compressed, int width = 2, int height = 2, int split = -1) {
		var ihdr = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(ihdr, width); BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
		ihdr[8] = 8; ihdr[9] = 6; // RGBA8, no interlace
		using var result = new MemoryStream(); result.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }); result.Write(Chunk("IHDR", ihdr));
		if(split < 0) result.Write(Chunk("IDAT", compressed));
		else { result.Write(Chunk("IDAT", compressed[..split])); result.Write(Chunk("IDAT", compressed[split..])); }
		result.Write(Chunk("IEND", [])); return result.ToArray();
	}

	[Fact]
	public void ZlibRequiresChecksumEndAndFullConsumptionAcrossEveryIdatSplit() {
		var compressed = Compressed(new byte[18]); // 2 RGBA8 rows, including filter bytes
		for(var split = 0; split <= compressed.Length; split++) {
			Assert.Equal((2, 2), ConversationImageValidation.Validate(Image(compressed, split: split), "image/png"));
			Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(Image([.. compressed, 42, 43], split: split), "image/png"));
			Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(Image([.. compressed, .. compressed], split: split), "image/png"));
		}
		for(var cut = 1; cut < compressed.Length; cut++)
			Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(Image(compressed[..^cut]), "image/png"));
		var corrupt = compressed.ToArray(); corrupt[^1] ^= 1;
		Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(Image(corrupt), "image/png"));
		Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(Image(Compressed(new byte[500_000])), "image/png"));
	}

	[Fact]
	public void NearTwoMiBValidInputAndMaximumPixelsHaveBoundedValidationWork() {
		const int width = 1024, height = 511;
		var pixels = new byte[(width * 4 + 1) * height]; new Random(471).NextBytes(pixels);
		for(var row = 0; row < height; row++) pixels[row * (width * 4 + 1)] = 0;
		var image = Image(Compressed(pixels, CompressionLevel.NoCompression), width, height);
		Assert.InRange(image.Length, 2_090_000, ConversationImageValidation.MaxBytes);
		var watch = Stopwatch.StartNew();
		for(var i = 0; i < 3; i++) Assert.Equal((width, height), ConversationImageValidation.Validate(image, "image/png"));
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Three near-limit validations exceeded the bounded test deadline.");
		var maxPixels = Image(Compressed(new byte[(2000 * 4 + 1) * 1500]), 2000, 1500);
		Assert.Equal((2000, 1500), ConversationImageValidation.Validate(maxPixels, "image/png"));
	}

	static ProviderRequest Request() {
		var bytes = ConversationImageTests.Png();
		return new ProviderRequest { Model = ModelCatalog.Seed.Single(m => m.Id == "gpt-6-astra-api"),
			Messages = [new ChatMessage { Role = "user", Images = [new ToolImage("test-image", "image/png", 2, 2)] }],
			ImageData = new Dictionary<string, byte[]> { ["test-image"] = bytes }, ReasoningEffort = "high" };
	}
	static OpenAiResponsesProvider Provider(ScriptedHandler handler) => new(new HttpClient(handler), apiKey: () => "synthetic", codexAuthPath: () => null, routeOverride: "auto");

	[Theory]
	[InlineData("escaped")]
	[InlineData("split")]
	[InlineData("malformed")]
	public async Task ImageDiagnosticsOmittedWithoutDependingOnEncoding(string variant) {
		var request = Request(); var encoded = Convert.ToBase64String(request.ImageData["test-image"]);
		// Regression of Námo's independently reproduced JSON-escaped-colon disclosure; also no URI and invalid JSON.
		var echo = variant switch {
			"escaped" => "{\"echo\":\"data\\u003aimage/png;base64," + encoded + "\"}",
			"split" => "{\"first\":\"" + encoded[..20] + "\",\"second\":\"" + encoded[20..] + "\"}",
			_ => "invalid diagnostic=" + encoded,
		};
		var handler = new ScriptedHandler(); handler.Add(_ => true, _ => ScriptedHandler.Json(echo, HttpStatusCode.BadRequest));
		var error = await Assert.ThrowsAsync<ProviderException>(() => Provider(handler).CompleteAsync(request, null, default));
		Assert.Equal(400, error.Status); Assert.False(error.MayHaveBilled); Assert.False(error.Retryable);
		Assert.Equal("openai HTTP 400: [provider diagnostics omitted for image request]", error.Message);
		handler = new ScriptedHandler();
		var data = variant == "malformed" ? echo : "{\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":51,\"output_tokens\":7},\"debug\":" + echo + "}}";
		handler.Add(_ => true, _ => ScriptedHandler.Sse("data: " + data + "\n\n"));
		if(variant == "malformed") {
			error = await Assert.ThrowsAsync<ProviderException>(() => Provider(handler).CompleteAsync(request, null, default));
			Assert.True(error.MayHaveBilled); Assert.True(error.Retryable);
			Assert.Equal("openai stream interrupted: [provider diagnostics omitted for image request]", error.Message);
		} else {
			var response = await Provider(handler).CompleteAsync(request, null, default);
			Assert.Equal("[request diagnostics omitted for image request]", response.RawRequest);
			Assert.Equal("[response diagnostics omitted for image request]", response.RawResponse);
			Assert.Equal(51, response.Usage.InputTokens); Assert.Equal(7, response.Usage.OutputTokens); Assert.True(response.Usage.Reported);
		}
	}

	[Fact]
	public async Task GeneratedContentRemainsContentWhileMidstreamErrorsRetainBillingUncertainty() {
		var request = Request(); var encoded = Convert.ToBase64String(request.ImageData["test-image"]);
		var delta = "data: " + JsonUtil.Serialize(new { type = "response.output_text.delta", delta = encoded }) + "\n\n";
		var handler = new ScriptedHandler(); handler.Add(_ => true, _ => ScriptedHandler.Sse(delta + "data: {\"type\":\"error\",\"debug\":\"" + encoded + "\"}\n\n"));
		var seen = new List<StreamDelta>();
		var error = await Assert.ThrowsAsync<ProviderException>(() => Provider(handler).CompleteAsync(request, seen.Add, default));
		Assert.True(error.MayHaveBilled); Assert.False(error.Retryable);
		Assert.Equal("openai error event: [provider diagnostics omitted for image request]", error.Message);
		Assert.True(Assert.Single(seen).Text == encoded); // Intentional generated content is not raw diagnostics.
	}

	[Fact]
	public async Task SyntheticUiRegistryCoversCatalogAndNeverNeedsCredentialsOrExternalTransport() {
		using var home = new TempHome(); var registry = OfflineUiProviders.Create(home.Path);
		Assert.Equal(ModelCatalog.Seed.Select(m => m.Provider).Distinct().Order(), registry.Ids.Order());
		foreach(var model in ModelCatalog.Seed) {
			var request = new ProviderRequest { Model = model, Messages = [ChatMessage.User("fixture")] };
			if(model.Provider == "openai") {
				var result = await registry.Get(model.Provider).CompleteAsync(request, null, default);
				Assert.Contains("OFFLINE UI FIXTURE", result.Content);
			} else await Assert.ThrowsAsync<ProviderException>(() => registry.Get(model.Provider).CompleteAsync(request, null, default));
		}
		var image = await registry.Get("openai").CompleteAsync(Request(), null, default);
		Assert.Contains("received 1 image input(s)", image.Content);
		Assert.Throws<ProviderException>(() => registry.Get("not-in-catalog"));
	}
}
