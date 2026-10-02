using System.Security.Cryptography;
using System.Text;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Tools;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed partial class AinurRuntime {
	public const int MaxConversationImages = 2;
	public const int MaxRetainedConversationImages = 128;
	public const long MaxRetainedConversationImageBytes = 256L * 1024 * 1024;
	static readonly SemaphoreSlim ImageUploadGate = new(1, 1);

	static (Project Project, Session Session) ImageScope(Db.Unit u, string projectId) {
		var project = u.Single<Project>("SELECT * FROM projects WHERE id=@projectId", new { projectId }) ?? throw new DomainException("Project not found.");
		var session = u.Single<Session>("SELECT * FROM sessions WHERE id=(SELECT primary_session_id FROM agents WHERE id=@id) AND project_id=@projectId", new { id = project.RootAgentId, projectId }) ?? throw new DomainException("Project has no root primary session.");
		return (project, session);
	}

	public ConversationImage UploadConversationImage(string projectId, byte[] bytes, string mimeType) {
		var dimensions = ConversationImageValidation.Validate(bytes, mimeType);
		ImageUploadGate.Wait();
		try {
			// Artifact writes happen only after decoded validation and scope/quota admission. Metadata
			// is retained even after deletion/expiry, so abandoned content never resets the disk quota.
			var image = Db.Write(u => {
				var (_, session) = ImageScope(u, projectId);
				if(u.Scalar<int>("SELECT COUNT(*) FROM conversation_images WHERE project_id=@projectId", new { projectId }) >= MaxRetainedConversationImages ||
					u.Scalar<long>("SELECT COALESCE(SUM(bytes),0) FROM conversation_images WHERE project_id=@projectId", new { projectId }) + bytes.Length > MaxRetainedConversationImageBytes)
					throw new DomainException("Retained image quota exhausted (128 uploads / 256 MiB per project, including deleted and expired uploads).");
				var image = new ConversationImage { Id = Ids.New("img"), ProjectId = projectId, SessionId = session.Id,
					Artifact = $"sha256:{Convert.ToHexStringLower(SHA256.HashData(bytes))}", MimeType = mimeType, Width = dimensions.Width, Height = dimensions.Height,
					Bytes = bytes.Length, CreatedAt = Clock.Now, ExpiresAt = Clock.Now + 24 * 60 * 60 * 1000L };
				u.Execute("""
					INSERT INTO conversation_images(id,project_id,session_id,artifact,mime_type,width,height,bytes,created_at,expires_at)
					VALUES(@Id,@ProjectId,@SessionId,@Artifact,@MimeType,@Width,@Height,@Bytes,@CreatedAt,@ExpiresAt)
					""", image);
				return image;
			});
			// Reserve retained quota durably first. A filesystem failure leaves charged metadata,
			// not unaccounted orphan bytes; send/content will reject missing artifacts.
			Artifacts.Put(bytes);
			return image;
		} finally { ImageUploadGate.Release(); }
	}

	static ConversationImage? ScopedImage(Db.Unit u, string projectId, string imageId) {
		var (_, session) = ImageScope(u, projectId);
		return u.Single<ConversationImage>("""
			SELECT * FROM conversation_images WHERE id=@imageId AND project_id=@projectId AND session_id=@sessionId
			AND deleted_at IS NULL AND (conversation_id IS NOT NULL OR expires_at>@now)
			""", new { imageId, projectId, sessionId = session.Id, now = Clock.Now });
	}

	public byte[]? ReadConversationImage(string projectId, string imageId) {
		var image = Db.Write(u => ScopedImage(u, projectId, imageId));
		if(image is null) return null;
		return ReadValidatedConversationImage(image);
	}

	byte[] ReadValidatedConversationImage(ConversationImage image) {
		try {
			var bytes = BrowserImageInput.Hydrate(Artifacts, [new ToolImage(image.Artifact, image.MimeType, image.Width, image.Height)])[image.Artifact];
			if(bytes.LongLength != image.Bytes) throw new DomainException("Stored image metadata does not match its content.");
			return bytes;
		} catch(Providers.ProviderException e) { throw new DomainException(e.Message); }
	}

	public bool RemoveConversationImage(string projectId, string imageId) => Db.Write(u => {
		var image = ScopedImage(u, projectId, imageId);
		if(image is null) return false;
		if(image.ConversationId is not null) throw new ConversationConflictException("Sent image attachments cannot be removed.");
		u.Execute("UPDATE conversation_images SET deleted_at=@now WHERE id=@imageId", new { now = Clock.Now, imageId });
		return true;
	});

	public List<ToolImage> ConversationImagesForNotification(string sessionId, string notificationId) {
		using var c = Db.Open();
		return c.Query<ConversationImage>("""
			SELECT i.* FROM conversation_images i JOIN conversation c ON c.id=i.conversation_id
			JOIN notifications n ON n.causal_parent_id=c.id JOIN sessions s ON s.id=i.session_id
			WHERE i.session_id=@sessionId AND n.id=@notificationId AND n.type='user_message'
			AND n.to_agent_id=s.agent_id AND n.project_id=i.project_id AND c.project_id=i.project_id
			AND c.author='user' AND i.deleted_at IS NULL ORDER BY i.attachment_order
			""", new { sessionId, notificationId }).Select(i => new ToolImage(i.Artifact, i.MimeType, i.Width, i.Height)).ToList();
	}

	public ConversationEntry PostUserMessage(string projectId, string text, IReadOnlyList<string>? attachmentIds, string? clientMessageId) {
		var ids = attachmentIds?.ToArray() ?? [];
		if(ids.Length > MaxConversationImages || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(string.IsNullOrWhiteSpace))
			throw new DomainException("A message accepts at most two distinct image attachment IDs.");
		if(string.IsNullOrWhiteSpace(text) && ids.Length == 0) throw new DomainException("A message requires text or images.");
		if(clientMessageId is not null && (clientMessageId.Length is < 1 or > 128 || clientMessageId.Any(char.IsControl))) throw new DomainException("client_message_id must be 1..128 characters without control characters.");
		if(ids.Length > 0 && clientMessageId is null) throw new DomainException("Image messages require a stable client_message_id for safe retries.");
		var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonUtil.Serialize(new { text, attachment_ids = ids }))));
		var fresh = false;
		string? rootId = null;
		var entry = Db.Write(u => {
			var (project, session) = ImageScope(u, projectId); rootId = project.RootAgentId;
			if(clientMessageId is not null) {
				var receipt = u.Single<ConversationReceipt>("SELECT * FROM conversation_receipts WHERE project_id=@projectId AND client_message_id=@clientMessageId", new { projectId, clientMessageId });
				if(receipt is not null) {
					if(receipt.Fingerprint != fingerprint) throw new ConversationConflictException("client_message_id was already used for different message content.");
					var previous = u.Single<ConversationEntry>("SELECT * FROM conversation WHERE id=@id", new { id = receipt.ConversationId })!;
					previous.Attachments = u.Query<ConversationImage>("SELECT * FROM conversation_images WHERE conversation_id=@id ORDER BY attachment_order", new { id = previous.Id });
					return previous;
				}
			}
			var images = ids.Select(id => ScopedImage(u, projectId, id) ?? throw new DomainException("Image attachment not found in this project/session or expired.")).ToList();
			foreach(var image in images) {
				if(image.ConversationId is not null) throw new ConversationConflictException("Image attachment is already bound to a sent message.");
				ReadValidatedConversationImage(image);
			}
			var conversation = Store.AppendConversation(u, projectId, "user", null, text);
			conversation.Attachments = images;
			Store.InsertNotification(u, NewNotification(projectId, NotificationTypes.UserMessage, null, rootId!, text, null, conversation.Id, $"conv:{conversation.Id}"));
			for(var index = 0; index < images.Count; index++) { var image = images[index]; image.ConversationId = conversation.Id; u.Execute("UPDATE conversation_images SET conversation_id=@conversationId, attachment_order=@index WHERE id=@id", new { conversationId = conversation.Id, id = image.Id, index }); }
			if(clientMessageId is not null) u.Execute("INSERT INTO conversation_receipts(project_id,client_message_id,fingerprint,conversation_id) VALUES(@projectId,@clientMessageId,@fingerprint,@conversationId)", new { projectId, clientMessageId, fingerprint, conversationId = conversation.Id });
			if(images.Count > 0) u.Journal("conversation.images_bound", projectId, "conversation", conversation.Id, payload: new { attachment_ids = ids });
			fresh = true; return conversation;
		});
		if(fresh) {
			if(Store.GetAgent(rootId!)!.State == AgentStates.Paused) ResumeAgent(rootId!, null);
			Wake(rootId!);
		}
		return entry;
	}

	sealed class ConversationReceipt {
		public string Fingerprint { get; set; } = "";
		public string ConversationId { get; set; } = "";
	}
}

public sealed class ConversationConflictException(string message) : Exception(message);
