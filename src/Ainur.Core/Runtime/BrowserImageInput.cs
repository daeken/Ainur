using System.Security.Cryptography;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Tools;
using Dapper;

namespace Ainur.Core.Runtime;

/// <summary>One dispatch boundary for durable user uploads and untrusted tool images.</summary>
public static class BrowserImageInput {
	public const int MaxImagesPerRequest = 4;
	public const int MaxImageBytes = ConversationImageValidation.MaxBytes;
	public const int MaxDimension = ConversationImageValidation.MaxDimension;
	public const int MaxRequestBytes = 8 * 1024 * 1024;

	public static void EnsureAuthorized(Store store, string projectId, string sessionId, IReadOnlyList<ChatMessage> messages) {
		var session = store.GetSession(sessionId);
		if(session is null || session.ProjectId != projectId) throw new ProviderException("Image input requires a session in the calling project.");
		foreach(var message in messages.Where(m => m.Images.Count > 0)) {
			var item = message.SourceItemId is null ? null : store.GetItem(message.SourceItemId);
			if(item is null || item.SessionId != sessionId) throw new ProviderException("Image source is not a durable item in this session.");
			if(message.Role == "tool" && item.Kind == ItemKinds.ToolResult) {
				var result = JsonUtil.Deserialize<ToolResultPayload>(item.Payload)!;
				if(string.IsNullOrEmpty(message.ToolCallId) || result.CallId != message.ToolCallId || message.Images.Any(i => !result.Images.Contains(i)))
					throw new ProviderException("Tool image must match its persisted tool call result.");
			} else if(message.Role == "user" && item.Kind == ItemKinds.User) {
				var user = JsonUtil.Deserialize<UserPayload>(item.Payload)!;
				if(message.Images.Count > AinurRuntime.MaxConversationImages || !message.Images.SequenceEqual(user.Images))
					throw new ProviderException("User images must match the original durable user message.");
				var bound = store.Db.Read(c => c.Query<ConversationImage>("""
					SELECT i.* FROM conversation_images i JOIN conversation c ON c.id=i.conversation_id
					JOIN notifications n ON n.causal_parent_id=c.id
					WHERE i.session_id=@sessionId AND i.project_id=@projectId AND n.id IN @ids
					AND n.type='user_message' AND n.to_agent_id=@agentId AND n.project_id=@projectId
					AND c.project_id=@projectId AND c.author='user' AND i.deleted_at IS NULL
					ORDER BY i.attachment_order
					""", new { sessionId, projectId, ids = user.NotificationIds, agentId = session.AgentId }).ToList());
				if(user.NotificationIds.Count != 1 || !message.Images.SequenceEqual(bound.Select(i => new ToolImage(i.Artifact, i.MimeType, i.Width, i.Height))))
					throw new ProviderException("User images are not bound to an authorized conversation notification.");
			} else throw new ProviderException("Images cannot change provenance between user and tool roles.");
		}
	}

	public static IReadOnlyDictionary<string, byte[]> Hydrate(ArtifactStore store, IReadOnlyList<ToolImage> images) {
		if(images.Count > MaxImagesPerRequest) throw new ProviderException("Image request exceeds four images; no pixels were silently dropped.");
		var loaded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		var total = 0;
		foreach(var image in images) {
			if(!image.Artifact.StartsWith("sha256:", StringComparison.Ordinal) || image.Artifact.Length != 71 || !image.Artifact.AsSpan(7).ToString().All(Uri.IsHexDigit))
				throw new ProviderException("Image is not a content-addressed artifact reference.");
			byte[] bytes;
			try {
				using var stream = File.OpenRead(Path.Combine(store.Root, image.Artifact[7..9], image.Artifact[9..]));
				if(stream.Length > MaxImageBytes) throw new ProviderException("Image exceeds 2 MiB.");
				bytes = new byte[(int) stream.Length]; stream.ReadExactly(bytes);
			} catch(IOException) { throw new ProviderException("Image artifact is missing or unreadable; refusing text-only fallback."); }
			total += bytes.Length;
			if(total > MaxRequestBytes) throw new ProviderException("Image request exceeds 8 MiB.");
			if(!string.Equals(image.Artifact[7..], Convert.ToHexStringLower(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase))
				throw new ProviderException("Image artifact failed SHA-256 verification.");
			try {
				if(ConversationImageValidation.Validate(bytes, image.MimeType) != (image.Width, image.Height)) throw new ProviderException("Image dimensions differ from durable metadata.");
			} catch(DomainException e) { throw new ProviderException(e.Message); }
			loaded[image.Artifact] = bytes;
		}
		return loaded;
	}
}
