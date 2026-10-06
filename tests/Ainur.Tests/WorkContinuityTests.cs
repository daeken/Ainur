using System.Reflection;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Context;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Dapper;
using Ainur.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Ainur.Tests;

public sealed class WorkContinuityTests {
	[Fact] public void AcceptedBeforeWakeSurvivesRestartAndRepeatedReconcileUsesOriginalNotification() {
		using var f = new Fixture(); var n = f.Assign();
		Assert.Equal("owed", f.Work(n.Id).State);
		f.Restart(); var wake = f.Reconcile(); Assert.Contains(f.Worker.Id, wake);
		for(int i=0;i<8;i++) f.Reconcile();
		Assert.Single(f.Notifications()); Assert.Equal(n.Id, f.Work(n.Id).ActionNotificationId);
	}
	[Fact] public void AllOwnersSleepingAfterConsumedAssignmentGetsBoundedOwnerAndManagerContinuationAcrossRestart() {
		using var f = new Fixture(); var n = f.Assign(); f.Deliver(n); f.Final(f.Worker);
		f.Restart(); f.Reconcile(); f.Reconcile();
		Assert.Single(f.Notifications(), x => x.DedupeKey == "continuity:owner:"+n.Id);
		Assert.Single(f.Notifications(), x => x.DedupeKey == "continuity:owner:"+n.Id+":manager");
		var next = f.Notifications().Single(x=>x.DedupeKey=="continuity:owner:"+n.Id);
		f.Deliver(next); f.Final(f.Worker); WorkContinuity.Finished(f.Rt,f.Worker.Id);
		for(int i=0;i<8;i++)f.Reconcile();
		Assert.Equal("stalled",f.Work(n.Id).State);
		Assert.Single(f.Notifications(), x=>x.DedupeKey=="continuity:manager:"+n.Id);
	}
	[Fact] public void AutomaticDependenciesGateDeliveryAndCompletionThenReconcileAfterAllPrerequisitesComplete() {
		using var f = new Fixture(); var b=f.Prerequisite(); var c=f.Prerequisite(); f.Depend(b);f.Depend(c); var n=f.Assign();
		Assert.Equal("wait_dependencies", f.Work(n.Id).State); Assert.False(WorkContinuity.CanDeliver(f.Rt,n));
		Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		Assert.Throws<DomainException>(()=>f.Complete(f.Objective));
		f.Complete(b);Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		f.Complete(c); f.Restart();Assert.Contains(f.Worker.Id,f.Reconcile());
		Assert.True(WorkContinuity.CanDeliver(f.Rt,n));Assert.Single(f.Notifications());f.Complete(f.Objective);
	}
	[Fact] public async Task DependencyCompletionNeverClearsExplicitBlockOrPause() {
		using var f=new Fixture();var b=f.Prerequisite();f.Depend(b);var n=f.Assign();
		await f.Disposition(n.Id,"blocked","operator maintenance","operator authorization");f.Complete(b);
		Assert.DoesNotContain(f.Worker.Id,f.Reconcile());Assert.Equal("blocked",f.Work(n.Id).State);
		await f.Disposition(n.Id,"owed"); f.Pause(); f.Restart();Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		Assert.Equal(AgentStates.Paused,f.Rt.Store.GetAgent(f.Worker.Id)!.State);Assert.Contains("paused",System.Text.Json.JsonSerializer.Serialize(WorkContinuity.Status(f.Rt,f.Project.Id)));
	}
	[Fact] public void UnknownSideEffectsBlockRecoveryAndEscalateOnceWithoutToolReplay() {
		using var f=new Fixture();var n=f.Assign();f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Worker.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=Clock.Now}));
		for(int i=0;i<6;i++)Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		Assert.Equal("blocked",f.Work(n.Id).State);Assert.Contains("UNKNOWN",f.Work(n.Id).Reason);
		Assert.Single(f.Notifications(),x=>x.DedupeKey=="continuity:manager:"+n.Id);
	}
	[Fact] public void ResultThenIndependentReviewThenReviewerResultCreatesDurableManagerBaton() {
		using var f=new Fixture();var n=f.Assign();
		var ctx=f.Context(f.Worker);ReviewHandoff.Send(ctx,f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"ready");
		Assert.Equal("completed",f.Work(n.Id).State);
		var review=f.Notifications().Single(x=>x.Type==NotificationTypes.Assignment&&x.ToAgentId==f.Reviewer.Id);
		Assert.Equal(f.Manager.Id,review.FromAgentId);Assert.Contains(f.Reviewer.Id,f.Reconcile());
		f.Deliver(review);f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Reviewer.Id,f.Manager.Id,"GO",f.Objective.Id)));
		Assert.Equal("completed",f.Work(review.Id).State);f.Restart();Assert.Contains(f.Manager.Id,f.Reconcile());
		Assert.Equal(2,f.Notifications().Count(x=>x.Type==NotificationTypes.Result));
	}
	[Fact] public async Task FinalExplicitWaitCancellationAndCompletionDoNotSpinOpenObjectives() {
		using var f=new Fixture();var n=f.Assign();f.Deliver(n);await f.Disposition(n.Id,"wait","external readiness not established","operator reports exact release identity");
		WorkContinuity.Finished(f.Rt,f.Worker.Id);f.Restart();for(int i=0;i<6;i++)Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		Assert.Single(f.Notifications());await f.Disposition(n.Id,"canceled","superseded");
		Assert.DoesNotContain(f.Worker.Id,f.Reconcile());Assert.Equal("canceled",f.Work(n.Id).State);
	}
	[Fact] public void MissingTargetDoesNotPreventOtherCommittedDispatch() {
		using var f=new Fixture();var n=f.Assign();f.Rt.Db.Write(u=>u.Execute("UPDATE work_obligations SET owner_id='missing' WHERE id=@id",new{id=n.Id}));
		f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"other ready",null)));
		Assert.Contains(f.Manager.Id,f.Reconcile());Assert.Equal("stalled",f.Work(n.Id).State);
	}
	[Fact] public void ReviewerOutsideReceivingManagerAuthorityCannotSelfDispatch() {
		using var f=new Fixture();f.Rt.Db.Write(u=>u.Execute("UPDATE agents SET manager_id=NULL WHERE id=@id",new{id=f.Reviewer.Id}));
		Assert.Throws<ToolException>(()=>ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"ready"));Assert.Empty(f.Notifications());
	}

	[Fact] public void Schema7UpgradeBackfillsPendingDispatchAndLegacyAmbiguityWithoutReplay() {
		using var f=new Fixture();var n=f.Assign();f.Deliver(n);
		var pending=f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"ready",f.Objective.Id)));
		f.Rt.Db.Write(u=>{u.Execute("DROP TABLE git_review_receipts; DROP TABLE git_registration_intents; DROP TABLE git_operations; DROP TABLE git_workspaces; DROP TABLE git_repositories; DROP TABLE maintenance_pending_calls; DROP TABLE maintenance_activities; DROP TABLE maintenance_holds; DROP TABLE maintenance_operations; DROP TABLE workbook_runs; DROP TABLE workbook_revisions; DROP TABLE workbook_cells; DROP TABLE workbooks; DROP TABLE work_obligations; DROP TABLE review_handoffs; PRAGMA user_version=7;");});
		// Exercise schema7 -> continuity9 -> workbook10 + maintenance11 + nativeGit12.
		f.Restart();Assert.Equal(14,f.Rt.Db.SchemaVersion);
		Assert.Empty(new Workbooks(f.Rt.Db).List(f.Project.Id));
		Assert.Equal("stalled",f.Work(n.Id).State);Assert.Equal("owed",f.Work(pending.Id).State);Assert.Equal(n.Id,f.Work(n.Id).OriginNotificationId);Assert.Equal(pending.Id,f.Work(pending.Id).OriginNotificationId);
		Assert.Contains(f.Manager.Id,f.Reconcile());Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		for(int i=0;i<5;i++)f.Reconcile();Assert.Single(f.Notifications(),x=>x.DedupeKey=="continuity:manager:"+n.Id);
		var books=new Workbooks(f.Rt.Db);var book=books.Create(f.Project.Id,"upgrade book");books.Add(f.Project.Id,book.Id,"powershell","'saved only'");
		f.Restart();Assert.Single(new Workbooks(f.Rt.Db).List(f.Project.Id));Assert.Equal("stalled",f.Work(n.Id).State);Assert.Equal("owed",f.Work(pending.Id).State);
	}
	[Fact] public async Task ActualSleepingHostDeliversOriginalAssignmentOnceAndHonorsDurableWait() {
		string? id=null;
		var provider=new FakeProvider((_,call)=>call==1?FakeProvider.Call("disposition_work",System.Text.Json.JsonSerializer.Serialize(new{notification_id=id,state="wait",reason="external readiness",resume_condition="operator provides ready result"})):FakeProvider.Text("waiting explicitly"));
		using var f=new Fixture(provider);var n=f.Assign();id=n.Id;
		f.Rt.Wake(f.Worker.Id);await Wait.Until(()=>f.Work(n.Id).State=="wait",TimeSpan.FromSeconds(10),"explicit durable wait");
		await Wait.Until(()=>f.Rt.GetHost(f.Worker.PrimarySessionId!)?.IsRunning==false,TimeSpan.FromSeconds(10),"host sleeping");
		for(int i=0;i<5;i++)f.Reconcile();Assert.Equal(2,provider.Requests.Count);
		Assert.Equal("delivered",f.Notifications().Single(x=>x.Id==n.Id).State);
		Assert.Single(f.Rt.Store.Items(f.Worker.PrimarySessionId!),x=>x.Kind==ItemKinds.User);
	}

	[Fact] public async Task AssignmentToolCommitsObjectiveInboxAndObligationAtomicallyAndDedupesSameInvocation() {
		using var f=new Fixture();f.Pause();var context=f.Context(f.Manager);
		var args=new JsonObject{["agent"]=f.Worker.Id,["title"]="new durable assignment",["message"]="work"};
		await new AssignWorkTool().InvokeAsync(context,args);await new AssignWorkTool().InvokeAsync(context,args);
		Assert.Single(f.Notifications());var n=f.Notifications()[0];Assert.Equal("owed",f.Work(n.Id).State);
		Assert.Equal(f.Worker.Id,f.Rt.Store.GetObjective(n.ObjectiveId!)!.OwnerId);
		var count=f.Rt.Store.ListObjectives(f.Project.Id).Count;
		var bad=f.Context(f.Manager);
		await Assert.ThrowsAsync<ToolException>(()=>new AssignWorkTool().InvokeAsync(bad,new JsonObject{["agent"]=f.Worker.Id,["title"]="rollback",["message"]=null}));
		Assert.Equal(count,f.Rt.Store.ListObjectives(f.Project.Id).Count);Assert.Single(f.Notifications());
	}
	[Fact] public void CompleteObjectiveDoesNotDiscardReadyResultOrIndependentManagerContinuation() {
		using var f=new Fixture();var n=f.Assign();f.Deliver(n);f.Complete(f.Objective);
		var result=f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"complete artifact awaits acceptance",f.Objective.Id)));
		f.Restart();Assert.Contains(f.Manager.Id,f.Reconcile());Assert.Equal("owed",f.Work(result.Id).State);
		Assert.True(WorkContinuity.CanDeliver(f.Rt,result));
	}
	[Fact] public async Task CompletedArtifactNamedReviewSurvivesRestartThenReviewerResultAndManagerAcceptance() {
		using var f=new Fixture();var implementation=f.Assign();f.Deliver(implementation);f.Complete(f.Objective);
		var ctx=f.Context(f.Worker);ReviewHandoff.Send(ctx,f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"completed artifact");
		var review=f.Notifications().Single(n=>n.Type==NotificationTypes.Assignment&&n.ToAgentId==f.Reviewer.Id);
		f.Restart();for(int i=0;i<4;i++)Assert.Contains(f.Reviewer.Id,f.Reconcile());
		Assert.Equal("completed",f.Work(implementation.Id).State);Assert.False(WorkContinuity.CanDeliver(f.Rt,implementation));
		Assert.Equal("owed",f.Work(review.Id).State);Assert.True(WorkContinuity.CanDeliver(f.Rt,review));
		ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"duplicate completed artifact");
		Assert.Single(f.Notifications(),n=>n.Type==NotificationTypes.Assignment&&n.ToAgentId==f.Reviewer.Id);
		f.Deliver(review);var outcome=f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Reviewer.Id,f.Manager.Id,"independent GO",f.Objective.Id)));
		f.Restart();Assert.Equal("completed",f.Work(review.Id).State);Assert.Contains(f.Manager.Id,f.Reconcile());Assert.True(WorkContinuity.CanDeliver(f.Rt,outcome));
		foreach(var ready in f.Notifications().Where(n=>n.ToAgentId==f.Manager.Id&&n.Type==NotificationTypes.Result)) {
			f.Deliver(ready);await new DispositionWorkTool().InvokeAsync(f.Context(f.Manager),new JsonObject{["notification_id"]=ready.Id,["state"]="completed",["reason"]="accepted independent reviewed artifact"});
		}
		WorkContinuity.Finished(f.Rt,f.Manager.Id);f.Restart();for(int i=0;i<4;i++)Assert.Empty(f.Reconcile());
	}
	[Fact] public void CompletedReviewHonorsPauseAndUnknownSideEffectFences() {
		using var f=new Fixture();f.Complete(f.Objective);ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"ready");
		var review=f.Notifications().Single(n=>n.Type==NotificationTypes.Assignment);f.Rt.Db.Write(u=>f.Rt.Store.SetAgentState(u,f.Reviewer.Id,AgentStates.Paused));
		f.Restart();Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.Equal("owed",f.Work(review.Id).State);
		f.Rt.Db.Write(u=>{f.Rt.Store.SetAgentState(u,f.Reviewer.Id,AgentStates.Sleeping);f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Reviewer.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=Clock.Now});});
		f.Restart();for(int i=0;i<4;i++)Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.Equal("blocked",f.Work(review.Id).State);Assert.False(WorkContinuity.CanDeliver(f.Rt,review));
		Assert.Single(f.Notifications(),n=>n.DedupeKey=="continuity:manager:"+review.Id);
	}
	[Theory][InlineData("objective-canceled")][InlineData("review-canceled")][InlineData("review-superseded")][InlineData("obligation-canceled")]
	public async Task CompletedReviewHonorsExplicitCancellationAndSupersession(string mode) {
		using var f=new Fixture();f.Complete(f.Objective);ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"ready");
		var review=f.Notifications().Single(n=>n.Type==NotificationTypes.Assignment);
		if(mode=="objective-canceled")f.Rt.Db.Write(u=>{var o=f.Rt.Store.GetObjective(u,f.Objective.Id)!;o.State=ObjectiveStates.Canceled;f.Rt.Store.UpdateObjective(u,o,null);});
		else if(mode=="obligation-canceled")await new DispositionWorkTool().InvokeAsync(f.Context(f.Manager),new JsonObject{["notification_id"]=review.Id,["state"]="canceled",["reason"]="superseded artifact"});
		else f.Rt.Db.Write(u=>u.Execute("UPDATE review_handoffs SET status=@status WHERE action_notification_id=@id",new{id=review.Id,status=mode=="review-canceled"?"canceled":"superseded"}));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,review));f.Restart();Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.Equal("canceled",f.Work(review.Id).State);
	}
	[Theory][InlineData("canceled")][InlineData("superseded")][InlineData("objective-canceled")][InlineData("obligation-canceled")]
	public async Task OriginalReviewCancellationFencesOwnerAndManagerContinuationsAcrossRestart(string mode) {
		using var f=new Fixture();f.Complete(f.Objective);ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"ready");
		var review=f.Notifications().Single(n=>n.Type==NotificationTypes.Assignment);f.Deliver(review);f.Final(f.Reviewer);f.Reconcile();
		var ownerContinuation=f.Notifications().Single(n=>n.DedupeKey=="continuity:owner:"+review.Id);
		f.Deliver(ownerContinuation);f.Final(f.Reviewer);f.Reconcile(); // force escalation up the manager chain too
		var derived=f.Notifications().Where(n=>n.DedupeKey?.StartsWith("continuity:")==true).ToList();Assert.True(derived.Count>=2);
		// Dispose the legitimate result separately; then prove unrelated accepted work still dispatches.
		foreach(var result in f.Notifications().Where(n=>n.Type==NotificationTypes.Result))await new DispositionWorkTool().InvokeAsync(f.Context(f.Manager),new JsonObject{["notification_id"]=result.Id,["state"]="completed",["reason"]="received artifact"});
		var unrelated=f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Escalation,f.Worker.Id,f.Manager.Id,"separate coordination",null)));
		if(mode=="objective-canceled")f.Rt.Db.Write(u=>{var o=f.Rt.Store.GetObjective(u,f.Objective.Id)!;o.State=ObjectiveStates.Canceled;f.Rt.Store.UpdateObjective(u,o,null);});
		else if(mode=="obligation-canceled")await new DispositionWorkTool().InvokeAsync(f.Context(f.Manager),new JsonObject{["notification_id"]=review.Id,["state"]="canceled",["reason"]="review explicitly withdrawn"});
		else f.Rt.Db.Write(u=>u.Execute("UPDATE review_handoffs SET status=@status WHERE action_notification_id=@id",new{id=review.Id,status=mode}));
		foreach(var n in derived)Assert.False(WorkContinuity.CanDeliver(f.Rt,n));
		f.Restart();for(int i=0;i<4;i++){var wakes=f.Reconcile();Assert.DoesNotContain(f.Reviewer.Id,wakes);Assert.Contains(f.Manager.Id,wakes);}
		Assert.Equal("canceled",f.Work(review.Id).State);Assert.True(WorkContinuity.CanDeliver(f.Rt,unrelated));
		Assert.Equal(derived.Count,f.Notifications().Count(n=>n.DedupeKey?.StartsWith("continuity:")==true));
		foreach(var n in derived){Assert.False(WorkContinuity.CanDeliver(f.Rt,n));if(n.ToAgentId==f.Manager.Id){var obligation=WorkContinuity.List(f.Rt,f.Project.Id).Single(w=>w.ActionNotificationId==n.Id);Assert.Equal("canceled",obligation.State);Assert.Equal(review.Id,obligation.OriginNotificationId);}}
	}
	[Theory][InlineData("paused")][InlineData("unknown")]
	public void ReviewContinuationPreservesPauseAndUnknownFences(string fence) {
		using var f=new Fixture();f.Complete(f.Objective);ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Name,"ready");
		var review=f.Notifications().Single(n=>n.Type==NotificationTypes.Assignment);f.Deliver(review);f.Final(f.Reviewer);f.Reconcile();
		var continuation=f.Notifications().Single(n=>n.DedupeKey=="continuity:owner:"+review.Id);Assert.Equal(review.Id,f.Work(review.Id).OriginNotificationId);
		f.Rt.Db.Write(u=>{if(fence=="paused")f.Rt.Store.SetAgentState(u,f.Reviewer.Id,AgentStates.Paused);else f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Reviewer.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=Clock.Now});});
		f.Restart();Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.Equal(review.Id,f.Work(review.Id).OriginNotificationId);
		if(fence=="unknown")Assert.False(WorkContinuity.CanDeliver(f.Rt,continuation));
	}

	[Fact] public void CompletedAssignmentWithForgedReviewTextHasNoReviewReceiptAndCannotReplay() {
		using var f=new Fixture();f.Complete(f.Objective);
		var n=f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Assignment,f.Manager.Id,f.Reviewer.Id,"Independent review requested",f.Objective.Id,dedupe:"review-action:forged")));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,n));f.Restart();Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.Equal("completed",f.Work(n.Id).State);
	}

	[Fact] public void ObjectiveBlockedReasonCannotBeRemovedByCompletingDependency() {
		using var f=new Fixture();var b=f.Prerequisite();f.Depend(b);var n=f.Assign();
		f.Rt.Db.Write(u=>{var o=f.Rt.Store.GetObjective(u,f.Objective.Id)!;o.State=ObjectiveStates.Blocked;f.Rt.Store.UpdateObjective(u,o,null);});
		f.Complete(b);f.Restart();Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		Assert.Equal("blocked",f.Work(n.Id).State);Assert.False(WorkContinuity.CanDeliver(f.Rt,n));
	}
	[Fact] public void CorruptSessionCheckpointFailsOneObligationVisiblyNotGlobalReconciliation() {
		using var f=new Fixture();var n=f.Assign();f.Deliver(n);f.Final(f.Worker);
		f.Rt.Db.Write(u=>u.Execute("UPDATE session_items SET payload='invalid' WHERE session_id=@id",new{id=f.Worker.PrimarySessionId}));
		f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"other ready",null)));
		Assert.Contains(f.Manager.Id,f.Reconcile());Assert.Equal("stalled",f.Work(n.Id).State);Assert.Contains("failed",f.Work(n.Id).Reason);
	}

	[Fact] public async Task HttpHealthAndWorkStatusExposeDrainReasonAndDependencyWaitWithoutFalseStall() {
		using var f=new Fixture();var prerequisite=f.Prerequisite();f.Depend(prerequisite);var n=f.Assign();
		var builder=WebApplication.CreateBuilder();builder.Logging.ClearProviders();builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(f.Rt);builder.Services.AddSingleton(new ServerOptions());builder.Services.AddSingleton<EventHub>();
		builder.Services.ConfigureHttpJsonOptions(o=>{o.SerializerOptions.PropertyNamingPolicy=JsonUtil.Options.PropertyNamingPolicy;o.SerializerOptions.DefaultIgnoreCondition=JsonUtil.Options.DefaultIgnoreCondition;});
		await using var app=builder.Build();Api.Map(app);await app.StartAsync();using var client=new HttpClient{BaseAddress=new Uri(app.Urls.Single())};
		async Task<JsonObject> Health(){var response=await client.GetAsync("/api/v1/health");return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();}
		var ready=await Health();Assert.True(ready["ready"]!.GetValue<bool>());Assert.Empty(ready["stuck"]!.AsArray());Assert.NotNull(ready["observed_at"]);
		f.Rt.Draining=true;var draining=await Health();Assert.False(draining["ready"]!.GetValue<bool>());Assert.Contains("draining",draining["readiness_reason"]!.GetValue<string>());f.Rt.Draining=false;
		var work=JsonNode.Parse(await client.GetStringAsync($"/api/v1/projects/{f.Project.Id}/work"))!.AsArray();
		var row=work.Single(x=>x!["id"]!.GetValue<string>()==n.Id)!;Assert.Equal("wait_dependencies",row["state"]!.GetValue<string>());Assert.Equal(f.Worker.Id,row["owner_id"]!.GetValue<string>());Assert.NotEmpty(row["resume_condition"]!.GetValue<string>());
	}

	[Fact] public void HistoricalUnknownDoesNotPoisonFreshDispatchButOriginalWorkStaysHeldAcrossRestartAndResult() {
		using var f=new Fixture();var original=f.Assign();f.AcceptedAt(original,100000);
		var invocation=new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Worker.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="powershell",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=100001,StartedAt=100001};
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,invocation));f.Reconcile();Assert.Equal("blocked",f.Work(original.Id).State);
		var fresh=f.Assign();f.AcceptedAt(fresh,100002);
		f.Restart();Assert.True(WorkContinuity.CanDeliver(f.Rt,fresh));Assert.Contains(f.Worker.Id,f.Reconcile());Assert.False(WorkContinuity.CanDeliver(f.Rt,original));
		f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"fresh coordination complete",f.Objective.Id)));
		Assert.Equal("blocked",f.Work(original.Id).State);Assert.Equal("completed",f.Work(fresh.Id).State);
		f.Complete(f.Objective);f.Reconcile();Assert.Equal("blocked",f.Work(original.Id).State);
		Assert.Equal(InvocationStates.Unknown,f.Rt.Store.GetInvocation(invocation.Id)!.State);
	}

	[Fact] public void InvocationQueuedBeforeDispatchButStartedAfterStillHoldsItsOverlappingWork() {
		using var f=new Fixture();var n=f.Assign();f.AcceptedAt(n,100000);
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Worker.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="write_file",State=InvocationStates.Unknown,CreatedAt=99990,StartedAt=100010}));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,n));f.Reconcile();Assert.Equal("blocked",f.Work(n.Id).State);
	}

	[Fact] public async Task UnrelatedResultCannotClearExplicitWaitOrBlockOnSameObjective() {
		using var f=new Fixture();var held=f.Assign();await f.Disposition(held.Id,"wait","external authorization","operator disposition exact action");
		f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"other increment complete",f.Objective.Id)));
		Assert.Equal("wait",f.Work(held.Id).State);Assert.False(WorkContinuity.CanDeliver(f.Rt,held));
	}

	[Fact] public void GeneratedManagerStallCannotCascadeToAnotherUpwardManagerLineage() {
		using var f=new Fixture();f.Rt.Db.Write(u=>u.Execute("UPDATE agents SET manager_id=@id WHERE id=@manager",new{id=f.Reviewer.Id,manager=f.Manager.Id}));
		var n=f.Assign();f.Deliver(n);f.Final(f.Worker);f.Reconcile();
		var next=f.Notifications().Single(x=>x.DedupeKey=="continuity:owner:"+n.Id);f.Deliver(next);f.Final(f.Worker);WorkContinuity.Finished(f.Rt,f.Worker.Id);f.Reconcile();
		var stall=f.Notifications().Single(x=>x.DedupeKey=="continuity:manager:"+n.Id);f.Deliver(stall);f.Final(f.Manager);f.Reconcile();
		var managerNext=f.Notifications().Single(x=>x.DedupeKey=="continuity:owner:"+stall.Id);f.Deliver(managerNext);f.Final(f.Manager);WorkContinuity.Finished(f.Rt,f.Manager.Id);
		for(int i=0;i<20;i++)f.Reconcile();
		Assert.Equal("stalled",f.Work(stall.Id).State);Assert.DoesNotContain(f.Notifications(),x=>x.DedupeKey=="continuity:manager:"+stall.Id);
	}

	[Theory]
	[InlineData("wait_outcome")][InlineData("owed")][InlineData("continue")][InlineData("stalled")]
	public void SubordinateResultCannotCompleteRecipientOverlappingUnknownInAnyActiveState(string state) {
		using var f=new Fixture();var assignment=f.Assign();f.AcceptedAt(assignment,100000);
		var managerWork=assignment.Id+":manager";
		f.Rt.Db.Write(u=>u.Execute("UPDATE work_obligations SET state=@state WHERE id=@id",new{state,id=managerWork}));
		var invocation=new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Manager.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="powershell",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=100001,StartedAt=null};
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,invocation));
		var before=JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id));
		f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"generic subordinate outcome",f.Objective.Id)));
		Assert.Equal(state,f.Work(managerWork).State);Assert.Equal("completed",f.Work(assignment.Id).State);
		f.Restart();Assert.Equal(state,f.Work(managerWork).State);f.Reconcile();Assert.Equal("blocked",f.Work(managerWork).State);
		Assert.Equal(before,JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id)));Assert.Empty(f.ProviderRequests());
	}

	[Theory]
	[InlineData(99999L,null,false)][InlineData(100000L,null,true)][InlineData(100001L,null,true)]
	[InlineData(99990L,100000L,true)][InlineData(100010L,99999L,false)][InlineData(99990L,100001L,true)]
	public void RecipientCausalGuardUsesStartedTimestampOrCreatedFallback(long created,long? started,bool held) {
		using var f=new Fixture();var assignment=f.Assign();f.AcceptedAt(assignment,100000);
		var invocation=new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Manager.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=created,StartedAt=started};
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,invocation));var before=JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id));
		f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(f.Project.Id,NotificationTypes.Result,f.Worker.Id,f.Manager.Id,"generic subordinate outcome",f.Objective.Id)));
		Assert.Equal(held?"wait_outcome":"completed",f.Work(assignment.Id+":manager").State);
		f.Restart();f.Reconcile();Assert.Equal(held?"blocked":"completed",f.Work(assignment.Id+":manager").State);
		Assert.Equal(before,JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id)));Assert.Empty(f.ProviderRequests());
	}

	[Theory]
	[InlineData("wait")][InlineData("blocked")][InlineData("wait_dependencies")][InlineData("wait_outcome")]
	public async Task RetiredOwnerPreservesDeliberateHoldsAcrossRestartReturnAndSuccessor(string state) {
		using var f=new Fixture();var prerequisite=f.Prerequisite();f.Depend(prerequisite);var n=f.Assign();
		if(state=="wait_outcome")f.Rt.Db.Write(u=>u.Execute("UPDATE work_obligations SET state='wait_outcome',reason='delegated',resume_condition='exact subordinate outcome' WHERE id=@id",new{id=n.Id}));
		else await f.Disposition(n.Id,state,"explicit operator hold UNKNOWN not reconciled","operator supplies exact release identity");
		var before=f.Work(n.Id);
		f.Rt.Db.Write(u=>{f.Rt.Store.SetAgentState(u,f.Worker.Id,AgentStates.Retired);u.Execute("UPDATE objectives SET owner_id=@owner WHERE id=@id",new{owner=f.Reviewer.Id,id=f.Objective.Id});});
		f.Restart();for(int i=0;i<8;i++){var wake=f.Reconcile();Assert.DoesNotContain(f.Worker.Id,wake);Assert.DoesNotContain(f.Reviewer.Id,wake);}
		var after=f.Work(n.Id);Assert.Equal(state,after.State);Assert.Equal(before.Reason,after.Reason);Assert.Equal(before.ResumeCondition,after.ResumeCondition);
		Assert.Equal(before.UpdatedAt,after.UpdatedAt);Assert.Equal(f.Worker.Id,after.OwnerId);Assert.Equal(n.Id,after.ActionNotificationId);
		Assert.Single(f.Notifications(),x=>x.DedupeKey=="continuity:manager:"+n.Id);
		Assert.Contains("Resume condition has not been evaluated",f.Notifications().Single(x=>x.DedupeKey=="continuity:manager:"+n.Id).Body);
		if(state is "wait" or "blocked") {
			f.Rt.Db.Write(u=>f.Rt.Store.SetAgentState(u,f.Worker.Id,AgentStates.Sleeping));f.Restart();
			for(int i=0;i<6;i++)Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
			Assert.Equal(state,f.Work(n.Id).State);Assert.Equal(before.Reason,f.Work(n.Id).Reason);Assert.False(WorkContinuity.CanDeliver(f.Rt,n));
		}
		Assert.Empty(f.ProviderRequests());
	}

	[Theory][InlineData(false)][InlineData(true)]
	public void UnknownRetiredOwnerNeverBecomesRunnableOrTransfersToObjectiveSuccessor(bool holdBeforeRetirement) {
		using var f=new Fixture();var n=f.Assign();f.AcceptedAt(n,100000);
		var invocation=new ToolInvocation{Id=Ids.New("inv"),SessionId=f.Worker.PrimarySessionId!,ProjectId=f.Project.Id,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=100001};
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,invocation));var before=JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id));
		if(holdBeforeRetirement)f.Reconcile();
		f.Rt.Db.Write(u=>{f.Rt.Store.SetAgentState(u,f.Worker.Id,AgentStates.Terminated);u.Execute("UPDATE objectives SET owner_id=@owner WHERE id=@id",new{owner=f.Reviewer.Id,id=f.Objective.Id});});
		f.Restart();for(int i=0;i<8;i++){var wake=f.Reconcile();Assert.DoesNotContain(f.Worker.Id,wake);Assert.DoesNotContain(f.Reviewer.Id,wake);}
		Assert.Equal("blocked",f.Work(n.Id).State);Assert.Contains("UNKNOWN",f.Work(n.Id).Reason);Assert.Equal(f.Worker.Id,f.Work(n.Id).OwnerId);
		f.Rt.Db.Write(u=>f.Rt.Store.SetAgentState(u,f.Worker.Id,AgentStates.Sleeping));f.Restart();for(int i=0;i<4;i++)Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		Assert.False(WorkContinuity.CanDeliver(f.Rt,n));Assert.Equal("blocked",f.Work(n.Id).State);
		Assert.Equal(before,JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id)));Assert.Empty(f.ProviderRequests());
		Assert.Single(f.Notifications(),x=>x.DedupeKey=="continuity:manager:"+n.Id);
	}

	[Theory][InlineData("wait")][InlineData("blocked")]
	public async Task RetiredOwnerCorruptObjectiveKeepsExplicitDispositionWithoutDispatch(string state) {
		using var f=new Fixture();var n=f.Assign();await f.Disposition(n.Id,state,"manual hold UNKNOWN","explicit release only");
		f.Rt.Db.Write(u=>{u.Execute("UPDATE work_obligations SET objective_id='missing-objective' WHERE id=@id",new{id=n.Id});f.Rt.Store.SetAgentState(u,f.Worker.Id,AgentStates.Retired);});
		var before=f.Work(n.Id);f.Restart();for(int i=0;i<6;i++)Assert.DoesNotContain(f.Worker.Id,f.Reconcile());
		var after=f.Work(n.Id);Assert.Equal(state,after.State);Assert.Equal(before.Reason,after.Reason);Assert.Equal(before.ResumeCondition,after.ResumeCondition);Assert.Equal(before.UpdatedAt,after.UpdatedAt);
		// Invalid linkage is not authority to clear the deliberate hold or dispatch work.
		Assert.Single(f.Notifications(),x=>x.DedupeKey=="continuity:manager:"+n.Id);Assert.False(WorkContinuity.CanDeliver(f.Rt,n));Assert.Empty(f.ProviderRequests());
	}

	[Theory][InlineData(AgentStates.Retired)][InlineData(AgentStates.Terminated)]
	public async Task CompletedArtifactRetiredReviewerHoldStaysHeldAndManagerGetsOneNotice(string ownerState) {
		using var f=new Fixture();f.Complete(f.Objective);
		ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Id,"completed exact-source artifact");
		var review=f.Notifications().Single(x=>x.Type==NotificationTypes.Assignment);
		await new DispositionWorkTool().InvokeAsync(f.Context(f.Reviewer),new JsonObject{["notification_id"]=review.Id,["state"]="wait",["reason"]="review requires operator evidence",["resume_condition"]="explicit evidence identity"});
		var before=f.Work(review.Id);f.Rt.Db.Write(u=>f.Rt.Store.SetAgentState(u,f.Reviewer.Id,ownerState));f.Restart();
		for(int i=0;i<8;i++)Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());
		Assert.Equal("wait",f.Work(review.Id).State);Assert.Equal(before.Reason,f.Work(review.Id).Reason);Assert.Equal(before.ResumeCondition,f.Work(review.Id).ResumeCondition);
		Assert.Single(f.Notifications(),x=>x.DedupeKey=="continuity:manager:"+review.Id);
		f.Rt.Db.Write(u=>f.Rt.Store.SetAgentState(u,f.Reviewer.Id,AgentStates.Sleeping));f.Restart();
		Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.False(WorkContinuity.CanDeliver(f.Rt,review));Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task AgedWaitDiagnosticsAreObservationalAndRevealRetiredOwnerWithoutInferringReadiness() {
		using var f=new Fixture();var n=f.Assign();await f.Disposition(n.Id,"wait","external authorization","free text appears ready; not evaluated");f.AcceptedAt(n,100000);
		f.Rt.Db.Write(u=>{f.Rt.Store.SetAgentState(u,f.Worker.Id,AgentStates.Retired);u.Execute("UPDATE objectives SET owner_id=@owner WHERE id=@id",new{owner=f.Reviewer.Id,id=f.Objective.Id});});
		var before=JsonUtil.Serialize(WorkContinuity.List(f.Rt,f.Project.Id));var notices=f.Notifications().Count;
		for(int i=0;i<4;i++) {
			var status=JsonNode.Parse(JsonUtil.Serialize(WorkContinuity.Status(f.Rt,f.Project.Id)))!.AsArray().Single(x=>x!["id"]!.GetValue<string>()==n.Id)!;
			Assert.Equal("wait",status["obligation_state"]!.GetValue<string>());Assert.False(status["owner_available"]!.GetValue<bool>());
			Assert.Equal(f.Reviewer.Id,status["objective_owner_id"]!.GetValue<string>());Assert.True(status["ownership_differs"]!.GetValue<bool>());
			Assert.Equal("owner_unavailable_manual_reconciliation",status["diagnostic"]!.GetValue<string>());Assert.False(status["resume_condition_evaluated"]!.GetValue<bool>());
			Assert.Equal("pending",status["action_state"]!.GetValue<string>());Assert.True(status["age_ms"]!.GetValue<long>()>0);
		}
		Assert.Equal(before,JsonUtil.Serialize(WorkContinuity.List(f.Rt,f.Project.Id)));Assert.Equal(notices,f.Notifications().Count);Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task DecisionOnlySyntheticHandoffCannotWakeOrDischargeButSupportedNamedResultDoes() {
		using var f=new Fixture();var assignment=f.Assign();f.Deliver(assignment);
		await new SendMessageTool().InvokeAsync(f.Context(f.Worker),new JsonObject{["to"]=f.Reviewer.Id,["type"]="decision",["objective_id"]=f.Objective.Id,["body"]="ready artifact; please review"});
		var decision=f.Notifications().Single(x=>x.Type==NotificationTypes.Decision);Assert.False(decision.Wakes);
		Assert.DoesNotContain(WorkContinuity.List(f.Rt,f.Project.Id),w=>w.Id==decision.Id);Assert.Equal("owed",f.Work(assignment.Id).State);
		f.Restart();Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.Empty(f.ProviderRequests());
		ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Id,"exact base/head/path plus deterministic test evidence");
		ReviewHandoff.Send(f.Context(f.Worker),f.Manager,NotificationTypes.Result,f.Objective.Id,f.Reviewer.Id,"duplicate cannot dispatch again");
		var review=Assert.Single(f.Notifications(),x=>x.ToAgentId==f.Reviewer.Id&&x.Type==NotificationTypes.Assignment);
		Assert.True(review.Wakes);Assert.Equal("completed",f.Work(assignment.Id).State);Assert.True(WorkContinuity.CanDeliver(f.Rt,review));
		Assert.Single(f.Notifications(),x=>x.Type==NotificationTypes.Result);Assert.Contains(f.Reviewer.Id,f.Reconcile());Assert.Equal(review.Id,f.Work(review.Id).ActionNotificationId);
	}

	sealed class Fixture:IDisposable {
		readonly TempHome Home=new();readonly FakeProvider Provider;
		public AinurRuntime Rt {get;private set;}
		public Project Project {get;} public Agent Manager {get;} public Agent Worker {get;} public Agent Reviewer {get;} public Objective Objective {get;}
		public Fixture(FakeProvider? provider=null){
			Provider=provider??new FakeProvider((_,_)=>FakeProvider.Text("offline"));
			Rt=Home.Runtime(Provider,start:false);Project=new(){Id=Ids.New("prj"),Name="continuity",CreatedAt=Clock.Now,UpdatedAt=Clock.Now};
			Manager=Agent("manager");Worker=Agent("worker",Manager.Id);Reviewer=Agent("reviewer",Manager.Id);
			Objective=new(){Id=Ids.New("obj"),ProjectId=Project.Id,OwnerId=Worker.Id,Title="accepted work",State=ObjectiveStates.Active,CreatedAt=Clock.Now,UpdatedAt=Clock.Now};
			Rt.Db.Write(u=>{Rt.Store.InsertProject(u,Project);foreach(var a in new[]{Manager,Worker,Reviewer}){Rt.Store.InsertAgent(u,a);Rt.Store.InsertSession(u,new Session{Id=a.PrimarySessionId!,ProjectId=Project.Id,AgentId=a.Id,Kind="primary",State="idle",ModelId=a.ModelId!,WorkspacePath=Home.Workspace,CreatedAt=Clock.Now});}Rt.Store.InsertObjective(u,Objective,null);});
		}
		Agent Agent(string name,string? manager=null)=>new(){Id=Ids.New("agt"),ProjectId=Project.Id,Name=name,ManagerId=manager,Role=manager is null?Roles.Manager:Roles.Specialist,State=AgentStates.Sleeping,ModelId="deepseek-v4.1-flash",PrimarySessionId=Ids.New("ses"),CreatedAt=Clock.Now,UpdatedAt=Clock.Now};
		public Notification Assign()=>Rt.Db.Write(u=>Rt.Store.InsertNotification(u,AinurRuntime.NewNotification(Project.Id,NotificationTypes.Assignment,Manager.Id,Worker.Id,"Deliver reviewed artifact",Objective.Id)));
		public void AcceptedAt(Notification n,long time)=>Rt.Db.Write(u=>{u.Execute("UPDATE notifications SET created_at=@time WHERE id=@id",new{id=n.Id,time});u.Execute("UPDATE work_obligations SET created_at=@time WHERE id=@id OR id=@manager",new{id=n.Id,manager=n.Id+":manager",time});});
		public System.Collections.IEnumerable ProviderRequests()=>Provider.Requests;
		public List<Notification> Notifications()=>Rt.Store.ListNotifications(Project.Id);
		public WorkObligation Work(string id)=>WorkContinuity.List(Rt,Project.Id).Single(w=>w.Id==id);
		public List<string> Reconcile(){var wakes=new List<string>();typeof(WorkContinuity).GetMethod("Reconcile",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[Rt,(Action<string>)wakes.Add]);return wakes;}
		public void Deliver(Notification n)=>Rt.Db.Write(u=>u.Execute("UPDATE notifications SET state='delivered',delivered_at=@now WHERE id=@id",new{id=n.Id,now=Clock.Now}));
		public void Final(Agent a)=>Rt.Db.Write(u=>Rt.Store.AppendItem(u,a.PrimarySessionId!,ItemKinds.Assistant,new AssistantPayload{Content="waiting"},4));
		public void Restart(){Rt.Dispose();Rt=Home.Runtime(Provider,start:false);}
		public Objective Prerequisite(){var o=new Objective{Id=Ids.New("obj"),ProjectId=Project.Id,OwnerId=Worker.Id,Title="prereq",State=ObjectiveStates.Active,CreatedAt=Clock.Now,UpdatedAt=Clock.Now};Rt.Db.Write(u=>Rt.Store.InsertObjective(u,o,null));return o;}
		public void Depend(Objective b)=>Rt.Db.Write(u=>Rt.Store.AddDependency(u,Objective.Id,b.Id,null));
		public void Complete(Objective o)=>Rt.Db.Write(u=>{var actual=Rt.Store.GetObjective(u,o.Id)!;actual.State=ObjectiveStates.Complete;actual.Evidence="[\"offline evidence\"]";Rt.Store.UpdateObjective(u,actual,null);});
		public void Pause()=>Rt.Db.Write(u=>Rt.Store.SetAgentState(u,Worker.Id,AgentStates.Paused));
		public ToolContext Context(Agent a)=>new(){Runtime=Rt,Project=Project,Agent=a,Session=Rt.Store.GetSession(a.PrimarySessionId!)!,InvocationId=Ids.New("inv"),Host=null!,CancellationToken=CancellationToken.None};
		public Task<ToolResult> Disposition(string id,string state,string reason="",string condition="")=>new DispositionWorkTool().InvokeAsync(Context(Worker),new JsonObject{["notification_id"]=id,["state"]=state,["reason"]=reason,["resume_condition"]=condition});
		public void Dispose(){Rt.Dispose();Home.Dispose();}
	}
}
