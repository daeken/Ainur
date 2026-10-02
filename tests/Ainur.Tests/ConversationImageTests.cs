using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Ainur.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Ainur.Tests;

public class ConversationImageTests {
	internal static byte[] Png(int width = 2, int height = 2) {
		using var bitmap = new SKBitmap(width, height);
		bitmap.Erase(SKColors.Red);
		using var image = SKImage.FromBitmap(bitmap);
		using var data = image.Encode(SKEncodedImageFormat.Png, 100);
		return data.ToArray();
	}
	static byte[] Chunk(string type, byte[] data) {
		var bytes = new byte[data.Length + 12];
		BinaryPrimitives.WriteInt32BigEndian(bytes, data.Length);
		System.Text.Encoding.ASCII.GetBytes(type).CopyTo(bytes, 4); data.CopyTo(bytes, 8);
		var crc = uint.MaxValue;
		foreach(var b in bytes.AsSpan(4, data.Length + 4)) {
			crc ^= b;
			for(var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint) -(int) (crc & 1));
		}
		BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(bytes.Length - 4), ~crc);
		return bytes;
	}

	[Fact]
	public void ActualPngDecodeRejectsSignatureCrcTruncationAnimationAndDecompressionDimensions() {
		var png = Png();
		Assert.Equal((2, 2), ConversationImageValidation.Validate(png, "image/png"));
		foreach(var mime in new[] { "image/jpeg", "image/webp", "image/svg+xml", "text/html" })
			Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(png, mime));
		Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(new byte[ConversationImageValidation.MaxBytes + 1], "image/png"));
		Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(png[..^1], "image/png"));
		var corrupt = png.ToArray(); corrupt[40] ^= 1;
		Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(corrupt, "image/png"));
		var header = png[16..29]; BinaryPrimitives.WriteUInt32BigEndian(header, 100_000);
		var huge = png[..8].Concat(Chunk("IHDR", header)).Concat(png[33..]).ToArray();
		Assert.Contains("dimensions", Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(huge, "image/png")).Message);
		Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(Png(1800, 1800), "image/png"));
		var animated = png[..33].Concat(Chunk("acTL", new byte[8])).Concat(png[33..]).ToArray();
		Assert.Contains("Animated", Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(animated, "image/png")).Message);
		// Well-framed/CRC-correct PNG with invalid compressed pixel data must fail the actual codec.
		var undecodable = png[..33].Concat(Chunk("IDAT", new byte[] { 1, 2, 3, 4 })).Concat(Chunk("IEND", [])).ToArray();
		Assert.Throws<DomainException>(() => ConversationImageValidation.Validate(undecodable, "image/png"));
	}

	[Fact]
	public async Task ImageOnlySendRetriesAreAtomicScopedAndDurableWithoutEncodedPayloads() {
		using var home = new TempHome();
		string projectId, imageId, entryId, sessionId, notificationId;
		var png = Png();
		using(var rt = home.Runtime(start: false)) {
			var project = rt.CreateProject("images", "", home.Workspace);
			projectId = project.Id;
			var other = rt.CreateProject("other", "", home.Workspace);
			var image = rt.UploadConversationImage(projectId, png, "image/png"); imageId = image.Id; sessionId = image.SessionId;
			Assert.Null(rt.ReadConversationImage(other.Id, imageId));
			Assert.Throws<DomainException>(() => rt.PostUserMessage(other.Id, "", [imageId], "wrong-project"));
			Assert.Throws<DomainException>(() => rt.PostUserMessage(projectId, "", [imageId, "img_missing"], "bad"));
			Assert.Empty(rt.Store.Conversation(projectId));
			Assert.DoesNotContain(rt.Store.ListNotifications(projectId), n => n.Type == NotificationTypes.UserMessage);
			Assert.Throws<DomainException>(() => rt.PostUserMessage(projectId, "", [imageId], null));
			var receipts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => rt.PostUserMessage(projectId, "", [imageId], "same-key"))));
			entryId = receipts[0].Id;
			Assert.All(receipts, e => Assert.Equal(entryId, e.Id));
			Assert.Single(rt.Store.Conversation(projectId));
			var notification = Assert.Single(rt.Store.ListNotifications(projectId), n => n.Type == NotificationTypes.UserMessage); notificationId = notification.Id;
			Assert.Equal(entryId, notification.CausalParentId);
			Assert.Single(rt.ConversationImagesForNotification(sessionId, notificationId));
			Assert.Empty(rt.ConversationImagesForNotification(rt.Store.GetAgent(other.RootAgentId!)!.PrimarySessionId!, notificationId));
			Assert.Throws<ConversationConflictException>(() => rt.PostUserMessage(projectId, "different", [imageId], "same-key"));
			Assert.Throws<ConversationConflictException>(() => rt.PostUserMessage(projectId, "", [imageId], "new-key"));
			Assert.Throws<ConversationConflictException>(() => rt.RemoveConversationImage(projectId, imageId));
			Assert.DoesNotContain(Convert.ToBase64String(png), JsonUtil.Serialize(rt.Store.Events(projectId)));
			Assert.DoesNotContain("sha256:", JsonUtil.Serialize(rt.Store.Conversation(projectId)));
		}
		using(var reopened = home.Runtime(start: false)) {
			Assert.Equal(png, reopened.ReadConversationImage(projectId, imageId));
			Assert.Equal(imageId, Assert.Single(Assert.Single(reopened.Store.Conversation(projectId)).Attachments).Id);
			Assert.Equal(entryId, reopened.PostUserMessage(projectId, "", [imageId], "same-key").Id);
			Assert.Single(reopened.ConversationImagesForNotification(sessionId, notificationId));
		}
	}

	[Fact]
	public void ExpiredDeletedMissingAndQuotaFailuresNeverPersistMessagesAndRetainQuota() {
		using var home = new TempHome(); using var rt = home.Runtime(start: false);
		var p = rt.CreateProject("images", "", home.Workspace); var bytes = Png();
		var expired = rt.UploadConversationImage(p.Id, bytes, "image/png");
		rt.Db.Write(u => u.Execute("UPDATE conversation_images SET expires_at=0 WHERE id=@Id", expired));
		Assert.Null(rt.ReadConversationImage(p.Id, expired.Id));
		Assert.Throws<DomainException>(() => rt.PostUserMessage(p.Id, "", [expired.Id], "expired"));
		var deleted = rt.UploadConversationImage(p.Id, bytes, "image/png"); Assert.True(rt.RemoveConversationImage(p.Id, deleted.Id));
		Assert.Null(rt.ReadConversationImage(p.Id, deleted.Id));
		Assert.Throws<DomainException>(() => rt.PostUserMessage(p.Id, "", [deleted.Id], "deleted"));
		var missing = rt.UploadConversationImage(p.Id, bytes, "image/png"); File.Delete(Path.Combine(rt.Artifacts.Root, missing.Artifact[7..9], missing.Artifact[9..]));
		Assert.Throws<DomainException>(() => rt.PostUserMessage(p.Id, "", [missing.Id], "missing"));
		Assert.Empty(rt.Store.Conversation(p.Id));
		for(var i = 3; i < AinurRuntime.MaxRetainedConversationImages; i++) rt.RemoveConversationImage(p.Id, rt.UploadConversationImage(p.Id, bytes, "image/png").Id);
		Assert.Throws<DomainException>(() => rt.UploadConversationImage(p.Id, bytes, "image/png"));
		Assert.Equal(AinurRuntime.MaxRetainedConversationImages, rt.Db.Read(c => { using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT COUNT(*) FROM conversation_images"; return Convert.ToInt32(cmd.ExecuteScalar()); }));
		var other = rt.CreateProject("bytes quota", "", home.Workspace);
		var retained = rt.UploadConversationImage(other.Id, bytes, "image/png");
		rt.Db.Write(u => u.Execute("UPDATE conversation_images SET bytes=@bytes WHERE id=@Id", new { retained.Id, bytes = AinurRuntime.MaxRetainedConversationImageBytes }));
		Assert.Throws<DomainException>(() => rt.UploadConversationImage(other.Id, bytes, "image/png"));
	}

	[Fact]
	public void OrderedAttachmentReplayAndSessionScopeAreStrict() {
		using var home = new TempHome(); using var rt = home.Runtime(start: false);
		var p = rt.CreateProject("ordered", "", home.Workspace);
		var first = rt.UploadConversationImage(p.Id, Png(), "image/png");
		var second = rt.UploadConversationImage(p.Id, Png(3, 2), "image/png");
		var receipt = rt.PostUserMessage(p.Id, "ordered", [second.Id, first.Id], "ordered-key");
		Assert.Equal(new[] { second.Id, first.Id }, receipt.Attachments.Select(i => i.Id));
		Assert.Equal(new[] { second.Id, first.Id }, rt.PostUserMessage(p.Id, "ordered", [second.Id, first.Id], "ordered-key").Attachments.Select(i => i.Id));
		Assert.Equal(new[] { second.Id, first.Id }, Assert.Single(rt.Store.Conversation(p.Id)).Attachments.Select(i => i.Id));
		Assert.Throws<ConversationConflictException>(() => rt.PostUserMessage(p.Id, "ordered", [first.Id, second.Id], "ordered-key"));
		var pending = rt.UploadConversationImage(p.Id, Png(), "image/png");
		// A foreign session reference must fail even when its project column is forged to match.
		var other = rt.CreateProject("other", "", home.Workspace);
		rt.Db.Write(u => u.Execute("UPDATE conversation_images SET session_id=@sid WHERE id=@Id", new { pending.Id, sid = rt.Store.GetAgent(other.RootAgentId!)!.PrimarySessionId }));
		Assert.Null(rt.ReadConversationImage(p.Id, pending.Id));
		Assert.Throws<DomainException>(() => rt.PostUserMessage(p.Id, "", [pending.Id], "foreign-session"));
		Assert.Single(rt.Store.Conversation(p.Id));
	}

	[Fact]
	public async Task HttpUploadContentSendReplayDeleteAndLegacyTextContract() {
		using var home = new TempHome(); using var rt = home.Runtime(start: false);
		var p = rt.CreateProject("http", "", home.Workspace);
		var other = rt.CreateProject("other", "", home.Workspace);
		var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(rt); builder.Services.AddSingleton(new ServerOptions()); builder.Services.AddSingleton<EventHub>();
		builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonUtil.Options.PropertyNamingPolicy; o.SerializerOptions.DefaultIgnoreCondition = JsonUtil.Options.DefaultIgnoreCondition; });
		await using var app = builder.Build(); Api.Map(app); await app.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
		var path = $"/api/v1/projects/{p.Id}/conversation";
		async Task<HttpResponseMessage> Upload(byte[] bytes, string mime = "image/png") {
			using var content = new ByteArrayContent(bytes); content.Headers.ContentType = new(mime);
			return await client.PostAsync(path + "/images", content);
		}
		Assert.Equal(HttpStatusCode.BadRequest, (await Upload(Png(), "image/jpeg")).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await Upload(new byte[ConversationImageValidation.MaxBytes + 1])).StatusCode);
		Assert.Equal(HttpStatusCode.BadRequest, (await Upload(new byte[100])).StatusCode);
		var upload = await Upload(Png()); upload.EnsureSuccessStatusCode();
		var image = JsonNode.Parse(await upload.Content.ReadAsStringAsync())!;
		Assert.Equal("image/png", image["mime_type"]!.GetValue<string>()); Assert.Equal(2, image["width"]!.GetValue<int>());
		var imageId = image["id"]!.GetValue<string>(); var url = image["content_url"]!.GetValue<string>();
		var contentResponse = await client.GetAsync(url); contentResponse.EnsureSuccessStatusCode();
		Assert.Equal(Png(), await contentResponse.Content.ReadAsByteArrayAsync()); Assert.Equal("nosniff", contentResponse.Headers.GetValues("X-Content-Type-Options").Single());
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url.Replace(p.Id, other.Id))).StatusCode);
		var body = new { text = "", attachment_ids = new[] { imageId }, client_message_id = "http-retry" };
		var first = await client.PostAsJsonAsync(path, body); first.EnsureSuccessStatusCode();
		var repeat = await client.PostAsJsonAsync(path, body); repeat.EnsureSuccessStatusCode();
		Assert.Equal(await first.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
		Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(path, new { text = "changed", attachment_ids = new[] { imageId }, client_message_id = "http-retry" })).StatusCode);
		Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync(path + "/images/" + imageId)).StatusCode);
		var list = JsonNode.Parse(await client.GetStringAsync(path))!.AsArray(); Assert.Single(list[0]!["attachments"]!.AsArray());
		var legacy = await client.PostAsJsonAsync(path, new { text = "legacy text" }); legacy.EnsureSuccessStatusCode();
		Assert.Empty(JsonNode.Parse(await legacy.Content.ReadAsStringAsync())!["attachments"]!.AsArray());
		var draft = JsonNode.Parse(await (await Upload(Png())).Content.ReadAsStringAsync())!;
		Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(path + "/images/" + draft["id"]!.GetValue<string>())).StatusCode);
		Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(draft["content_url"]!.GetValue<string>())).StatusCode);
	}
}
