using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed class PauseRequest {
	public string Id { get; set; } = "";
	public string ProjectId { get; set; } = "";
	public string RequesterSessionId { get; set; } = "";
	public string RequesterAgentId { get; set; } = "";
	public string TargetSessionId { get; set; } = "";
	public string Scope { get; set; } = "";
	public string Reason { get; set; } = "";
	public string ReleaseCondition { get; set; } = "";
	public long Generation { get; set; }
	public string State { get; set; } = "requested";
	public long CreatedAt { get; set; }
	public long? AcknowledgedAt { get; set; }
	public long? ReleasedAt { get; set; }
	public long ExpiresAt { get; set; }
}

/// <summary>
/// Structured pause requests. A request becomes acknowledged only when the target session reaches a safe boundary;
/// it is released explicitly or when the requesting fork finishes, and expiry revokes it and starts recovery rather
/// than silently handing the scope to another writer.
/// </summary>
public sealed partial class AinurRuntime {
	public PauseRequest RequestPause(string requesterSessionId, string targetSessionId, string scope, string reason, string releaseCondition, TimeSpan ttl) {
		var requester = Store.GetSession(requesterSessionId) ?? throw new DomainException("Unknown requester session");
		var target = Store.GetSession(targetSessionId) ?? throw new DomainException("Unknown target session");
		if(target.State == "finished") throw new DomainException("The target session has finished");
		PauseRequest request;
		lock(AdmissionGate) {
			request = Db.Write(u => {
				var generation = (u.Scalar<long?>("SELECT MAX(generation) FROM pause_requests WHERE target_session_id=@targetSessionId", new { targetSessionId }) ?? 0) + 1;
				var r = new PauseRequest {
					Id = Ids.New("pse"), ProjectId = target.ProjectId, RequesterSessionId = requesterSessionId, RequesterAgentId = requester.AgentId,
					TargetSessionId = targetSessionId, Scope = scope, Reason = reason, ReleaseCondition = releaseCondition, Generation = generation,
					State = "requested", CreatedAt = Clock.Now, ExpiresAt = Clock.Now + (long) ttl.TotalMilliseconds,
				};
				u.Execute("""
					INSERT INTO pause_requests(id,project_id,requester_session_id,requester_agent_id,target_session_id,scope,reason,release_condition,generation,state,created_at,expires_at)
					VALUES(@Id,@ProjectId,@RequesterSessionId,@RequesterAgentId,@TargetSessionId,@Scope,@Reason,@ReleaseCondition,@Generation,@State,@CreatedAt,@ExpiresAt)
					""", r);
				u.Journal("pause.requested", r.ProjectId, "session", targetSessionId, requester.AgentId, new { r.Id, r.Scope, r.Reason, r.ReleaseCondition, r.Generation });
				return r;
			});
			GetHost(targetSessionId)?.InvalidateStep(); // also fences a request released before a late response arrives
		}
		// A target that is idle is already at a safe boundary.
		if(GetHost(targetSessionId) is { IsRunning: false }) AcknowledgePause(targetSessionId);
		return request;
	}

	public PauseRequest? ActivePause(string sessionId) => Db.Read(c => c.QueryFirstOrDefault<PauseRequest>(
		"SELECT * FROM pause_requests WHERE target_session_id=@sessionId AND state IN ('requested','acknowledged') AND expires_at > @now ORDER BY created_at LIMIT 1",
		new { sessionId, now = Clock.Now }));

	public PauseRequest? GetPause(string id) => Db.Read(c => c.QuerySingleOrDefault<PauseRequest>("SELECT * FROM pause_requests WHERE id=@id", new { id }));

	/// <summary>Called by the target's dispatcher at a safe boundary: records quiescence for outstanding requests.</summary>
	public void AcknowledgePause(string sessionId) => Db.Write(u => {
		foreach(var r in u.Query<PauseRequest>("SELECT * FROM pause_requests WHERE target_session_id=@sessionId AND state='requested'", new { sessionId })) {
			u.Execute("UPDATE pause_requests SET state='acknowledged', acknowledged_at=@now WHERE id=@Id", new { r.Id, now = Clock.Now });
			u.Journal("pause.acknowledged", r.ProjectId, "session", sessionId, r.RequesterAgentId, new { r.Id, r.Scope });
		}
	});

	public void ReleasePause(string id, string? byAgentId, string summary) {
		var r = GetPause(id) ?? throw new DomainException($"Unknown pause request {id}");
		if(r.State is "released" or "expired") return;
		var target = Store.GetSession(r.TargetSessionId)!;
		lock(AdmissionGate) {
			GetHost(r.TargetSessionId)?.InvalidateStep();
			Db.Write(u => {
				u.Execute("UPDATE pause_requests SET state='released', released_at=@now WHERE id=@id", new { id, now = Clock.Now });
				u.Journal("pause.released", r.ProjectId, "session", r.TargetSessionId, byAgentId, new { id, summary });
				if(target.Kind == "primary")
					Store.InsertNotification(u, NewNotification(r.ProjectId, NotificationTypes.Resume, r.RequesterAgentId, target.AgentId,
						$"Pause {id} on scope '{r.Scope}' was released. {summary}".Trim(), null, dedupe: $"resume:{id}"));
			});
		}
		GetHost(r.TargetSessionId)?.Wake();
	}

	public void ReleasePausesBy(string requesterSessionId, string summary) {
		var ids = Db.Read(c => c.Query<string>("SELECT id FROM pause_requests WHERE requester_session_id=@requesterSessionId AND state IN ('requested','acknowledged')", new { requesterSessionId }).AsList());
		foreach(var id in ids) ReleasePause(id, null, summary);
	}

	/// <summary>Expired claims are revoked and both sides are told so they can reconcile before relying on the scope.</summary>
	public void ExpirePauses() {
		var expired = Db.Read(c => c.Query<PauseRequest>("SELECT * FROM pause_requests WHERE state IN ('requested','acknowledged') AND expires_at <= @now", new { now = Clock.Now }).AsList());
		foreach(var r in expired) {
			var target = Store.GetSession(r.TargetSessionId);
			lock(AdmissionGate) {
				GetHost(r.TargetSessionId)?.InvalidateStep();
				Db.Write(u => {
					u.Execute("UPDATE pause_requests SET state='expired', released_at=@now WHERE id=@Id", new { r.Id, now = Clock.Now });
					u.Journal("pause.expired", r.ProjectId, "session", r.TargetSessionId, r.RequesterAgentId, new { r.Id, r.Scope });
					var text = $"Pause {r.Id} on scope '{r.Scope}' expired before it was released. Work in that scope may be in an uncertain state: reconcile with {AgentLabel(r.RequesterAgentId)} before relying on it.";
					if(target?.Kind == "primary")
						Store.InsertNotification(u, NewNotification(r.ProjectId, NotificationTypes.Resume, null, target.AgentId, text, null, dedupe: $"expired-target:{r.Id}"));
					var requester = Store.GetSession(r.RequesterSessionId);
					if(requester is { State: not "finished" })
						Store.AppendItem(u, requester.Id, ItemKinds.Notice, new Context.NoticePayload { Text = text }, Tokens.Estimate(text));
				});
			}
			GetHost(r.TargetSessionId)?.Wake();
			GetHost(r.RequesterSessionId)?.Wake();
		}
	}

	/// <summary>
	/// Tells the agent that requested an upgrade how it ended (on whichever runtime is now serving), so the requesting
	/// work resumes after the restart instead of waiting for an unrelated wake.
	/// </summary>
	public void ReportUpgradeOutcome(string attemptId, string state, string releaseId, string detail) {
		var origin = Db.Read(c => c.QueryFirstOrDefault<(string? AgentId, string? ProjectId)>(
			"SELECT agent_id, project_id FROM events WHERE kind='upgrade.requested' AND entity_id=@attemptId ORDER BY id LIMIT 1", new { attemptId }));
		Db.Write(u => {
			u.Execute("UPDATE upgrade_attempts SET state=@state, detail=@detail, updated_at=@now WHERE id=@attemptId", new { state, detail, attemptId, now = Clock.Now });
			u.Journal($"upgrade.outcome", origin.ProjectId, "upgrade_attempt", attemptId, origin.AgentId, new { state, release = releaseId, detail, generation = Generation });
		});
		if(origin.AgentId is null || origin.ProjectId is null) return;
		var text = state switch {
			"succeeded" => $"Upgrade {attemptId} succeeded: this runtime (generation {Generation}) is release {releaseId}. Continue the work that requested it, e.g. confirm the change on the running instance.",
			"rolled_back" => $"Upgrade {attemptId} to {releaseId} FAILED and was rolled back automatically: {detail}. The candidate is suppressed from reactivation; investigate and repair.",
			_ => $"Upgrade {attemptId} to {releaseId} ended as {state}: {detail}.",
		};
		Notify(origin.ProjectId, NotificationTypes.System, null, origin.AgentId, text, dedupe: $"upgrade-outcome:{attemptId}:{state}");
	}
}
