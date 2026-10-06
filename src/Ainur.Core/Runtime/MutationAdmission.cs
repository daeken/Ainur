using Ainur.Core.Persistence;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed partial class AinurRuntime {
	// Non-session mutations must participate in the same drain boundary as admitted session steps.
	// Pair the drain token with the existing durable maintenance lease. NativeGit
	// persists its own operation intents; this does not create a second Git intent.
	readonly HashSet<string> ActiveMutationTokens = [];
	IEnumerable<string> AdmittedMutations => ActiveMutationTokens.Concat(Db.Read(c => c.Query<string>("""
		SELECT 'git-unknown:' || id FROM maintenance_activities WHERE kind='git_mutation' AND state='unknown'
		UNION ALL SELECT 'git-registration:' || id FROM git_registration_intents WHERE state IN ('running','unknown')
		""")));
	// Intent uncertainty is independent of transient lease survival. A project-wide fence
	// deliberately avoids deriving no-effects or destination identity from absent/partial bindings.
	internal void RequireResolvedGitRegistrations(string projectId, Db.Unit? unit = null) {
		const string sql = "SELECT COUNT(*) FROM git_registration_intents WHERE project_id=@projectId AND state IN ('running','unknown')";
		if((unit is null ? Db.Read(c => c.ExecuteScalar<int>(sql, new { projectId })) : unit.Scalar<int>(sql, new { projectId })) != 0)
			throw new DomainException("Unresolved native registration intent fences this project's Git mutations; inspect only, never bypass with a fresh operation_id or path spelling.");
	}
	internal IUnknownMutationLease AdmitMutation(string kind, string projectId) {
		lock(AdmissionGate) {
			if(Draining) throw new DomainException("Runtime is draining; Git mutation rejected, not queued. No operation admitted; retry only by an explicit fresh request after maintenance.");
			if(Db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM maintenance_activities WHERE kind='git_mutation' AND state='unknown'")) != 0)
				throw new DomainException("Unresolved prior Git mutation maintenance lease; manual resolution required. No operation admitted or replayed.");
			RequireResolvedGitRegistrations(projectId);
			var maintenance = Maintenance.Admit("git_mutation", referenceId: kind);
			var token = kind + ":" + Guid.NewGuid().ToString("N");
			ActiveMutationTokens.Add(token);
			return new MutationLease(this, token, maintenance);
		}
	}
	sealed class MutationLease(AinurRuntime runtime, string token, IDisposable maintenance) : IUnknownMutationLease {
		int disposed;
		public void MarkUnknown() { lock(runtime.AdmissionGate) { if(disposed == 0) ((IUnknownMutationLease)maintenance).MarkUnknown(); } }
		public void Dispose() {
			if(Interlocked.Exchange(ref disposed, 1) != 0) return;
			lock(runtime.AdmissionGate) {
				try { maintenance.Dispose(); }
				finally { runtime.ActiveMutationTokens.Remove(token); }
			}
		}
	}
}
