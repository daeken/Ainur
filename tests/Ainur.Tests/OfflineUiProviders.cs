using System.Net;
using System.Text;
using System.Text.Json;
using Ainur.Core;
using Ainur.Core.Accounting;
using Ainur.Core.Providers;

namespace Ainur.Tests;

/// <summary>Disposable browser-test fixture, never production startup configuration.</summary>
public static class OfflineUiProviders {
	public static ProviderRegistry Create(string freshHome) {
		var auth = Path.Combine(freshHome, "offline-ui-synthetic-auth.json");
		Directory.CreateDirectory(freshHome);
		File.WriteAllText(auth, JsonUtil.Serialize(new { tokens = new { access_token = "synthetic-no-network", account_id = "synthetic", expires_at = Clock.Now + 86_400_000 } }));
		var registry = new ProviderRegistry(); // Deliberately NEVER call DefaultProviders or resolve credentials.
		foreach(var id in ModelCatalog.Seed.Select(m => m.Provider).Distinct()) registry.Register(new ClosedProvider(id));
		registry.Register(new OpenAiResponsesProvider(new HttpClient(new InMemoryResponses()),
			apiKey: () => "synthetic-no-network", codexAuthPath: () => auth, routeOverride: "auto"));
		return registry;
	}

	sealed class ClosedProvider(string id) : IModelProvider {
		public string Id => id;
		public Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) =>
			throw new ProviderException("OFFLINE UI FIXTURE: this provider is intentionally unavailable; no network transport exists.");
	}

	sealed class InMemoryResponses : HttpMessageHandler {
		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
			if(request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri is not (OpenAiResponsesProvider.ApiEndpoint or OpenAiResponsesProvider.SubscriptionEndpoint))
				throw new InvalidOperationException("OFFLINE UI FIXTURE: unexpected request; no external transport exists.");
			using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
			var images = 0;
			foreach(var item in body.RootElement.GetProperty("input").EnumerateArray()) {
				if(!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
				foreach(var part in content.EnumerateArray()) if(part.GetProperty("type").GetString() == "input_image") {
					var url = part.GetProperty("image_url").GetString()!;
					const string prefix = "data:image/png;base64,";
					if(!url.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidOperationException("Fixture expected PNG pixels.");
					Ainur.Core.Runtime.ConversationImageValidation.Validate(Convert.FromBase64String(url[prefix.Length..]), "image/png");
					images++;
				}
			}
			// Only deterministic plain text: no tool calls, no model claims, no encoded bytes logged.
			var delta = JsonUtil.Serialize(new { type = "response.output_text.delta", delta = $"OFFLINE UI FIXTURE: received {images} image input(s). No real model or network call occurred." });
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("data: " + delta + "\n\ndata: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n", Encoding.UTF8, "text/event-stream") };
		}
	}
}
