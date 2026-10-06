using Ainur.Core.Model;
using Ainur.Core.Persistence;

namespace Ainur.Core.Runtime;

/// <summary>Structured action provenance only. Legacy notification text is never proof of review purpose.</summary>
internal static class NativeGitReviewPurpose {
	internal sealed class Receipt {
		public string ActionNotificationId { get; set; } = "";
		public string Status { get; set; } = "";
	}
	// Also used by continuity for original actions and generated owner/manager continuations.
	// A receipt with any identity/lifecycle mismatch is terminal, not an ordinary assignment.
	internal const string StatusSql = """
		SELECT CASE WHEN r.status IN ('canceled','superseded') THEN r.status
		 WHEN EXISTS (SELECT 1 FROM work_obligations b WHERE b.id=r.action_notification_id AND b.state='canceled') THEN 'canceled'
		 WHEN w.id IS NULL OR n.id IS NULL OR o.id IS NULL OR o.state='canceled'
		 OR r.project_id<>w.project_id OR r.project_id<>o.project_id OR r.objective_id<>w.objective_id
		 OR r.base_sha<>w.base_sha OR r.head_sha IS NOT w.review_head OR r.reviewer_id IS NOT w.reviewer_id
		 OR r.project_id<>n.project_id OR r.objective_id IS NOT n.objective_id OR r.reviewer_id<>n.to_agent_id
		 OR n.type<>'assignment' OR n.wakes<>1 OR w.retention<>'retained'
		 OR (r.status='active' AND w.state<>'review_pending')
		 OR (r.status='accepted' AND (w.state NOT IN ('integration_owed','integrated')
		   OR w.acceptance_head IS NOT r.head_sha OR w.acceptance_base IS NOT r.base_sha OR (w.acceptance_actor IS NOT r.reviewer_id AND w.acceptance_actor IS NOT 'human')))
		 THEN 'superseded' ELSE r.status END
		 FROM git_review_receipts r LEFT JOIN git_workspaces w ON w.id=r.workspace_id
		 LEFT JOIN notifications n ON n.id=r.action_notification_id LEFT JOIN objectives o ON o.id=r.objective_id
		 WHERE r.action_notification_id=@origin
		""";

	internal static Receipt? Prior(Db.Unit u, GitWorkspace w, string head, string reviewer) => u.Single<Receipt>("""
		SELECT action_notification_id,status FROM git_review_receipts
		WHERE workspace_id=@id AND base_sha=@baseSha AND head_sha=@head AND reviewer_id=@reviewer
		""", new { id = w.Id, baseSha = w.BaseSha, head, reviewer });

	internal static bool Current(Db.Unit u, Receipt r) => u.Scalar<string>(StatusSql, new { origin = r.ActionNotificationId }) == r.Status &&
		u.Scalar<long>("SELECT COUNT(*) FROM work_obligations WHERE origin_notification_id=@id AND id=@id AND state='canceled'", new { id = r.ActionNotificationId }) == 0;

	internal static bool Held(Db.Unit u, Receipt r) => u.Scalar<long>("""
		SELECT COUNT(*) FROM work_obligations b JOIN agents a ON a.id=b.owner_id
		WHERE b.id=@id AND (b.state IN ('blocked','wait','wait_dependencies','wait_outcome','stalled')
		 OR EXISTS(SELECT 1 FROM tool_invocations i JOIN sessions s ON s.id=i.session_id
		 WHERE s.agent_id=a.id AND i.state='unknown' AND COALESCE(i.started_at,i.created_at)>=b.created_at))
		""", new { id = r.ActionNotificationId }) > 0;

	// Cancellation/supersession do not reconcile tool effects. A fresh purpose identity
	// may not move its timestamp forward past UNKNOWN attached to earlier review work.
	internal static bool UnknownHistory(Db.Unit u, string workspace) => u.Scalar<long>("""
		SELECT COUNT(*) FROM git_review_receipts r JOIN work_obligations b ON b.id=r.action_notification_id
		WHERE r.workspace_id=@workspace AND EXISTS(SELECT 1 FROM tool_invocations i JOIN sessions s ON s.id=i.session_id
		 WHERE s.agent_id=b.owner_id AND i.state='unknown' AND COALESCE(i.started_at,i.created_at)>=b.created_at)
		""", new { workspace }) > 0;

	// Legacy purpose is unprovable, but exact objective/reviewer work holds are observable.
	// Fail closed rather than creating a fresh action timestamp that bypasses earlier UNKNOWN.
	internal static bool LegacyHeld(Db.Unit u, GitWorkspace w) => u.Scalar<long>("""
		SELECT COUNT(*) FROM work_obligations b WHERE b.objective_id=@objective AND b.owner_id=@reviewer
		AND b.state NOT IN ('completed','canceled') AND (b.state IN ('blocked','wait','wait_dependencies','wait_outcome','stalled')
		 OR EXISTS(SELECT 1 FROM tool_invocations i JOIN sessions s ON s.id=i.session_id
		 WHERE s.agent_id=b.owner_id AND i.state='unknown' AND COALESCE(i.started_at,i.created_at)>=b.created_at))
		""", new { objective = w.ObjectiveId, reviewer = w.ReviewerId }) > 0;

	// Transactional point-of-intent authority check for NEW integration operations only.
	internal static void RequireAccepted(Db.Unit u, GitWorkspace w, string head) {
		var receipt = Prior(u, w, head, w.ReviewerId ?? "");
		if(receipt is null || receipt.Status != "accepted" || !Current(u, receipt) || Held(u, receipt) || UnknownHistory(u, w.Id))
			throw new DomainException("NEW integration requires current exact accepted review purpose without historical UNKNOWN; legacy/canceled/superseded/held authority is insufficient.");
	}

	internal static void Close(Db.Unit u, string workspace, string status) => u.Execute("""
		UPDATE git_review_receipts SET status=@status,updated_at=@now
		WHERE workspace_id=@workspace AND status IN ('active','accepted')
		""", new { workspace, status, now = Clock.Now });

	internal static Notification Create(AinurRuntime rt, Db.Unit u, GitWorkspace w, string actor, string head, string reviewer, string evidence) {
		Close(u, w.Id, "superseded");
		var id = Ids.New("ntf");
		var n = rt.Store.InsertNotification(u, new Notification { Id = id, ProjectId = w.ProjectId, Type = NotificationTypes.Assignment,
			FromAgentId = actor == "human" ? null : actor, ToAgentId = reviewer, ObjectiveId = w.ObjectiveId, Wakes = true, State = "pending",
			DedupeKey = "git-review-purpose:" + id,
			Body = $"Independent exact Git review {w.Id}: base {w.BaseSha}, head {head}. Inspect git_workspace before accepting. Evidence: {evidence}. No self acceptance or auto-integration.", CreatedAt = Clock.Now });
		u.Execute("""
			INSERT INTO git_review_receipts(action_notification_id,workspace_id,project_id,objective_id,base_sha,head_sha,reviewer_id,status,created_at,updated_at)
			VALUES(@action,@workspace,@project,@objective,@baseSha,@head,@reviewer,'active',@now,@now)
			""", new { action = n.Id, workspace = w.Id, project = w.ProjectId, objective = w.ObjectiveId, baseSha = w.BaseSha, head, reviewer, now = Clock.Now });
		return n;
	}
}
