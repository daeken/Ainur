using Ainur.Core.Model;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed record SessionControlReceipt(string ReceiptId, long Generation, string SessionId, string AgentId,
	bool StopRequested, bool LoopExitConfirmed, bool StopConfirmed, int UnsettledOperations,
	bool UnknownTools, bool UncertainModelBilling, bool WasPaused, bool Resumed);

public sealed partial class AinurRuntime {
	sealed record StopReceipt(string Id, long Generation, SessionHost Host, string? Actor, bool WasPaused, long OrdinaryPauseVersion);
	readonly Dictionary<string, StopReceipt> StopReceipts = new();

	internal bool HasRunningSessionWork(string sessionId) => Db.Read(c => c.ExecuteScalar<int>(
		"SELECT COUNT(*) FROM maintenance_activities WHERE session_id=@sessionId AND state='running' AND kind!='resume_stopped_session'", new { sessionId })) > 0;

	SessionControlReceipt Observe(StopReceipt receipt, bool resumed = false) {
		var h = receipt.Host;
		var unknownTools = Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM tool_invocations WHERE session_id=@sid AND state IN ('running','unknown')", new { sid = h.SessionId })) > 0;
		var uncertainModel = Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM model_requests WHERE session_id=@sid AND state IN ('dispatched','unknown')", new { sid = h.SessionId })) > 0;
		return new(receipt.Id, receipt.Generation, h.SessionId, h.AgentId, h.StopRequested, h.LoopExitConfirmed,
			h.StopConfirmed, h.UnsettledOperations, unknownTools, uncertainModel, receipt.WasPaused, resumed);
	}

	/// <summary>Stop one already-live host. Never create a host to certify a historic stop.</summary>
	public async Task<SessionControlReceipt> StopSessionAsync(string sessionId, string agentId, long generation, string? actorId,
		TimeSpan wait, CancellationToken ct = default) {
		if(wait < TimeSpan.Zero || wait > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(wait));
		StopReceipt receipt;
		lock(AdmissionGate) {
			if(generation != Generation) throw new InvalidOperationException("Runtime generation mismatch");
			if(!Hosts.TryGetValue(sessionId, out var host) || host.AgentId != agentId)
				throw new InvalidOperationException("No matching live host; historical UNKNOWN is not certified by absence");
			if(!StopReceipts.TryGetValue(sessionId, out receipt!)) {
				var pauseVersion = Db.Read(c => c.ExecuteScalar<long>(
					"SELECT COALESCE(MAX(id),0) FROM events WHERE kind='agent.paused' AND entity_type='agent' AND entity_id=@agentId", new { agentId }));
				receipt = new(Guid.NewGuid().ToString("N"), Generation, host, actorId, IsPaused(sessionId), pauseVersion);
				Db.Write(u => {
					if(Store.GetAgent(agentId)?.PrimarySessionId == sessionId) Store.SetAgentState(u, agentId, AgentStates.Paused);
					u.Execute("UPDATE sessions SET state='paused' WHERE id=@sessionId AND state!='finished'", new { sessionId });
					u.Journal("session.stop_requested", Store.GetSession(host.SessionId)!.ProjectId, "session", sessionId, actorId,
						new { receipt.Id, generation, agentId, receipt.WasPaused, receipt.OrdinaryPauseVersion });
				});
				StopReceipts.Add(sessionId, receipt);
			}
			host.Stop(); // epoch invalidation + token cancellation under admission lock.
		}
		var until = DateTime.UtcNow + wait;
		while(!receipt.Host.StopConfirmed && DateTime.UtcNow < until) await Task.Delay(10, ct);
		var observed = Observe(receipt);
		Db.Write(u => u.Journal("session.stop_observed", Store.GetSession(receipt.Host.SessionId)!.ProjectId, "session", sessionId, actorId, observed));
		return observed;
	}

	public SessionControlReceipt ResumeStoppedSession(string sessionId, string agentId, long generation, string receiptId,
		string? actorId, bool acknowledgeUncertainModelBilling = false) {
		using var admission = Maintenance.Admit("resume_stopped_session", sessionId);
		SessionHost replacement;
		SessionControlReceipt observed;
		bool preservePause;
		lock(AdmissionGate) {
			if(generation != Generation || !StopReceipts.TryGetValue(sessionId, out var receipt) || receipt.Id != receiptId || receipt.Generation != Generation)
				throw new InvalidOperationException("Exact live-generation stop receipt required");
			if(!Hosts.TryGetValue(sessionId, out var old) || old != receipt.Host || old.AgentId != agentId)
				throw new InvalidOperationException("Host identity changed");
			observed = Observe(receipt);
			if(!observed.StopConfirmed) throw new InvalidOperationException("Stop/unwind not confirmed; resume blocked");
			if(observed.UnknownTools || Maintenance.HasUnknown(sessionId)) throw new InvalidOperationException("UNKNOWN tool/maintenance outcome cannot be replayed");
			if(observed.UncertainModelBilling && !acknowledgeUncertainModelBilling)
				throw new InvalidOperationException("Explicit uncertain model billing acknowledgement required");
			var agent = Store.GetAgent(agentId)!;
			var session = Store.GetSession(sessionId)!;
			if(agent.PrimarySessionId != sessionId || !AgentStates.IsLive(agent.State) || session.State == "finished" || !receipt.WasPaused && ActivePause(sessionId) is not null)
				throw new InvalidOperationException("Session is not eligible; resolve its durable pause separately");
			preservePause = receipt.WasPaused;
			Db.Write(u => {
				// PauseAgent shares AdmissionGate. Compare monotonic durable journal IDs, not
				// clocks or the already-paused agent state, inside the resume write transaction.
				var pauseVersion = u.Scalar<long>(
					"SELECT COALESCE(MAX(id),0) FROM events WHERE kind='agent.paused' AND entity_type='agent' AND entity_id=@agentId", new { agentId });
				preservePause |= pauseVersion > receipt.OrdinaryPauseVersion;
				if(!preservePause) {
					Store.SetAgentState(u, agentId, AgentStates.Sleeping);
					u.Execute("UPDATE sessions SET state='idle' WHERE id=@sessionId", new { sessionId });
				}
				u.Journal("session.resumed_after_stop", Store.GetSession(old.SessionId)!.ProjectId, "session", sessionId, actorId,
					new { receiptId, generation, agentId, acknowledgeUncertainModelBilling, preservePause, receipt.OrdinaryPauseVersion, pauseVersion, observed });
			});
			replacement = new SessionHost(this, Store.GetSession(sessionId)!);
			replacement.Delta += (sid, d) => Delta?.Invoke(sid, d);
			Hosts[sessionId] = replacement;
			StopReceipts.Remove(sessionId);
			Failures.TryRemove(sessionId, out _);
			old.Dispose();
		}
		if(!preservePause) replacement.Wake();
		return observed with { Resumed = true };
	}
}
