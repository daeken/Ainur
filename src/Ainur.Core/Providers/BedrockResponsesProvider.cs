using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.Runtime.Internal;
using Amazon.Runtime.Internal.Auth;

namespace Ainur.Core.Providers;

/// <summary>OpenAI Responses over AWS SigV4. The SDK refreshes shared-profile/credential_process credentials.</summary>
public sealed class BedrockResponsesProvider : IModelProvider {
	public string Id { get; }
	public string Region { get; }
	public Uri Endpoint { get; }
	readonly HttpClient http;
	readonly Lazy<AWSCredentials> credentials;
	readonly OpenAiResponsesProvider codec;
	readonly AmazonBedrockRuntimeConfig config;

	public BedrockResponsesProvider(HttpClient http, string region, bool mantle = false, Func<AWSCredentials>? credentials = null) {
		if(!Regex.IsMatch(region, @"^[a-z]{2}(?:-[a-z]+)+-\d+$"))
			throw new ArgumentException("Expected an AWS region identifier.", nameof(region));
		Id = mantle ? "bedrock-mantle" : "bedrock";
		Region = region;
		var domain = region.StartsWith("cn-", StringComparison.Ordinal) ? "amazonaws.com.cn" : "amazonaws.com";
		Endpoint = new Uri(mantle ? $"https://bedrock-mantle.{region}.api.aws/openai/v1/responses"
			: $"https://bedrock-runtime.{region}.{domain}/openai/v1/responses");
		this.http = http;
		this.credentials = new Lazy<AWSCredentials>(credentials ?? ResolveCredentials);
		codec = new OpenAiResponsesProvider(http, routeOverride: "subscription");
		config = new AmazonBedrockRuntimeConfig { AuthenticationRegion = region, AuthenticationServiceName = "bedrock", ServiceURL = Endpoint.GetLeftPart(UriPartial.Authority) };
	}

	static AWSCredentials ResolveCredentials() {
		var profile = Environment.GetEnvironmentVariable("AWS_PROFILE") ?? "default";
		if(new CredentialProfileStoreChain().TryGetAWSCredentials(profile, out var credentials)) return credentials;
		throw new ProviderException($"AWS profile '{profile}' was not found. Configure a shared AWS profile, including credential_process when using temporary credentials.");
	}

	public JsonObject BuildRequest(ProviderRequest request) {
		if(request.Model.Provider != Id || request.Model.Billing != "api")
			throw new ProviderException($"{Id} requires an API-billed model belonging to this provider.");
		if(request.EnableWebSearch)
			throw new ProviderException("Native web search is not enabled for this Bedrock adapter; use client-side tools.");
		if(request.Messages.Any(m => m.Images.Count > 0))
			throw new ProviderException("Image inputs are not yet supported by the Bedrock adapter; no images were sent or dropped.");
		if(request.MaxOutputTokens <= 0) throw new ProviderException("Max output tokens must be positive.");
		var body = codec.BuildRequest(request);
		body["max_output_tokens"] = request.Model.MaxOutputTokens is { } max ? Math.Min(max, request.MaxOutputTokens) : request.MaxOutputTokens;
		if(request.ToolChoice is not null) body["tool_choice"] = request.ToolChoice;
		return body;
	}

	public Task<ProviderResponse> CompleteAsync(ProviderRequest request, Action<StreamDelta>? onDelta, CancellationToken ct) =>
		OpenAiResponsesProvider.SendResponsesAsync(http, Id, Endpoint.AbsoluteUri, request, BuildRequest(request), onDelta, ct, SignAsync);

	async Task SignAsync(HttpRequestMessage message, CancellationToken ct) {
		ImmutableCredentials current;
		try {
			current = await credentials.Value.GetCredentialsAsync();
			ct.ThrowIfCancellationRequested();
		} catch(OperationCanceledException) { throw; }
		catch(Exception) {
			// A credential process's stderr may contain secrets. Keep it out of persisted model errors.
			throw new ProviderException("AWS credential resolution failed. Check the configured AWS profile and refresh your host login.");
		}
		var request = new DefaultRequest(new SignableRequest(), "bedrock") {
			HttpMethod = "POST", Endpoint = new Uri(Endpoint.GetLeftPart(UriPartial.Authority)),
			ResourcePath = Endpoint.AbsolutePath, Content = await message.Content!.ReadAsByteArrayAsync(ct),
		};
		request.Headers["Content-Type"] = message.Content.Headers.ContentType!.ToString();
		if(current.UseToken) request.Headers["X-Amz-Security-Token"] = current.Token;
		var signed = new AWS4Signer().SignRequest(request, config, null, current.AccessKey, current.SecretKey);
		foreach(var (name, value) in request.Headers)
			if(!name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) message.Headers.TryAddWithoutValidation(name, value);
		message.Headers.TryAddWithoutValidation("Authorization", signed.ForAuthorizationHeader);
	}

	sealed class SignableRequest : AmazonWebServiceRequest;

	public static void RegisterConfigured(ProviderRegistry registry) {
		if(Environment.GetEnvironmentVariable("AINUR_BEDROCK_ENABLED") != "1") return;
		var region = Environment.GetEnvironmentVariable("AWS_REGION") ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION") ?? "us-west-2";
		HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(30) };
		registry.Register(new BedrockResponsesProvider(Client(), region));
		registry.Register(new BedrockResponsesProvider(Client(), Environment.GetEnvironmentVariable("AINUR_MANTLE_REGION") ?? region, mantle: true));
	}
}
