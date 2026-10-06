using Dapper;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Tools;

namespace Ainur.Core.Runtime;

/// <summary>Durable inbox obligations. Reconcile coordination only; never replay a tool or override pause.</summary>
public sealed class WorkObligation {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string? ObjectiveId { get; set; }
	public string OwnerId { get; set; } = "";
	public string? ManagerId { get; set; }
	public string State { get; set; } = "owed";
	public string Reason { get; set; } = "";
	public string ResumeCondition { get; set; } = "";
	public string ActionNotificationId { get; set; } = "";
	public string OriginNotificationId { get; set; } = "";
	public string? ContinuationId { get; set; }
	public long CreatedAt { get; set; }
	public long UpdatedAt { get; set; }
}

public static class WorkContinuity {
	// Stable origin, shared by initial dispatch and all generated owner/manager continuations.
	// Receipt/obligation provenance is persisted, never inferred from body, notification type or dedupe text.
	sealed class Purpose {
		public string? OriginType { get; set; }
		public string? ObjectiveState { get; set; }
		public string? ReviewStatus { get; set; }
		public string? NativeReviewStatus { get; set; }
		public string? OriginState { get; set; }
	}
	const string PurposeSql = """
		SELECT (SELECT type FROM notifications WHERE id=@origin) origin_type,
		(SELECT state FROM objectives WHERE id=@objective) objective_state,
		(SELECT status FROM review_handoffs WHERE action_notification_id=@origin) review_status,
		(SELECT state FROM work_obligations WHERE id=@origin) origin_state,
		""" + "(" + NativeGitReviewPurpose.StatusSql + ") native_review_status";
	static string? TerminalDisposition(WorkObligation w, Purpose p) {
		if(w.State == "blocked" && w.Reason == "Interrupted tool side effects UNKNOWN; no automatic replay") return null;
		if(p.OriginState == "canceled" || p.NativeReviewStatus is "canceled" or "superseded" || p.ReviewStatus is "canceled" or "superseded" ||
			p.OriginType == NotificationTypes.Assignment && p.ObjectiveState == ObjectiveStates.Canceled) return "canceled";
		if(p.NativeReviewStatus == "accepted") return "completed";
		if(p.OriginType == NotificationTypes.Assignment && p.ObjectiveState == ObjectiveStates.Complete && p.NativeReviewStatus != "active" && p.ReviewStatus is null && w.State != "wait_outcome") return "completed";
		return null;
	}
	public static void Accepted(Db.Unit u, Notification n) {
		if(!n.Wakes || n.Type is not (NotificationTypes.Assignment or NotificationTypes.Result or NotificationTypes.Escalation) || n.DedupeKey?.StartsWith("continuity:") == true) return;
		// Outcome is the durable baton from implementer/reviewer to manager; text/final alone is not.
		if(n.Type is NotificationTypes.Result or NotificationTypes.Escalation && n.FromAgentId is not null)
			u.Execute("""
				UPDATE work_obligations SET state='completed',reason='Outcome delivered to accountable owner',updated_at=@now
				WHERE owner_id=@sender AND manager_id=@recipient AND objective_id=@objective AND state NOT IN ('completed','canceled','blocked','wait','wait_dependencies')
				AND NOT EXISTS (SELECT 1 FROM tool_invocations i JOIN agents a ON a.id=work_obligations.owner_id
				 WHERE i.session_id=a.primary_session_id AND i.state='unknown' AND COALESCE(i.started_at,i.created_at)>=work_obligations.created_at)
				""", new { now = Clock.Now, sender = n.FromAgentId, recipient = n.ToAgentId, objective = n.ObjectiveId });
		if(n.Type is NotificationTypes.Result or NotificationTypes.Escalation && n.FromAgentId is not null)
			u.Execute("""
				UPDATE work_obligations SET state='completed',reason='Subordinate outcome durably delivered',updated_at=@now
				WHERE owner_id=@recipient AND objective_id=@objective AND state IN ('wait_outcome','continue','owed','stalled')
				AND action_notification_id IN (SELECT id FROM notifications WHERE type='assignment')
				AND action_notification_id IN (SELECT id FROM notifications WHERE to_agent_id=@sender)
				AND NOT EXISTS (SELECT 1 FROM tool_invocations i JOIN agents a ON a.id=work_obligations.owner_id
				 WHERE i.session_id=a.primary_session_id AND i.state='unknown' AND COALESCE(i.started_at,i.created_at)>=work_obligations.created_at)
				""", new { sender = n.FromAgentId, recipient = n.ToAgentId, objective = n.ObjectiveId, now = Clock.Now });
		var manager = u.Scalar<string>("SELECT manager_id FROM agents WHERE id=@id", new { id = n.ToAgentId });
		u.Execute("""
			INSERT OR IGNORE INTO work_obligations(id,project_id,objective_id,owner_id,manager_id,state,action_notification_id,origin_notification_id,created_at,updated_at)
			VALUES(@id,@project,@objective,@owner,@manager,'owed',@id,@id,@now,@now)
			""", new { id = n.Id, project = n.ProjectId, objective = n.ObjectiveId, owner = n.ToAgentId, manager, now = Clock.Now });
		if(n.Type == NotificationTypes.Assignment) {
			u.Execute("""
				UPDATE work_obligations SET state='wait_dependencies',reason='Declared prerequisites incomplete',resume_condition='All declared prerequisite objectives complete'
				WHERE id=@id AND EXISTS (SELECT 1 FROM objective_dependencies d LEFT JOIN objectives p ON p.id=d.depends_on_id WHERE d.objective_id=@objective AND (p.id IS NULL OR p.state<>'complete'))
				""", new { id = n.Id, objective = n.ObjectiveId });
			if(n.FromAgentId is not null) u.Execute("""
				INSERT OR IGNORE INTO work_obligations(id,project_id,objective_id,owner_id,manager_id,state,action_notification_id,origin_notification_id,reason,resume_condition,created_at,updated_at)
				SELECT @id,@project,@objective,@owner,manager_id,'wait_outcome',@action,@action,'Awaiting subordinate delivery','Subordinate result or explicit disposition',@now,@now FROM agents WHERE id=@owner
				""", new { id = n.Id + ":manager", project = n.ProjectId, objective = n.ObjectiveId, owner = n.FromAgentId, action = n.Id, now = Clock.Now });
		}
		u.Journal("work.accepted", n.ProjectId, "notification", n.Id, n.FromAgentId,
			new { owner_id = n.ToAgentId, objective_id = n.ObjectiveId, next_action = n.Type });
	}

	public static bool CanDeliver(AinurRuntime rt, Notification n) => rt.Db.Read(c => {
		var w = c.QuerySingleOrDefault<WorkObligation>("SELECT * FROM work_obligations WHERE id=@id OR action_notification_id=@id ORDER BY CASE WHEN id=@id THEN 0 ELSE 1 END,created_at LIMIT 1", new { id = n.Id });
		if(w is null) return true;
		if(w.ActionNotificationId != n.Id) return false;
		if(c.ExecuteScalar<long>(UnknownSql, new { owner = w.OwnerId, accepted = w.CreatedAt }) > 0) return false;
		var purpose = c.QuerySingle<Purpose>(PurposeSql, new { origin = w.OriginNotificationId, objective = w.ObjectiveId });
		if(TerminalDisposition(w, purpose) is not null || purpose.OriginType == NotificationTypes.Assignment && purpose.ObjectiveState == ObjectiveStates.Blocked) return false;
		if(w.State is "blocked" or "wait" or "wait_dependencies" or "wait_outcome" or "completed" or "canceled" or "stalled") return false;
		return purpose.OriginType != NotificationTypes.Assignment || c.ExecuteScalar<long>("SELECT COUNT(*) FROM objective_dependencies d LEFT JOIN objectives p ON p.id=d.depends_on_id WHERE d.objective_id=@id AND (p.id IS NULL OR p.state<>'complete')", new { id = w.ObjectiveId }) == 0;
	});

	// Historical UNKNOWN invocations are never replayed or resolved here. They cannot
	// poison a dispatch accepted after they began. For overlapping older work, absent
	// exact invocation-to-inbox provenance, conservatively retain the manual hold.
	const string UnknownSql = "SELECT COUNT(*) FROM tool_invocations WHERE session_id=(SELECT primary_session_id FROM agents WHERE id=@owner) AND state='unknown' AND COALESCE(started_at,created_at)>=@accepted";

	public static List<WorkObligation> List(AinurRuntime rt, string project) => rt.Db.Read(c =>
		Dapper.SqlMapper.Query<WorkObligation>(c, "SELECT * FROM work_obligations WHERE project_id=@project ORDER BY created_at", new { project }).ToList());

	// Observations only: age and free-text resume conditions never authorize dispatch or ownership transfer.
	public static object Status(AinurRuntime rt, string project) => List(rt, project).Select(w => {
		var owner = rt.Store.GetAgent(w.OwnerId);
		var pauseReason = rt.Db.Read(c => c.QueryFirstOrDefault<string>("SELECT reason FROM pause_requests WHERE target_session_id=@session AND state IN ('requested','acknowledged') ORDER BY created_at DESC LIMIT 1", new { session = owner?.PrimarySessionId }));
		var paused = owner?.State == AgentStates.Paused || owner?.PrimarySessionId is { } sid && rt.IsPaused(sid);
		var executing = owner?.PrimarySessionId is { } session && rt.CoordinationHost(session)?.IsRunning == true;
		var available = owner is not null && AgentStates.IsLive(owner.State) && owner.PrimarySessionId is not null;
		var actionState = rt.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM notifications WHERE id=@id", new { id = w.ActionNotificationId }));
		var unknown = rt.Db.Read(c => c.ExecuteScalar<long>(UnknownSql, new { owner = w.OwnerId, accepted = w.CreatedAt }));
		var objectiveOwner = w.ObjectiveId is null ? null : rt.Store.GetObjective(w.ObjectiveId)?.OwnerId;
		var diagnostic = w.State is "completed" or "canceled" ? "settled" : unknown > 0 ? "unknown_manual_hold" :
			!available ? "owner_unavailable_manual_reconciliation" : paused ? "intentional_pause" : w.State switch {
				"wait" => "intentional_wait", "blocked" => "explicit_block", "wait_dependencies" => "declared_dependencies",
				"wait_outcome" => "subordinate_outcome", "stalled" => "manual_disposition_required",
				_ => actionState is null ? "missing_action_manual_reconciliation" : executing ? "executing" :
					actionState == "pending" ? "pending_dispatch" : "consumed_coordination_owed"
			};
		return new { id = w.Id, objective_id = w.ObjectiveId, owner_id = w.OwnerId, manager_id = w.ManagerId,
			state = paused ? "paused" : executing && w.State == "owed" ? "executing" : w.State,
			obligation_state = w.State, owner_state = owner?.State, owner_available = available,
			objective_owner_id = objectiveOwner, ownership_differs = objectiveOwner is not null && objectiveOwner != w.OwnerId,
			diagnostic, action_state = actionState, overlapping_unknown_count = unknown,
			observed_at = Clock.Now, age_ms = Math.Max(0, Clock.Now - w.CreatedAt),
			unchanged_ms = Math.Max(0, Clock.Now - w.UpdatedAt), resume_condition_evaluated = false,
			next_action = w.ActionNotificationId, reason = paused ? pauseReason ?? "Agent intentionally paused" : w.Reason,
			resume_condition = paused ? "Explicit authorized pause release; never automatic" : w.ResumeCondition,
			continuation_id = w.ContinuationId };
	}).ToList();

	/// <summary>Final cannot discard an accepted actionable inbox obligation. One coordination continuation, then visible stall.</summary>
	public static void Finished(AinurRuntime rt, string owner) => rt.Db.Write(u => {
		foreach(var work in u.Query<WorkObligation>("SELECT * FROM work_obligations WHERE owner_id=@owner AND state='owed'", new { owner })) {
			var pending = u.Scalar<string>("SELECT state FROM notifications WHERE id=@id", new { id = work.ActionNotificationId });
			if(pending == "pending") continue; // accepted after this turn's snapshot, not yet consumed
			var delegated = u.Single<WorkObligation>("SELECT * FROM work_obligations WHERE owner_id=@owner AND objective_id=@objective AND state='wait_outcome' ORDER BY created_at DESC LIMIT 1", new { owner, objective = work.ObjectiveId });
			if(delegated is not null) {
				u.Execute("UPDATE work_obligations SET state='wait_outcome',action_notification_id=@action,origin_notification_id=@origin,reason='Review/delivery delegated; manager continuation remains owed',resume_condition='Subordinate outcome or explicit disposition',updated_at=@now WHERE id=@id", new { id = work.Id, action = delegated.ActionNotificationId, origin = delegated.OriginNotificationId, now = Clock.Now });
				continue;
			}
			u.Execute("UPDATE work_obligations SET state=@state,reason=@reason,updated_at=@now WHERE id=@id",
				new { id = work.Id, state = work.ContinuationId is null ? "continue" : "stalled",
					reason = "Turn ended without outcome or explicit disposition; coordination only, do not replay unknown operations", now = Clock.Now });
			u.Journal("work.continuation_owed", work.ProjectId, "notification", work.Id, owner);
		}
	});

	public static void Reconcile(AinurRuntime rt) => Reconcile(rt, rt.Wake);
	internal static void Reconcile(AinurRuntime rt, Action<string> activate) {
		if(!rt.Options.AutoStartHosts || rt.Draining || rt.Maintenance.Fenced) return;
		IDisposable admission;
		try { admission = rt.Maintenance.Admit("continuity"); } catch(MaintenanceAdmissionException) { return; }
		using var admissionLease = admission;
		// Every row commits independently. Corrupt/missing target must not prevent other coordination.
		var ids = rt.Db.Read(c => Dapper.SqlMapper.Query<string>(c,
			"SELECT id FROM work_obligations WHERE state NOT IN ('completed','canceled') ORDER BY created_at").ToList());
		foreach(var id in ids) {
			try {
				var target = rt.Db.Write(u => ReconcileOne(rt, u, id));
				if(target is not null) activate(target); // delivery activation only; no tool replay
			} catch(Exception e) {
				rt.Db.Write(u => {
					u.Execute("UPDATE work_obligations SET state='stalled',reason=@reason,resume_condition='Repair invalid coordination record explicitly',updated_at=@now WHERE id=@id AND state NOT IN ('blocked','wait','wait_dependencies','wait_outcome')", new { id, reason = "Coordination reconciliation failed: " + e.GetType().Name, now = Clock.Now });
					u.Journal("work.reconcile_failed", null, entityType: "notification", entityId: id, payload: new { error = e.GetType().Name });
				});
			}
		}
	}

	static string? ReconcileOne(AinurRuntime rt, Db.Unit u, string id) {
		var w = u.Single<WorkObligation>("SELECT * FROM work_obligations WHERE id=@id", new { id })!;
		var objective = w.ObjectiveId is null ? null : rt.Store.GetObjective(u, w.ObjectiveId);
		var actionType = u.Scalar<string>("SELECT type FROM notifications WHERE id=@id", new { id = w.ActionNotificationId });
		var purpose = u.Single<Purpose>(PurposeSql, new { origin = w.OriginNotificationId, objective = w.ObjectiveId })!;
		if(TerminalDisposition(w, purpose) is { } terminal) {
			Set(u, w, terminal, "Original accepted action explicitly terminal/canceled/superseded", ""); return null;
		}
		if(objective?.State == ObjectiveStates.Blocked && w.State is not ("wait_outcome" or "wait_dependencies" or "blocked" or "wait")) {
			Set(u, w, "blocked", "Objective explicitly blocked; dependency completion cannot clear unrelated blocker", "Owner must resolve objective blocker and disposition work explicitly");
		}
		var owner = rt.Store.GetAgent(u, w.OwnerId);
		if(owner is null || !AgentStates.IsLive(owner.State) || owner.PrimarySessionId is null) {
			// Retirement may transfer objectives, not accepted inbox work or tool side effects.
			// Keep deliberate dispositions byte-for-byte; no successor stealing or free-text evaluation.
			if(w.State is not ("blocked" or "wait" or "wait_dependencies" or "wait_outcome")) {
				if(u.Scalar<long>(UnknownSql, new { owner = w.OwnerId, accepted = w.CreatedAt }) > 0)
					Set(u, w, "blocked", "Interrupted tool side effects UNKNOWN; no automatic replay", "Owner must inspect unknown invocation and explicitly disposition work");
				else Set(u, w, "stalled", "Accountable owner missing/retired or lacks primary session", "Manager must choose a valid owner");
			}
			return Escalate(rt, u, w, "Accountable owner missing/retired or lacks primary session; objective successor is not an inbox-work successor");
		}
		if(w.State == "stalled" && w.Reason == "Accountable owner missing/retired or lacks primary session")
			Set(u, w, "owed", "Valid accountable owner/session now available; original dispatch restored", "");
		if(owner.State == AgentStates.Paused || u.Scalar<long>("""
			SELECT COUNT(*) FROM pause_requests WHERE target_session_id=@session AND state IN ('requested','acknowledged')
			""", new { session = owner.PrimarySessionId }) > 0) {
			// Preserve obligation state underneath the pause. Status computes actual pause reason separately.
			return null;
		}
		if(w.State is "blocked" or "wait") return null;
		if(w.State == "wait_outcome") {
			var child = u.Single<WorkObligation>("SELECT * FROM work_obligations WHERE id=@id", new { id = w.ActionNotificationId });
			if(child is null || child.State == "stalled" || child.ContinuationId is not null) {
				Set(u, w, "continue", "Manager continuation owed; accepted subordinate work ended without outcome", "");
			} else if(child.State is "completed" or "canceled") {
				Set(u, w, "continue", "Subordinate disposition recorded without delivery-owner outcome", "");
			} else return null;
		}
		if(purpose.OriginType == NotificationTypes.Assignment && w.State is not ("blocked" or "wait" or "wait_outcome") && w.ContinuationId is null) {
			var unmetNow = u.Scalar<long>("SELECT COUNT(*) FROM objective_dependencies d LEFT JOIN objectives p ON p.id=d.depends_on_id WHERE d.objective_id=@id AND (p.id IS NULL OR p.state<>'complete')", new { id = w.ObjectiveId });
			if(unmetNow > 0) Set(u, w, "wait_dependencies", "Declared prerequisites incomplete", "All declared prerequisites complete");
		}
		if(w.State == "wait_dependencies") {
			var unmet = u.Scalar<long>("""
				SELECT COUNT(*) FROM objective_dependencies d LEFT JOIN objectives o ON o.id=d.depends_on_id
				WHERE d.objective_id=@objective AND (o.id IS NULL OR o.state<>'complete')
				""", new { objective = w.ObjectiveId });
			if(unmet > 0) return null;
			if(objective?.State == ObjectiveStates.Blocked) {
				Set(u, w, "blocked", "Prerequisites complete but objective remains explicitly blocked", "Resolve remaining objective blocker and disposition work"); return null;
			}
			Set(u, w, "owed", "All declared prerequisites complete; accepted action runnable", "");
		}
		if(u.Scalar<long>(UnknownSql, new { owner = w.OwnerId, accepted = w.CreatedAt }) > 0) {
			Set(u, w, "blocked", "Interrupted tool side effects UNKNOWN; no automatic replay", "Owner must inspect unknown invocation and explicitly disposition work");
			return Escalate(rt, u, w);
		}
		if(w.State == "stalled") return Escalate(rt, u, w);
		var n = u.Single<Notification>("SELECT * FROM notifications WHERE id=@id", new { id = w.ActionNotificationId });
		if(n is null) { Set(u, w, "stalled", "Durable action notification missing", "Manager must disposition work"); return Escalate(rt, u, w); }
		var host = rt.CoordinationHost(owner.PrimarySessionId);
		if(host?.IsRunning == true) return null; // its normal inbox check sees new work; don't force an extra model turn
		var session = rt.Store.GetSession(u, owner.PrimarySessionId);
		if(session?.State == "paused") return null;
		if(n.State == "pending" && w.State != "continue") return owner.Id; // missed wake; same durable notification ID
		if(w.State == "owed") {
			// Consumed-but-not-completed work may be left at final after process death before Finished.
			var last = u.Single<SessionItem>("SELECT * FROM session_items WHERE session_id=@session ORDER BY seq DESC LIMIT 1", new { session = owner.PrimarySessionId });
			if(last is not null && (last.Kind != ItemKinds.Assistant || Ainur.Core.JsonUtil.Deserialize<Ainur.Core.Context.AssistantPayload>(last.Payload)?.ToolCalls.Count > 0)) return owner.Id; // resume checkpoint, not tool replay
			Set(u, w, w.ContinuationId is null ? "continue" : "stalled", "Consumed obligation lacks outcome/disposition after idle/restart", "");
		}
		if(w.State == "stalled") return Escalate(rt, u, w);
		if(w.State == "continue") {
			if(w.ContinuationId is not null) { Set(u, w, "stalled", "Continuation already delivered; explicit disposition required", "Owner or manager must disposition work"); return Escalate(rt, u, w); }
			var continuation = rt.Store.InsertNotification(u, AinurRuntime.NewNotification(w.ProjectId,
				NotificationTypes.Escalation, w.ManagerId, w.OwnerId,
				$"Durable coordination owed: {w.Id}, objective {w.ObjectiveId}. Give a supported result/handoff or explicit complete/canceled/blocked/wait disposition via disposition_work. Do NOT replay uncertain operations. Prior action: {n.Body}",
				w.ObjectiveId, w.Id, "continuity:owner:" + w.Id));
			u.Execute("UPDATE work_obligations SET state='owed',continuation_id=@next,action_notification_id=@next,updated_at=@now WHERE id=@id",
				new { id = w.Id, next = continuation.Id, now = Clock.Now });
			return owner.Id;
		}
		return null;
	}

	static void Set(Db.Unit u, WorkObligation w, string state, string reason, string condition) {
		w.State = state; w.Reason = reason; w.ResumeCondition = condition;
		u.Execute("UPDATE work_obligations SET state=@state,reason=@reason,resume_condition=@condition,updated_at=@now WHERE id=@id",
			new { id = w.Id, state, reason, condition, now = Clock.Now });
	}

	static string? Escalate(AinurRuntime rt, Db.Unit u, WorkObligation w, string? observation = null) {
		// Generated manager stall notices remain coordination obligations, not roots of
		// an unbounded fresh escalation lineage. Keep their eventual stall visible.
		if(u.Scalar<long>("SELECT COUNT(*) FROM notifications WHERE id=@id AND dedupe_key LIKE 'continuity:manager:%'", new { id = w.Id }) > 0) return null;
		var manager = w.ManagerId is null ? null : rt.Store.GetAgent(u, w.ManagerId);
		if(manager is null || !AgentStates.IsLive(manager.State) || manager.PrimarySessionId is null || manager.Id == w.OwnerId) return null;
		var key = "continuity:manager:" + w.Id;
		if(u.Scalar<long>("SELECT COUNT(*) FROM notifications WHERE dedupe_key=@key AND to_agent_id=@id", new { key, id = manager.Id }) > 0) return null;
		var n = rt.Store.InsertNotification(u, AinurRuntime.NewNotification(w.ProjectId, NotificationTypes.Escalation,
			w.OwnerId, manager.Id, $"Accountable coordination stall {w.Id}, objective {w.ObjectiveId}: {w.Reason}. Observation: {observation ?? "explicit coordination disposition required"}. Next action: {w.ResumeCondition}. Resume condition has not been evaluated. Explicitly disposition/reassign; never replay UNKNOWN operations.",
			w.ObjectiveId, w.Id, key));
		// The manager continuation is owed too, but bounded by the same one-continuation rule.
		u.Execute("""
			INSERT OR IGNORE INTO work_obligations(id,project_id,objective_id,owner_id,manager_id,state,action_notification_id,origin_notification_id,created_at,updated_at)
			SELECT @id,@project,@objective,@owner,manager_id,'owed',@id,@origin,@now,@now FROM agents WHERE id=@owner
			""", new { id = n.Id, project = w.ProjectId, objective = w.ObjectiveId, owner = manager.Id, origin = w.OriginNotificationId, now = Clock.Now });
		return manager.Id;
	}
}

public sealed class DispositionWorkTool : BuiltinTool {
 public override string Name => "disposition_work";
 public override IReadOnlyList<string> Tags => ["work", "handoff", "wait", "block"];
 public override string Description => "Disposition accepted work explicitly; wait/block need reason and resumption condition. Never resumes agents or retries tools.";
 public override System.Text.Json.Nodes.JsonObject InputSchema => Schema.Object(
        ("notification_id", Schema.String("Accepted assignment/result notification id."), true),
			("state", Schema.String("Disposition.", "completed", "canceled", "blocked", "wait", "wait_dependencies", "owed"), true),
			("reason", Schema.String("Reason; required for a wait/block."), false),
			("resume_condition", Schema.String("Concrete resumption condition for wait/block."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, System.Text.Json.Nodes.JsonObject args) {
		var id = Str(args, "notification_id"); var state = Str(args, "state");
		if(state is not ("completed" or "canceled" or "blocked" or "wait" or "wait_dependencies" or "owed")) throw new ToolException("Unsupported work disposition");
		var reason = OptStr(args, "reason") ?? ""; var condition = OptStr(args, "resume_condition") ?? "";
		if(state is "blocked" or "wait" or "wait_dependencies" && (string.IsNullOrWhiteSpace(reason) || string.IsNullOrWhiteSpace(condition)))
			throw new ToolException("A wait/block requires reason and resume_condition");
		ctx.Runtime.Db.Write(u => {
			var w = u.Single<WorkObligation>("SELECT * FROM work_obligations WHERE id=@id", new { id }) ?? throw new ToolException("Unknown work obligation");
			if(w.ProjectId != ctx.Project.Id || w.OwnerId != ctx.Agent.Id && !ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, w.OwnerId)) throw new ToolException("Outside work authority");
			if(state == "wait_dependencies" && (w.ObjectiveId is null || u.Scalar<long>("SELECT COUNT(*) FROM objective_dependencies WHERE objective_id=@id", new { id = w.ObjectiveId }) == 0)) throw new ToolException("No prerequisites to await");
			var nativeStatus = u.Scalar<string>(NativeGitReviewPurpose.StatusSql, new { origin = w.OriginNotificationId });
			if(nativeStatus is "canceled" or "superseded" or "accepted" && state is not ("canceled" or "completed")) throw new ToolException("Terminal native review purpose cannot be revived by work disposition");
			if(nativeStatus is "active" or "accepted" && state == "canceled")
				u.Execute("UPDATE git_review_receipts SET status='canceled',updated_at=@now WHERE action_notification_id=@id", new { id = w.OriginNotificationId, now = Clock.Now });
			u.Execute("UPDATE work_obligations SET state=@state,reason=@reason,resume_condition=@condition,updated_at=@now WHERE id=@id", new { id, state, reason, condition, now = Clock.Now });
			u.Journal("work.disposition", w.ProjectId, "notification", id, ctx.Agent.Id, new { state, reason, resume_condition = condition });
		});
		return Task.FromResult(ToolResult.Ok($"Work {id}: {state}", description: "work disposition"));
	}
}
