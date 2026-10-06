using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Dapper;

namespace Ainur.Core.Tools;

/// <summary>A named, one-shot result-to-review baton. The existing inbox/SessionHost is its dispatcher.</summary>
public static class ReviewHandoff {
	public static IReadOnlyList<ReviewHandoffView> ForObjective(AinurRuntime rt, string objectiveId) =>
		rt.Db.Read(c => c.Query<ReviewHandoffView>(
			"SELECT reviewer_key, status, action_notification_id FROM review_handoffs WHERE objective_id=@objectiveId ORDER BY created_at",
			new { objectiveId }).ToList());

	public static ToolResult Send(ToolContext ctx, Agent manager, string type, string? objectiveId, string reviewerName, string body) {
		if(type != NotificationTypes.Result) throw new ToolException("A named reviewer is only valid on a result message.");
		if(objectiveId is null) throw new ToolException("Named review handoff requires objective_id.");
		var rt = ctx.Runtime;
		var objective = rt.Store.GetObjective(objectiveId) ?? throw new ToolException("Review objective not found.");
		if(objective.ProjectId != ctx.Project.Id || objective.OwnerId != ctx.Agent.Id)
			throw new ToolException("Named review requires an objective owned by the sender in this project.");
		if(ctx.Agent.ManagerId != manager.Id) throw new ToolException("Review result must be addressed to the sender's manager.");
		if(string.IsNullOrWhiteSpace(reviewerName)) throw new ToolException("Reviewer name is required.");
		var candidates = rt.Store.ListAgents(ctx.Project.Id).Where(a => a.Id == reviewerName || a.Name.Equals(reviewerName, StringComparison.OrdinalIgnoreCase)).ToList();
		if(candidates.Count > 1) throw new ToolException("Reviewer name is ambiguous; use agent id.");
		var reviewer = candidates.SingleOrDefault();
		if(reviewer?.Id == ctx.Agent.Id || reviewer?.Id == manager.Id) throw new ToolException("Review must be assigned to an independent agent.");
		if(reviewer is not null && !rt.Store.IsInSubtree(manager.Id, reviewer.Id)) throw new ToolException("Reviewer must be in the receiving manager's reporting subtree.");
		var reviewerKey = reviewer?.Id ?? reviewerName.Trim().ToLowerInvariant();
		var session = reviewer?.PrimarySessionId is { } sid ? rt.Store.GetSession(sid) : null;
		var available = reviewer is not null && AgentStates.IsLive(reviewer.State) && reviewer.State != AgentStates.Paused && session is not null && session.State != "finished" && !rt.IsPaused(session.Id);
		var paused = reviewer?.State == AgentStates.Paused || session?.State == "paused" || session is not null && rt.IsPaused(session.Id);
		var status = available ? "queued" : paused ? "paused" : "stalled";
		var actionRecipient = available ? reviewer!.Id : manager.Id;
		var actionType = available ? NotificationTypes.Assignment : NotificationTypes.Escalation;
		var summary = available
			? $"Independent review requested for {objectiveId} ({objective.Title}). Result from {ctx.Agent.Name}: {body}"
			: $"Review handoff {objectiveId} to {reviewerName} cannot dispatch ({(paused ? "reviewer paused; operator action required" : "reviewer missing or without runnable primary session")}). Assign an available independent reviewer; do not auto-resume a paused agent. Result: {body}";
		var (existing, result, action, durableStatus, created) = rt.Store.Db.Write(u => {
			var prior = u.Single<ExistingHandoff>(
				"SELECT id, status, result_notification_id, action_notification_id FROM review_handoffs WHERE objective_id=@objectiveId AND reviewer_key=@reviewerKey",
				new { objectiveId, reviewerKey });
			if(prior is not null) return (prior.Id, prior.ResultNotificationId, prior.ActionNotificationId, prior.Status, false);
			var result = rt.Store.InsertNotification(u, new Notification { Id = Ids.New("ntf"), ProjectId = ctx.Project.Id,
				Type = NotificationTypes.Result, FromAgentId = ctx.Agent.Id, ToAgentId = manager.Id,
				ObjectiveId = objectiveId, Body = body, Wakes = true, State = "pending",
				DedupeKey = $"review-result:{objectiveId}:{reviewerKey}", CreatedAt = Clock.Now });
			var action = rt.Store.InsertNotification(u, new Notification { Id = Ids.New("ntf"), ProjectId = ctx.Project.Id,
				Type = actionType, FromAgentId = available ? manager.Id : ctx.Agent.Id, ToAgentId = actionRecipient,
				ObjectiveId = objectiveId, Body = summary, Wakes = true, State = "pending",
				DedupeKey = $"review-action:{objectiveId}:{reviewerKey}", CreatedAt = Clock.Now });
			var id = Ids.New("rvw");
			u.Execute("""
				INSERT INTO review_handoffs (id, project_id, objective_id, sender_id, reviewer_key, reviewer_id, manager_id,
				 result_notification_id, action_notification_id, status, created_at)
				VALUES (@id,@projectId,@objectiveId,@senderId,@reviewerKey,@reviewerId,@managerId,@resultId,@actionId,@status,@createdAt)
				""", new { id, projectId = ctx.Project.Id, objectiveId, senderId = ctx.Agent.Id, reviewerKey,
					reviewerId = reviewer?.Id, managerId = manager.Id, resultId = result.Id, actionId = action.Id, status, createdAt = Clock.Now });
			return (id, result.Id, action.Id, status, true);
		});
		// Commit first; these are hints only. Durable pending inbox is scanned by SessionHost on startup.
		if(created && rt.Generation > 0) {
			rt.Wake(manager.Id);
			if(durableStatus == "queued" && reviewer is not null) rt.Wake(reviewer.Id);
		}
		return ToolResult.Ok($"Review handoff {existing}: {durableStatus}. Result {result}; action {action}. " +
			(durableStatus == "queued" ? $"Reviewer {reviewer?.Name ?? reviewerKey} has a durable assignment." : "Manager has one durable actionable escalation."));
	}

	public sealed class ReviewHandoffView {
		public string ReviewerKey { get; set; } = "";
		public string Status { get; set; } = "";
		public string ActionNotificationId { get; set; } = "";
	}

	sealed class ExistingHandoff {
		public string Id { get; set; } = "";
		public string Status { get; set; } = "";
		public string ResultNotificationId { get; set; } = "";
		public string ActionNotificationId { get; set; } = "";
	}
}
