using System.Buffers.Binary;
using System.Security.Cryptography;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Tools;

namespace Ainur.Core.Runtime;

/// <summary>Hydrate only artifact-backed tool images at model dispatch, with hard limits verified against actual bytes.</summary>
public static class BrowserImageInput {
	public const int MaxImagesPerRequest = 1;
	public const int MaxImageBytes = 2 * 1024 * 1024;
	public const int MaxDimension = 2048;

	/// <summary>Only tool-result references persisted in this session may reach a provider.
	/// Do not accept images embedded in user prompts, model-produced paths, or message edits.</summary>
	public static void EnsureAuthorized(Store store, string sessionId, IReadOnlyList<ChatMessage> messages) {
		var images = messages.SelectMany(m => m.Images.Select(image => (Message: m, Image: image))).ToList();
		if(images.Count == 0) return;
		if(images.Any(pair => pair.Message.Role != "tool" || string.IsNullOrWhiteSpace(pair.Message.ToolCallId)))
			throw new ProviderException("Browser images must originate in persisted tool results, not user-supplied messages.");
		var recorded = store.Items(sessionId).Where(item => item.Kind == ItemKinds.ToolResult)
			.Select(item => JsonUtil.Deserialize<ToolResultPayload>(item.Payload))
			.Where(result => result is not null).ToList();
		foreach(var (message, image) in images) {
			if(!recorded.Any(result => result!.CallId == message.ToolCallId && result.Images.Contains(image)))
				throw new ProviderException("Browser image reference is not authorized by a persisted tool result in this session.");
		}
	}

	public static IReadOnlyDictionary<string, byte[]> Hydrate(ArtifactStore store, IReadOnlyList<ToolImage> images) {
		if(images.Count > MaxImagesPerRequest)
			throw new ProviderException($"Browser image request exceeds {MaxImagesPerRequest} image per model call; take a fresh screenshot instead.");
		var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		foreach(var image in images) {
			if(image.MimeType != "image/png" || image.Width is < 1 or > MaxDimension || image.Height is < 1 or > MaxDimension)
				throw new ProviderException("Browser image must be image/png with bounded, positive pixel dimensions.");
			if(!image.Artifact.StartsWith("sha256:", StringComparison.Ordinal) || image.Artifact.Length != 71 ||
				!image.Artifact.AsSpan(7).ToString().All(Uri.IsHexDigit))
				throw new ProviderException("Browser image is not a valid content-addressed artifact reference.");
			// The artifact is referenced by the persisted tool_result; never resolve model-supplied paths or URLs.
			var bytes = store.Get(image.Artifact) ?? throw new ProviderException("Browser image artifact is missing; cannot silently discard the screenshot.");
			if(bytes.Length is < 33 or > MaxImageBytes) throw new ProviderException($"Browser image exceeds the {MaxImageBytes}-byte limit or is empty.");
			var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
			if(!string.Equals(image.Artifact[7..], digest, StringComparison.OrdinalIgnoreCase))
				throw new ProviderException("Browser image artifact failed SHA-256 verification.");
			ReadOnlySpan<byte> b = bytes;
			if(!b[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
				BinaryPrimitives.ReadUInt32BigEndian(b.Slice(8, 4)) != 13 ||
				!b.Slice(12, 4).SequenceEqual("IHDR"u8) ||
				b[24] != 8 || b[25] is not (0 or 2 or 3 or 4 or 6) || b[26] != 0 || b[27] != 0 || b[28] != 0)
				throw new ProviderException("Browser image artifact is not PNG data.");
			var width = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4));
			var height = BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4));
			if(width != image.Width || height != image.Height || width == 0 || height == 0 || width > MaxDimension || height > MaxDimension)
				throw new ProviderException("Browser image PNG dimensions do not match the bounded tool-result metadata.");
			// Reject huge decompressed canvases even when compressed bytes fit the wire limit.
			if((long) width * height * 4 > 12L * 1024 * 1024)
				throw new ProviderException("Browser image decoded pixel budget exceeds 12 MiB.");
			loaded[image.Artifact] = bytes;
		}
		return loaded;
	}
}
