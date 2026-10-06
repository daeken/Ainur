using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed class MaintenanceOperation {
	public string Id { get; set; } = "";
	public string State { get; set; } = "collecting";
	public string Reason { get; set; } = "";
	public long CreatedAt { get; set; }
	public long DeadlineAt { get; set; }
	public long Generation { get; set; }
	public long? ReleasedAt { get; set; }
}
public sealed class MaintenanceBlocker {
	public string Kind { get; set; } = "";
	public string Id { get; set; } = "";
	public string? SessionId { get; set; }
	public string State { get; set; } = "";
}
public sealed record MaintenanceHold(string SessionId, string State, long? AcknowledgedAt, long Generation);
public sealed record MaintenanceStatus(MaintenanceOperation? Operation, bool AdmissionFenced, bool VerifiedQuiescent,
	bool DeadlineElapsed, IReadOnlyList<MaintenanceHold> Holds, IReadOnlyList<MaintenanceBlocker> Blockers);
public sealed class MaintenanceAdmissionException() : DomainException("Service maintenance fences new ordinary work; retained work resumes after release.");

internal interface IUnknownMutationLease : IDisposable { void MarkUnknown(); }

/// <summary>One durable service-wide fence. All admission shares the runtime's existing gate. No replay or automatic uncertainty release.</summary>
public sealed class MaintenanceCoordinator(AinurRuntime runtime) {
	readonly AinurRuntime rt = runtime;
	public bool Fenced => rt.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_operations WHERE released_at IS NULL")) != 0;
	public bool HasUnknown(string sessionId) => rt.Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE session_id=@sessionId AND state='unknown'", new { sessionId })) != 0;
	public IDisposable Admit(string kind, string? sessionId = null, string? referenceId = null) {
		lock(rt.AdmissionGate) {
			if(Fenced || sessionId is not null && HasUnknown(sessionId)) throw new MaintenanceAdmissionException();
			var id = Ids.New("maintact");
			rt.Db.Write(u => u.Execute("INSERT INTO maintenance_activities(id,kind,reference_id,session_id,generation,state,started_at) VALUES(@id,@kind,@referenceId,@sessionId,@generation,'running',@now)",
				new { id, kind, referenceId, sessionId, generation = rt.Generation, now = Clock.Now }));
			return new Lease(this, id);
		}
	}
	sealed class Lease(MaintenanceCoordinator owner, string id) : IUnknownMutationLease {
		bool disposed;
		public void MarkUnknown() {
			lock(owner.rt.AdmissionGate) {
				if(disposed) return;
				owner.rt.Db.Write(u => u.Execute("UPDATE maintenance_activities SET state='unknown' WHERE id=@id AND state='running'", new { id }));
				owner.Refresh();
			}
		}
		public void Dispose() {
			lock(owner.rt.AdmissionGate) {
				if(disposed) return;
				disposed = true;
				owner.rt.Db.Write(u => u.Execute("UPDATE maintenance_activities SET state='completed',finished_at=@now WHERE id=@id AND state='running'", new { id, now = Clock.Now }));
				owner.Refresh();
			}
		}
	}
	public MaintenanceStatus Begin(string reason, TimeSpan timeout) {
		if(string.IsNullOrWhiteSpace(reason) || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromDays(1)) throw new DomainException("Maintenance needs a reason and deadline from 1ms to 24h.");
		lock(rt.AdmissionGate) {
			if(Fenced) throw new DomainException("Maintenance is already active.");
			rt.Db.Write(u => {
				var id = Ids.New("maint");
				u.Execute("INSERT INTO maintenance_operations(id,state,reason,created_at,deadline_at,generation) VALUES(@id,'collecting',@reason,@now,@deadline,@generation)", new { id, reason, now = Clock.Now, deadline = Clock.Now + (long)timeout.TotalMilliseconds, generation = rt.Generation });
				u.Journal("maintenance.begun", null, "maintenance", id, payload: new { reason, generation = rt.Generation });
			});
			return Status();
		}
	}
	void Refresh() {
		var op = rt.Db.Read(c => c.QuerySingleOrDefault<MaintenanceOperation>("SELECT * FROM maintenance_operations WHERE released_at IS NULL"));
		if(op is null) return;
		// Only the actual loop/lease exit or an unstarted host can attest a checkpoint. State/health/pause are not proof.
		rt.Db.Write(u => {
			foreach(var s in u.Query<Session>("SELECT * FROM sessions WHERE state<>'finished' OR id IN (SELECT session_id FROM maintenance_holds WHERE operation_id=@op)", new { op = op.Id })) {
				var host = rt.CoordinationHost(s.Id);
				var unknown = u.Scalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE session_id=@id AND state='unknown'", new { id = s.Id }) > 0 ||
					u.Scalar<int>("SELECT COUNT(*) FROM tool_invocations WHERE session_id=@id AND state='unknown'", new { id = s.Id }) > 0 ||
					u.Scalar<int>("SELECT COUNT(*) FROM model_requests WHERE session_id=@id AND state='unknown'", new { id = s.Id }) > 0;
				var busy = host is not null && (host.IsRunning || !host.AtBoundary) ||
					u.Scalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE session_id=@id AND state='running'", new { id = s.Id }) > 0;
				var state = unknown ? "unresolved" : busy ? "running_to_checkpoint" : "held";
				u.Execute("INSERT INTO maintenance_holds(operation_id,session_id,state,acknowledged_at,generation) VALUES(@op,@sid,@state,@ack,@generation) ON CONFLICT(operation_id,session_id) DO UPDATE SET state=@state,acknowledged_at=@ack,generation=@generation",
					new { op = op.Id, sid = s.Id, state, ack = state == "held" ? (long?)Clock.Now : null, generation = rt.Generation });
			}
		});
	}
	public MaintenanceStatus Status() {
		lock(rt.AdmissionGate) {
			Refresh();
			var op = rt.Db.Read(c => c.QuerySingleOrDefault<MaintenanceOperation>("SELECT * FROM maintenance_operations WHERE released_at IS NULL"));
			if(op is null) return new(null, false, false, false, [], []);
			var holds = rt.Db.Read(c => c.Query<MaintenanceHold>("SELECT session_id,state,acknowledged_at,generation FROM maintenance_holds WHERE operation_id=@id ORDER BY session_id", new { id = op.Id }).AsList());
			var blockers = rt.Db.Read(c => c.Query<MaintenanceBlocker>("""
				SELECT kind,CAST(COALESCE(reference_id,id) AS TEXT) AS id,session_id,state FROM maintenance_activities WHERE state IN ('running','unknown')
				UNION ALL SELECT 'tool_invocation',id,session_id,state FROM tool_invocations WHERE state IN ('running','queued','unknown')
				UNION ALL SELECT 'model_request',id,session_id,state FROM model_requests WHERE state IN ('dispatched','unknown')
				UNION ALL SELECT 'workbook_run',r.id,NULL,r.status FROM workbook_runs r WHERE r.status='unknown'
				UNION ALL SELECT 'git_registration_intent',id,NULL,state FROM git_registration_intents WHERE state IN ('running','unknown')
				""").AsList());
			var verified = holds.All(h => h.State == "held") && blockers.Count == 0;
			return new(op, true, verified, Clock.Now >= op.DeadlineAt, holds, blockers);
		}
	}
	/// <summary>Attest a boundary for an externally supervised replacement. This does not stop or replace the process.</summary>
	public MaintenanceStatus PrepareHandoff(string id) {
		lock(rt.AdmissionGate) {
			var status = Status();
			if(status.Operation?.Id != id || !status.VerifiedQuiescent) throw new DomainException("Maintenance has not reached a verified service-wide boundary.");
			rt.Db.Write(u => { u.Execute("UPDATE maintenance_operations SET state='handoff_ready' WHERE id=@id", new { id }); u.Journal("maintenance.handoff_ready", null, "maintenance", id); });
			return Status();
		}
	}
	public void Release(string id, bool abort) {
		lock(rt.AdmissionGate) {
			var status = Status();
			if(status.Operation?.Id != id) throw new DomainException("Not the active maintenance operation.");
			if(!abort && !status.VerifiedQuiescent) throw new DomainException("Use abort to release an incomplete maintenance operation; uncertain actions are never replayed.");
			rt.Db.Write(u => {
				u.Execute("UPDATE maintenance_operations SET state=@state,released_at=@now WHERE id=@id", new { id, state = abort ? "aborted" : "released", now = Clock.Now });
				u.Execute("UPDATE maintenance_holds SET state='released' WHERE operation_id=@id AND state='held'", new { id });
				u.Journal("maintenance.released", null, "maintenance", id, payload: new { abort });
			});
		}
		// Existing pause, budget and continuity policy remain authoritative; never edit those records.
		if(rt.Options.AutoStartHosts) {
			foreach(var s in rt.Store.ActiveSessions().Where(s => !HasUnknown(s.Id))) rt.GetHost(s.Id)?.Wake();
			WorkContinuity.Reconcile(rt);
		}
	}
	internal void Recover() {
		lock(rt.AdmissionGate) {
			rt.Db.Write(u => {
				u.Execute("UPDATE maintenance_activities SET state='unknown' WHERE state='running' AND generation<>@generation", new { generation = rt.Generation });
				// Prior-generation registration execution is never reconstructed or replayed at startup.
				u.Execute("UPDATE git_registration_intents SET state='unknown',updated_at=@now WHERE state='running'", new { now = Clock.Now });
			});
			var status = Status();
			// Only an explicitly attested, clean handoff is eligible for automatic startup release.
			if(status.Operation is { State: "handoff_ready" } op && status.VerifiedQuiescent) Release(op.Id, false);
		}
	}
}
