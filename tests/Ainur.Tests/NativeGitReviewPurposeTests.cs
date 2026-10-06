using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Dapper;
using Xunit;

namespace Ainur.Tests;

public sealed class NativeGitReviewPurposeTests {
	[Fact] public void CompiledSqlHashesMatchRawUtf8ProvenanceNotLexicalSourceTuples() {
		var migrationType=typeof(Db).Assembly.GetType("Ainur.Core.Persistence.Migrations")!;
		var all=(IReadOnlyList<(int Version,string Name,string Sql)>)migrationType.GetField("All",BindingFlags.Static|BindingFlags.Public)!.GetValue(null)!;
		foreach(var expected in new[]{(Version:13,Length:540,Hash:"7D50CE91A32A723EB21E76BA17269C2373A39834F1120A36A360F85FDB2DE09D"),(Version:14,Length:751,Hash:"A4BB1551D4F3D65E597EFD5F6658AA84D7CE077552BFBC851CFB6946B9DD19B8")}) {
			var bytes=System.Text.Encoding.UTF8.GetBytes(all.Single(m=>m.Version==expected.Version).Sql);
			Assert.Equal(expected.Length,bytes.Length);Assert.Equal(expected.Hash,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
		}
	}

	[Theory]
	[InlineData(false,false,"before_accept")][InlineData(true,false,"before_accept")]
	[InlineData(false,true,"before_accept")][InlineData(true,true,"before_accept")]
	[InlineData(false,false,"after_accept")][InlineData(true,false,"after_accept")]
	[InlineData(false,true,"after_accept")][InlineData(true,true,"after_accept")]
	[InlineData(false,false,"after_integrate")][InlineData(true,false,"after_integrate")]
	public async Task LateHistoricalUnknownFencesSuccessorAcceptanceAndNewIntentWithoutReplayingPriorEffect(bool differentReviewer,bool cancelOld,string discovery) {
		using var f=new Fixture();f.Complete();await f.Submit();var old=f.Action();var oldReviewer=f.Reviewer;
		f.Rt.Db.Write(u=>{
			u.Execute("UPDATE work_obligations SET created_at=1000 WHERE id=@id",new{id=old.Id});
			u.Execute("UPDATE notifications SET state='delivered',delivered_at=@now WHERE id=@id",new{id=old.Id,now=Clock.Now});
		});f.Reconcile();var continuation=f.Work(old.Id).ActionNotificationId;Assert.NotEqual(old.Id,continuation);
		var invocation=new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=oldReviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Running,CreatedAt=1001,StartedAt=1001};
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,invocation));
		if(cancelOld)await f.Disposition(old.Id,"canceled","cancel is not tool reconciliation","");
		if(differentReviewer)f.ChangeReviewer();
		f.EditAndCommit("successor while old call running");await f.Submit();var successor=f.Action();Assert.NotEqual(old.Id,successor.Id);
		Assert.True(f.Work(successor.Id).CreatedAt>invocation.StartedAt);Assert.Equal(cancelOld?"canceled":"superseded",f.Status(old));
		if(discovery!="before_accept")f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha);
		if(discovery=="after_integrate")f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"prior-applied");
		// UNKNOWN is learned only after B exists (and optionally was accepted/applied).
		f.Rt.Db.Write(u=>u.Execute("UPDATE tool_invocations SET state='unknown' WHERE id=@id",new{id=invocation.Id}));
		var unknown=JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id));
		var target=Fixture.Git(f.Project.WorkspacePath!,"rev-parse","HEAD");
		var workspace=f.Rt.Db.Read(c=>JsonUtil.Serialize(c.QuerySingle("SELECT * FROM git_workspaces WHERE id=@id",new{id=f.Workspace.Id})));
		var receipts=f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM git_review_receipts ORDER BY action_notification_id")));
		Assert.Throws<DomainException>(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha));
		Assert.Throws<DomainException>(()=>f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"late-unknown-new"));
		Assert.Equal(0,f.Rt.Db.Read(c=>c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_operations WHERE id='late-unknown-new'")));
		Assert.Equal(workspace,f.Rt.Db.Read(c=>JsonUtil.Serialize(c.QuerySingle("SELECT * FROM git_workspaces WHERE id=@id",new{id=f.Workspace.Id}))));
		Assert.Equal(receipts,f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM git_review_receipts ORDER BY action_notification_id"))));
		Assert.Equal(target,Fixture.Git(f.Project.WorkspacePath!,"rev-parse","HEAD"));
		if(discovery=="after_integrate") {
			Assert.Equal("integrated",f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"prior-applied").State);
			f.Rt.Db.Write(u=>u.Execute("UPDATE git_operations SET state='unknown' WHERE id='prior-applied'"));
			Assert.Throws<DomainException>(()=>f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"prior-applied"));
			Assert.Equal("integrated",f.Service.Reconcile(f.Project.Id,f.Author.Id,"prior-applied").State);
		}
		f.Restart();Assert.Throws<DomainException>(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha));
		Assert.Equal(unknown,JsonUtil.Serialize(f.Rt.Store.GetInvocation(invocation.Id)));Assert.Equal(target,Fixture.Git(f.Project.WorkspacePath!,"rev-parse","HEAD"));Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task CompletedArtifactHasExactPersistedPurposeAcrossRestartAndAcceptedDuplicateDoesNotWakeOrReset() {
		using var f=new Fixture();f.Complete();var requested=await f.Submit();var action=f.Action();
		Assert.Equal("active",f.Status(action));Assert.True(WorkContinuity.CanDeliver(f.Rt,action));f.Restart();
		f.Reconcile();Assert.Equal("owed",f.Work(action.Id).State);Assert.True(WorkContinuity.CanDeliver(f.Rt,action));
		await f.Submit();Assert.Single(f.Actions());Assert.Equal(action.Id,f.Work(action.Id).ActionNotificationId);
		var accepted=await Task.FromResult(f.Service.Accept(f.Project.Id,f.Reviewer.Id,requested.Id,f.Head,f.Workspace.BaseSha));
		await f.Submit();Assert.Equal("accepted",f.Status(action));Assert.Equal(accepted.AcceptanceHead,f.Service.Workspaces(f.Project.Id).Single(w=>w.Id==requested.Id).AcceptanceHead);
		Assert.Equal("integration_owed",f.Service.Workspaces(f.Project.Id).Single(w=>w.Id==requested.Id).State);Assert.Single(f.Actions());
		Assert.False(WorkContinuity.CanDeliver(f.Rt,action));f.Restart();f.Reconcile();Assert.Equal("completed",f.Work(action.Id).State);
		await Task.FromResult(f.Service.Accept(f.Project.Id,f.Reviewer.Id,requested.Id,f.Head,f.Workspace.BaseSha));Assert.Empty(f.ProviderRequests());
	}

	[Theory][InlineData("legacy")][InlineData("canceled")][InlineData("superseded")][InlineData("unknown")]
	public async Task NewIntegrationRejectsInvalidPurposeWithoutIntentOrTargetMutation(string condition) {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha);
		var before=Fixture.Git(f.Project.WorkspacePath!,"rev-parse","HEAD");
		if(condition=="legacy")f.Rt.Db.Write(u=>u.Execute("DELETE FROM git_review_receipts"));
		else if(condition=="canceled")await f.Disposition(action.Id,"canceled","review withdrawn","");
		else if(condition=="superseded")f.Rt.Db.Write(u=>u.Execute("UPDATE git_review_receipts SET status='superseded'"));
		else f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=f.Reviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=f.Work(action.Id).CreatedAt+1}));
		Assert.Throws<DomainException>(()=>f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"unsafe-new"));
		Assert.Equal(before,Fixture.Git(f.Project.WorkspacePath!,"rev-parse","HEAD"));
		Assert.Equal(0,f.Rt.Db.Read(c=>c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_operations WHERE id='unsafe-new'")));Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task ValidNewIntegrationAndPriorAppliedReadbackDoNotReplayAfterCancellation() {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha);
		f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"valid-new");var head=Fixture.Git(f.Project.WorkspacePath!,"rev-parse","HEAD");Assert.Equal(f.Head,head);
		await f.Disposition(action.Id,"canceled","cancel after applied","");
		Assert.Equal("integrated",f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"valid-new").State); // Prior success readback, never a new merge.
		Assert.Throws<DomainException>(()=>f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"fresh-after-cancel"));
		f.Rt.Db.Write(u=>u.Execute("UPDATE git_operations SET state='unknown' WHERE id='valid-new'"));
		Assert.Throws<DomainException>(()=>f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"valid-new"));
		Assert.Equal("integrated",f.Service.Reconcile(f.Project.Id,f.Author.Id,"valid-new").State);Assert.Equal(head,Fixture.Git(f.Project.WorkspacePath!,"rev-parse","HEAD"));Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task CanceledReviewUnknownCannotBeBypassedByFreshHeadOrReviewer() {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=f.Reviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=f.Work(action.Id).CreatedAt+1}));
		await f.Disposition(action.Id,"canceled","cancel does not reconcile UNKNOWN","");f.EditAndCommit("fresh after cancel");await Assert.ThrowsAsync<DomainException>(()=>f.Submit());Assert.Single(f.Actions());Assert.Equal("canceled",f.Status(action));Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task AcceptedReceiptUnknownCannotBeBypassedBySubmittingAnotherHead() {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha);
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=f.Reviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=f.Work(action.Id).CreatedAt+1}));
		f.EditAndCommit("new after accepted unknown");await Assert.ThrowsAsync<DomainException>(()=>f.Submit());Assert.Single(f.Actions());Assert.Equal("accepted",f.Status(action));Assert.Empty(f.ProviderRequests());
	}

	[Theory][InlineData("wakes")][InlineData("to_agent_id")][InlineData("objective_id")]
	public async Task OriginalActionIdentityMismatchCannotBecomeReviewPurpose(string column) {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();
		f.Rt.Db.Write(u=>u.Execute($"UPDATE notifications SET {column}=@value WHERE id=@id",new{id=action.Id,value=column=="wakes"?(object)0:column=="to_agent_id"?f.Author.Id:f.Project.RootObjectiveId}));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,action));await Assert.ThrowsAsync<DomainException>(()=>Task.Run(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha)));
		f.Restart();f.Reconcile();Assert.Equal("canceled",f.Work(action.Id).State);Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task PopulatedSchema12UpgradeKeepsLegacyHoldsUnknownAndAcceptanceWithoutInventingPurpose() {
		using var f=new Fixture();f.Complete();await f.Submit();var legacy=f.Action();f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha);
		// Model the pre14 database state directly, not a new disposition of settled14 purpose.
		f.Rt.Db.Write(u=>u.Execute("UPDATE work_obligations SET state='wait',reason='legacy manual UNKNOWN hold',resume_condition='exact operator release' WHERE id=@id",new{id=legacy.Id}));
		var inv=new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=f.Reviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=f.Work(legacy.Id).CreatedAt+1};
		f.Rt.Db.Write(u=>{f.Rt.Store.InsertInvocation(u,inv);u.Execute("DROP TABLE git_review_receipts; DROP TABLE git_registration_intents; PRAGMA user_version=12;");});
		var before=LegacyRows(f.Rt.Db);var eventLimit=f.Rt.Db.Read(c=>c.ExecuteScalar<long>("SELECT MAX(id) FROM events"));var events=f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM events WHERE id<=@eventLimit ORDER BY id",new{eventLimit})));f.Restart();Assert.Equal(14,f.Rt.Db.SchemaVersion);Assert.Equal(before,LegacyRows(f.Rt.Db));
		Assert.Equal(events,f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM events WHERE id<=@eventLimit ORDER BY id",new{eventLimit})))); // Startup may append audit events, never alter old ones.
		Assert.Equal(0,f.Rt.Db.Read(c=>c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_review_receipts")));Assert.Equal(0,f.Rt.Db.Read(c=>c.ExecuteScalar<int>("SELECT COUNT(*) FROM git_registration_intents")));
		Assert.Equal("wait",f.Work(legacy.Id).State);Assert.Equal(InvocationStates.Unknown,f.Rt.Store.GetInvocation(inv.Id)!.State);
		Assert.Throws<DomainException>(()=>f.Service.Integrate(f.Project.Id,f.Author.Id,f.Workspace.Id,"legacy-upgrade-new"));
		var beforeSubmit=f.Work(legacy.Id);await Assert.ThrowsAsync<DomainException>(()=>f.Submit());Assert.Empty(f.Actions());
		Assert.Equal(beforeSubmit.Reason,f.Work(legacy.Id).Reason);Assert.Equal(beforeSubmit.ResumeCondition,f.Work(legacy.Id).ResumeCondition);Assert.Empty(f.ProviderRequests());
	}

	[Fact] public void ActualSchema13UpgradePreservesUnresolvedRegistrationIntentAndKeepsConservativeMutationFence() {
		using var f=new Fixture();f.Rt.Db.Write(u=>u.Execute("UPDATE git_registration_intents SET state='unknown'; DROP TABLE git_review_receipts; PRAGMA user_version=13;"));
		var intent=f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM git_registration_intents ORDER BY id")));var legacy=LegacyRows(f.Rt.Db);
		f.Restart();Assert.Equal(14,f.Rt.Db.SchemaVersion);Assert.Equal(intent,f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM git_registration_intents ORDER BY id"))));Assert.Equal(legacy,LegacyRows(f.Rt.Db));
		Assert.Throws<DomainException>(()=>f.Service.Submit(f.Project.Id,f.Author.Id,f.Workspace.Id,f.Head,"inspection",f.Reviewer.Id));
		Assert.Empty(f.Actions());Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task PinnedLegacy12MigrationGuardAndReadQueriesLeaveOutstanding14ReceiptsUnchanged() {
		using var f=new Fixture();f.Complete();await f.Submit();var superseded=f.Action();f.EditAndCommit("second receipt");await f.Submit();var canceled=f.Action();await f.Disposition(canceled.Id,"canceled","operator cancel","");f.EditAndCommit("third receipt");await f.Submit();var action=f.Action();await f.Disposition(action.Id,"wait","intentional hold","operator evidence");
		Assert.Equal("superseded",f.Status(superseded));Assert.Equal("canceled",f.Status(canceled));
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=f.Reviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=f.Work(action.Id).CreatedAt+1}));
		var legacy=LegacyRows(f.Rt.Db);var schema=f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY name")));var receipts=f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM git_review_receipts ORDER BY action_notification_id")));
		f.Rt.Db.Read(c=>{PinnedSchema12MigrationGuard(c);return c.Query("SELECT w.id,w.base_sha,w.review_head,w.reviewer_id,w.acceptance_head,n.type,n.wakes FROM git_workspaces w JOIN notifications n ON n.objective_id=w.objective_id ORDER BY n.id").ToList();});
		Assert.Equal(14,f.Rt.Db.SchemaVersion);Assert.Equal(legacy,LegacyRows(f.Rt.Db));Assert.Equal(schema,f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY name"))));Assert.Equal(receipts,f.Rt.Db.Read(c=>JsonUtil.Serialize(c.Query("SELECT * FROM git_review_receipts ORDER BY action_notification_id"))));
		f.Restart();Assert.Equal("active",f.Status(action));Assert.Equal("wait",f.Work(action.Id).State);Assert.False(WorkContinuity.CanDeliver(f.Rt,action));Assert.Empty(f.ProviderRequests());
	}

	// Pinned legacy reader model: the historical early-return logic from Db.Migrate,
	// with latest=12. This is a non-destructive read/guard compatibility test, NOT authority
	// to run older mutators, infer review purpose, replay UNKNOWN, or a full-binary rollback test.
	static void PinnedSchema12MigrationGuard(Microsoft.Data.Sqlite.SqliteConnection c) {
		var current=c.ExecuteScalar<int>("PRAGMA user_version;");const int latest=12;
		if(current>latest)return;
		throw new InvalidOperationException("Fixture must be newer than pinned legacy reader");
	}
	static string LegacyRows(Db db)=>db.Read(c=>string.Join("\n",new[]{"projects","agents","sessions","objectives","notifications","work_obligations","tool_invocations","git_repositories","git_workspaces","git_operations"}.Select(table=>table+":"+JsonUtil.Serialize(c.Query($"SELECT * FROM {table} ORDER BY 1")))));

	[Fact] public async Task ConcurrentExactSubmitsCommitOnePurposeAndCanceledObjectiveRejectsWithoutNewAction() {
		using var f=new Fixture();f.Complete();var outcomes=await Task.WhenAll(Enumerable.Range(0,6).Select(async _=>{
			try{await f.Submit();return true;}catch(DomainException e){Assert.Contains("baton busy",e.Message);return false;}
		}));Assert.Contains(true,outcomes); // Contending requests may fail closed; never retry them here.
		Assert.Single(f.Actions());var action=f.Action();Assert.Equal("active",f.Status(action));
		f.Rt.Db.Write(u=>u.Execute("UPDATE objectives SET state='canceled' WHERE id=@id",new{id=f.Objective.Id}));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,action));await Assert.ThrowsAsync<DomainException>(()=>f.Submit());
		await Assert.ThrowsAsync<DomainException>(()=>Task.Run(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha)));
		f.Restart();f.Reconcile();Assert.Equal("canceled",f.Work(action.Id).State);Assert.Single(f.Actions());Assert.Empty(f.ProviderRequests());
	}

	[Theory][InlineData("wait")][InlineData("blocked")]
	public async Task DuplicateSubmissionKeepsDeliberateHoldAndUnknownIsNeverReplayed(string state) {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();
		await f.Disposition(action.Id,state,"UNKNOWN operator hold","exact inspection required");var before=f.Work(action.Id);
		var inv=new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=f.Reviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=before.CreatedAt+1};
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,inv));var record=JsonUtil.Serialize(f.Rt.Store.GetInvocation(inv.Id));
		await f.Submit();await Assert.ThrowsAsync<DomainException>(()=>Task.Run(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha)));
		f.Restart();for(int i=0;i<8;i++)Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());
		Assert.Equal(state,f.Work(action.Id).State);Assert.Equal(before.Reason,f.Work(action.Id).Reason);Assert.Equal(before.ResumeCondition,f.Work(action.Id).ResumeCondition);
		Assert.False(WorkContinuity.CanDeliver(f.Rt,action));Assert.Single(f.Actions());Assert.Equal(record,JsonUtil.Serialize(f.Rt.Store.GetInvocation(inv.Id)));Assert.Empty(f.ProviderRequests());
		f.EditAndCommit("new held identity");await Assert.ThrowsAsync<DomainException>(()=>f.Submit());Assert.Single(f.Actions());Assert.Equal("active",f.Status(action));
	}

	[Fact] public async Task NewlyObservedUnknownBlocksNativeAcceptanceAndContinuityWithoutInvocationMutation() {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();
		var inv=new ToolInvocation{Id=Ids.New("inv"),ProjectId=f.Project.Id,SessionId=f.Reviewer.PrimarySessionId!,ToolName="write_file",Arguments="{}",State=InvocationStates.Unknown,CreatedAt=f.Work(action.Id).CreatedAt+1};
		f.Rt.Db.Write(u=>f.Rt.Store.InsertInvocation(u,inv));var before=JsonUtil.Serialize(f.Rt.Store.GetInvocation(inv.Id));
		await Assert.ThrowsAsync<DomainException>(()=>Task.Run(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha)));
		f.Restart();for(int i=0;i<4;i++)Assert.DoesNotContain(f.Reviewer.Id,f.Reconcile());Assert.Equal("blocked",f.Work(action.Id).State);
		Assert.Equal(before,JsonUtil.Serialize(f.Rt.Store.GetInvocation(inv.Id)));Assert.False(WorkContinuity.CanDeliver(f.Rt,action));Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task SupersededHeadCancelsOriginalAndContinuationAndCannotBeAcceptedOrRevived() {
		using var f=new Fixture();f.Complete();await f.Submit();var old=f.Action();var oldHead=f.Head;
		f.Rt.Db.Write(u=>u.Execute("UPDATE notifications SET state='delivered',delivered_at=@now WHERE id=@id",new{id=old.Id,now=Clock.Now}));f.Reconcile();var continuation=f.Work(old.Id).ActionNotificationId;Assert.NotEqual(old.Id,continuation);
		f.EditAndCommit("second");await f.Submit();Assert.Equal(2,f.Actions().Count);Assert.Equal("superseded",f.Status(old));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,old));Assert.False(WorkContinuity.CanDeliver(f.Rt,f.Rt.Store.ListNotifications(f.Project.Id).Single(n=>n.Id==continuation)));
		await Assert.ThrowsAsync<DomainException>(()=>Task.Run(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,oldHead,f.Workspace.BaseSha)));
		await Assert.ThrowsAsync<ToolException>(()=>f.Disposition(old.Id,"owed","",""));
		// Returning the filesystem head to an old exact identity is not permission to revive its receipt.
		Fixture.Git(f.Workspace.Path,"checkout",oldHead);await Assert.ThrowsAsync<DomainException>(()=>f.Submit());
		f.Restart();f.Reconcile();Assert.Equal("canceled",f.Work(old.Id).State);Assert.Empty(f.ProviderRequests());
	}

	[Theory][InlineData(false)][InlineData(true)]
	public async Task CanceledActionOrAbandonedWorkspaceCannotBeAcceptedOrRequeued(bool abandon) {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();
		if(abandon)f.Service.Abandon(f.Project.Id,f.Author.Id,f.Workspace.Id);else await f.Disposition(action.Id,"canceled","no GO","");
		Assert.Equal("canceled",f.Status(action));Assert.False(WorkContinuity.CanDeliver(f.Rt,action));
		await Assert.ThrowsAsync<DomainException>(()=>Task.Run(()=>f.Service.Accept(f.Project.Id,f.Reviewer.Id,f.Workspace.Id,f.Head,f.Workspace.BaseSha)));
		await Assert.ThrowsAsync<DomainException>(()=>f.Submit());await Assert.ThrowsAsync<ToolException>(()=>f.Disposition(action.Id,"owed","",""));
		f.Restart();f.Reconcile();Assert.Equal("canceled",f.Work(action.Id).State);Assert.Single(f.Actions());Assert.Empty(f.ProviderRequests());
	}

	[Theory][InlineData("review_head")][InlineData("base_sha")][InlineData("reviewer_id")][InlineData("objective_id")][InlineData("state")]
	public async Task ReceiptMismatchNeverGrantsCompletedArtifactPurpose(string column) {
		using var f=new Fixture();f.Complete();await f.Submit();var action=f.Action();
		f.Rt.Db.Write(u=>u.Execute($"UPDATE git_workspaces SET {column}=@value WHERE id=@id",new{id=f.Workspace.Id,value=column=="reviewer_id"?f.Author.Id:column=="objective_id"?f.Project.RootObjectiveId:column=="state"?"abandoned":"other-sha"}));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,action));f.Restart();f.Reconcile();Assert.Equal("canceled",f.Work(action.Id).State);Assert.Empty(f.ProviderRequests());
	}

	[Fact] public async Task LegacyTextNeverBackfillsPurposeButNewExactSupportedSubmitCreatesStructuredReceipt() {
		using var f=new Fixture();f.Complete();
		var legacy=f.Rt.Db.Write(u=>f.Rt.Store.InsertNotification(u,new Notification{Id=Ids.New("ntf"),ProjectId=f.Project.Id,ObjectiveId=f.Objective.Id,FromAgentId=f.Author.Id,ToAgentId=f.Reviewer.Id,Type=NotificationTypes.Assignment,Wakes=true,State="pending",DedupeKey=$"git-review:{f.Workspace.Id}:{f.Head}:{f.Reviewer.Id}",Body=$"Independent exact Git review base {f.Workspace.BaseSha} head {f.Head}",CreatedAt=Clock.Now}));
		Assert.False(WorkContinuity.CanDeliver(f.Rt,legacy));f.Reconcile();Assert.Equal("completed",f.Work(legacy.Id).State);Assert.Null(f.Status(legacy));
		await f.Submit();var action=f.Action();Assert.NotEqual(legacy.Id,action.Id);Assert.Equal("active",f.Status(action));Assert.True(WorkContinuity.CanDeliver(f.Rt,action));Assert.Empty(f.ProviderRequests());
	}

	sealed class Fixture:IDisposable {
		readonly TempHome home=new();readonly FakeProvider provider=new((_,_)=>throw new Exception("No provider permitted"));
		public AinurRuntime Rt=null!;public NativeGit Service=>new(Rt);public Project Project=null!;public Agent Author=null!,Reviewer=null!;public Objective Objective=null!;public GitWorkspace Workspace=null!;public string Head=>Git(Workspace.Path,"rev-parse","HEAD");
		public Fixture(){Rt=home.Runtime(provider,start:false);var repo=home.Workspace;Project=Rt.CreateProject("receipt","",repo);Git(repo,"init","-b","main");File.WriteAllText(Path.Combine(repo,"f"),"base");Git(repo,"add","f");Git(repo,"-c","user.name=Tests","-c","user.email=tests@example.invalid","commit","-m","base");
			Rt.Db.Write(u=>{Author=Agent("author");Reviewer=Agent("reviewer");Rt.Store.InsertAgent(u,Author);Rt.Store.InsertAgent(u,Reviewer);Rt.Store.InsertSession(u,new Session{Id=Author.PrimarySessionId!,ProjectId=Project.Id,AgentId=Author.Id,CreatedAt=Clock.Now});Rt.Store.InsertSession(u,new Session{Id=Reviewer.PrimarySessionId!,ProjectId=Project.Id,AgentId=Reviewer.Id,CreatedAt=Clock.Now});Objective=new Objective{Id=Ids.New("obj"),ProjectId=Project.Id,ParentId=Project.RootObjectiveId,OwnerId=Author.Id,State=ObjectiveStates.Active,Title="artifact",Evidence="exact tests",CreatedAt=Clock.Now,UpdatedAt=Clock.Now};Rt.Store.InsertObjective(u,Objective,Author.Id);});
			var registration=Service.Register(Project.Id,"human",repo,"main",Author.Id,operationId:"register-purpose-fixture");Workspace=Service.Create(Project.Id,Author.Id,registration.Id,Objective.Id,"create");EditAndCommit("first");
		}
		Agent Agent(string name)=>new(){Id=Ids.New("agt"),ProjectId=Project.Id,ManagerId=Project.RootAgentId,Name=name,ModelId="deepseek-v4.1-flash",State=AgentStates.Sleeping,PrimarySessionId=Ids.New("ses"),CreatedAt=Clock.Now};
		public void ChangeReviewer()=>Rt.Db.Write(u=>{Reviewer=Agent("successor-reviewer");Rt.Store.InsertAgent(u,Reviewer);Rt.Store.InsertSession(u,new Session{Id=Reviewer.PrimarySessionId!,ProjectId=Project.Id,AgentId=Reviewer.Id,CreatedAt=Clock.Now});});
		public void Complete()=>Rt.Db.Write(u=>u.Execute("UPDATE objectives SET state='complete' WHERE id=@id",new{id=Objective.Id}));
		public Task<GitWorkspace> Submit()=>Task.Run(()=>Service.Submit(Project.Id,Author.Id,Workspace.Id,Head,"exact tests",Reviewer.Id));
		public void EditAndCommit(string text){File.WriteAllText(Path.Combine(Workspace.Path,"f"),text);Git(Workspace.Path,"add","f");Git(Workspace.Path,"-c","user.name=Tests","-c","user.email=tests@example.invalid","commit","-m",text);}
		public List<Notification> Actions()=>Rt.Db.Read(c=>c.Query<Notification>("SELECT n.* FROM notifications n JOIN git_review_receipts r ON r.action_notification_id=n.id ORDER BY n.created_at").ToList());
		public Notification Action()=>Actions().Last();public string? Status(Notification n)=>Rt.Db.Read(c=>c.ExecuteScalar<string>("SELECT status FROM git_review_receipts WHERE action_notification_id=@id",new{id=n.Id}));
		public WorkObligation Work(string id)=>WorkContinuity.List(Rt,Project.Id).Single(w=>w.Id==id);
		public Task<ToolResult> Disposition(string id,string state,string reason,string condition)=>new DispositionWorkTool().InvokeAsync(new ToolContext{Runtime=Rt,Project=Project,Agent=Reviewer,Session=Rt.Store.GetSession(Reviewer.PrimarySessionId!)!,InvocationId=Ids.New("inv"),Host=null!,CancellationToken=CancellationToken.None},new JsonObject{["notification_id"]=id,["state"]=state,["reason"]=reason,["resume_condition"]=condition});
		public System.Collections.IEnumerable ProviderRequests()=>provider.Requests;
		public List<string> Reconcile(){var wakes=new List<string>();typeof(WorkContinuity).GetMethod("Reconcile",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[Rt,(Action<string>)wakes.Add]);return wakes;}
		public void Restart(){Rt.Dispose();Rt=home.Runtime(provider,start:false);}
		public void Dispose(){Rt.Dispose();home.Dispose();}
		public static string Git(string dir,params string[] args){var psi=new ProcessStartInfo("git"){WorkingDirectory=dir,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var arg in args)psi.ArgumentList.Add(arg);using var process=Process.Start(psi)!;var output=process.StandardOutput.ReadToEnd();var error=process.StandardError.ReadToEnd();if(!process.WaitForExit(20000))throw new TimeoutException("test git deadline");Assert.True(process.ExitCode==0,error);return output.Trim();}
	}
}
