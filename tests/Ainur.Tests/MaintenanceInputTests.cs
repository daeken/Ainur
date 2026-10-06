using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Runtime;
using Ainur.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ainur.Tests;

public class MaintenanceInputTests {
	static Project Project(AinurRuntime rt) => rt.CreateProject("input", "", null, managerModelId: "deepseek-v4.1-flash");
	static void AssertAcceptedOnce(AinurRuntime rt, Project p, ConversationEntry entry, string? imageId) {
		Assert.Single(rt.Store.Conversation(p.Id), c => c.Author == "user");
		var notification = Assert.Single(rt.Store.ListNotifications(p.Id), n => n.Type == NotificationTypes.UserMessage);
		Assert.Equal(entry.Id, notification.CausalParentId);
		Assert.Equal("pending", notification.State);
		if(imageId is not null) {
			Assert.Equal(imageId, Assert.Single(entry.Attachments).Id);
			Assert.Single(rt.ConversationImagesForNotification(rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!, notification.Id));
			Assert.NotNull(rt.ReadConversationImage(p.Id, imageId));
		}
	}

	[Theory]
	[InlineData(false, "operator intentional pause")]
	[InlineData(true, "operator intentional pause")]
	[InlineData(false, "cash safeguard remains authoritative")]
	[InlineData(true, "cash safeguard remains authoritative")]
	public void DirectPausedRootInputIsAcceptedOnceWithoutResumeOrChangingPausePolicy(bool image, string reason) {
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => o.AutoStartHosts = false);
		var p = Project(rt);
		var sid = rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!;
		rt.PauseAgent(p.RootAgentId!, null, reason);
		var pause = rt.RequestPause(sid, sid, "workspace", reason, "explicit operator release", TimeSpan.FromHours(1));
		var imageId = image ? rt.UploadConversationImage(p.Id, ConversationImageTests.Png(), "image/png").Id : null;
		var hosts = rt.LiveHosts.Count;
		rt.Options.AutoStartHosts = true;
		var op = rt.Maintenance.Begin("retain input", TimeSpan.FromSeconds(1));
		var entry = rt.PostUserMessage(p.Id, "queued", imageId is null ? [] : [imageId], "same-input");
		Assert.Equal(entry.Id, rt.PostUserMessage(p.Id, "queued", imageId is null ? [] : [imageId], "same-input").Id);
		AssertAcceptedOnce(rt, p, entry, imageId);
		Assert.Equal(hosts, rt.LiveHosts.Count);
		Assert.Null(rt.GetHost(sid));
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(p.RootAgentId!)!.State);
		Assert.Equal(pause.Id, rt.ActivePause(sid)!.Id);
		Assert.Equal(reason, rt.ActivePause(sid)!.Reason);
		Assert.DoesNotContain(rt.Store.Events(p.Id), e => e.Kind == "agent.resumed");
		Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
		rt.Maintenance.Release(op.Operation!.Id, true);
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(p.RootAgentId!)!.State);
		Assert.Equal(pause.Id, rt.ActivePause(sid)!.Id);
		AssertAcceptedOnce(rt, p, entry, imageId);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FenceArrivingExactlyAfterMessageCommitDoesNotFalselyRejectOrResume(bool image) {
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => o.AutoStartHosts = false);
		var p = Project(rt);
		rt.PauseAgent(p.RootAgentId!, null, "intentional");
		var imageId = image ? rt.UploadConversationImage(p.Id, ConversationImageTests.Png(), "image/png").Id : null;
		string? operation = null;
		var claimed = false;
		// Db.Committed is synchronous AFTER transaction commit, BEFORE PostUserMessage
		// attempts its activation. This exercises the exact reported race without sleep.
		rt.Db.Committed += _ => {
			if(claimed) return;
			claimed = true;
			operation = rt.Maintenance.Begin("post-commit fence", TimeSpan.FromSeconds(1)).Operation!.Id;
		};
		var entry = rt.PostUserMessage(p.Id, "racing input", imageId is null ? [] : [imageId], "race-input");
		Assert.NotNull(operation);
		Assert.True(rt.Maintenance.Fenced);
		Assert.Equal(entry.Id, rt.PostUserMessage(p.Id, "racing input", imageId is null ? [] : [imageId], "race-input").Id);
		AssertAcceptedOnce(rt, p, entry, imageId);
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(p.RootAgentId!)!.State);
		Assert.Empty(rt.LiveHosts);
		rt.Maintenance.Release(operation!, true);
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(p.RootAgentId!)!.State);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PausedRootActualHttpTextAndImagesReportAcceptedFirstAndRetryWhileFenced(bool image) {
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => o.AutoStartHosts = false);
		var p = Project(rt);
		rt.PauseAgent(p.RootAgentId!, null, "intentional HTTP root pause");
		var builder = WebApplication.CreateBuilder();
		builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(rt); builder.Services.AddSingleton(new ServerOptions()); builder.Services.AddSingleton<EventHub>();
		builder.Services.ConfigureHttpJsonOptions(o => { o.SerializerOptions.PropertyNamingPolicy = JsonUtil.Options.PropertyNamingPolicy; o.SerializerOptions.DefaultIgnoreCondition = JsonUtil.Options.DefaultIgnoreCondition; });
		await using var app = builder.Build(); Api.Map(app); await app.StartAsync();
		using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
		var op = rt.Maintenance.Begin("HTTP retained input", TimeSpan.FromSeconds(1));
		var path = $"/api/v1/projects/{p.Id}/conversation";
		string? imageId = null;
		if(image) {
			using var content = new ByteArrayContent(ConversationImageTests.Png());
			content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
			var uploaded = await client.PostAsync(path + "/images", content);
			Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
			imageId = JsonNode.Parse(await uploaded.Content.ReadAsStringAsync())!["id"]!.GetValue<string>();
		}
		var body = new { text = "accepted while held", attachment_ids = imageId is null ? Array.Empty<string>() : new[] { imageId }, client_message_id = "http-retained" };
		var first = await client.PostAsJsonAsync(path, body);
		Assert.Equal(HttpStatusCode.OK, first.StatusCode);
		var repeat = await client.PostAsJsonAsync(path, body);
		Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
		Assert.Equal(await first.Content.ReadAsStringAsync(), await repeat.Content.ReadAsStringAsync());
		var entry = rt.Store.Conversation(p.Id).Single(c => c.Author == "user");
		AssertAcceptedOnce(rt, p, entry, imageId);
		Assert.Empty(rt.LiveHosts);
		Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(p.RootAgentId!)!.State);
		rt.Maintenance.Release(op.Operation!.Id, true);
		Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(p.RootAgentId!)!.State);
		await app.StopAsync();
	}

	[Fact]
	public void UnknownMaintenanceOwnerAfterRestartRetainsInputAndAttachmentsWithoutResumeOrReplay() {
		using var home = new TempHome();
		string projectId, rootId, sid, imageId, operation;
		using(var rt = home.Runtime(configure: o => o.AutoStartHosts = false)) {
			var p = Project(rt); projectId = p.Id; rootId = p.RootAgentId!;
			sid = rt.Store.GetAgent(rootId)!.PrimarySessionId!;
			rt.PauseAgent(rootId, null, "intentional pause across uncertainty");
			imageId = rt.UploadConversationImage(p.Id, ConversationImageTests.Png(), "image/png").Id;
			_ = rt.Maintenance.Admit("unresolved_action", sid, "do-not-replay");
			operation = rt.Maintenance.Begin("restart uncertainty", TimeSpan.FromSeconds(1)).Operation!.Id;
		}
		using(var rt = home.Runtime(configure: o => o.AutoStartHosts = false)) {
			Assert.True(rt.Maintenance.HasUnknown(sid));
			rt.Maintenance.Release(operation, true); // no global fence, UNKNOWN still authoritative
			rt.Options.AutoStartHosts = true;
			var entry = rt.PostUserMessage(projectId, "input during uncertainty", [imageId], "unknown-input");
			Assert.Equal(entry.Id, rt.PostUserMessage(projectId, "input during uncertainty", [imageId], "unknown-input").Id);
			AssertAcceptedOnce(rt, rt.Store.GetProject(projectId)!, entry, imageId);
			Assert.Equal(AgentStates.Paused, rt.Store.GetAgent(rootId)!.State);
			Assert.Null(rt.GetHost(sid));
			Assert.Empty(rt.LiveHosts);
			Assert.Empty(rt.Store.ModelRequestsInState("dispatched"));
			Assert.True(rt.Maintenance.HasUnknown(sid));
		}
	}

	[Fact]
	public void OrdinaryMessageOutsideMaintenancePreservesExistingPausedRootResumeSemantics() {
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => o.AutoStartHosts = false);
		var p = Project(rt);
		rt.PauseAgent(p.RootAgentId!, null, "ordinary operator pause");
		var sid = rt.Store.GetAgent(p.RootAgentId!)!.PrimarySessionId!;
		var pause = rt.RequestPause(sid, sid, "workspace", "separate intentional scope", "explicit release", TimeSpan.FromHours(1));
		var entry = rt.PostUserMessage(p.Id, "normal legacy input", [], "ordinary-input");
		Assert.Equal(AgentStates.Sleeping, rt.Store.GetAgent(p.RootAgentId!)!.State);
		Assert.Equal(pause.Id, rt.ActivePause(sid)!.Id); // scoped pause is not released by existing root-resume policy
		AssertAcceptedOnce(rt, p, entry, null);
		Assert.Single(rt.Store.Events(p.Id), e => e.Kind == "agent.resumed");
	}
}
