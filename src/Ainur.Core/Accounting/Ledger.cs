using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Dapper;

namespace Ainur.Core.Accounting;

public class BudgetExhaustedException(string message) : DomainException(message);

public sealed class CostSummary {
	public long? CashNanos { get; set; }
	public long CashKnownNanos { get; set; }
	public int CashUnknownCount { get; set; }
	public long EffectiveNanos { get; set; }
	public long ReservedEffectiveNanos { get; set; }
	public long ReservedCashNanos { get; set; }
	public long BudgetNanos { get; set; }
	public long? CashCeilingNanos { get; set; }
}

/// <summary>Admission control and settlement. Each cost event is recorded exactly once against its model request.</summary>
public sealed class Ledger(Store store) {
	readonly Db Db = store.Db;

	/// <summary>Atomically checks the project's envelopes and holds a reservation for the quoted upper bound.</summary>
	public string Reserve(Db.Unit u, string projectId, string modelRequestId, Quote q) {
		var project = u.Single<Project>("SELECT * FROM projects WHERE id=@projectId", new { projectId }) ?? throw new DomainException($"Unknown project {projectId}");
		var spentEffective = u.Scalar<long>("SELECT COALESCE(SUM(effective_nanos),0) FROM cost_events WHERE project_id=@projectId", new { projectId });
		var heldEffective = u.Scalar<long>("SELECT COALESCE(SUM(effective_nanos),0) FROM reservations WHERE project_id=@projectId AND state='held'", new { projectId });
		if(project.EffectiveBudgetNanos > 0 && spentEffective + heldEffective + q.ReservedEffectiveNanos > project.EffectiveBudgetNanos)
			throw new BudgetExhaustedException($"Effective budget exhausted: spent {Money.Format(spentEffective)}, reserved {Money.Format(heldEffective)}, request needs up to {Money.Format(q.ReservedEffectiveNanos)} of {Money.Format(project.EffectiveBudgetNanos)}.");
		if(project.CashCeilingNanos is { } ceiling) {
			var spentCash = u.Scalar<long>("SELECT COALESCE(SUM(cash_nanos),0) FROM cost_events WHERE project_id=@projectId", new { projectId });
			var heldCash = u.Scalar<long>("SELECT COALESCE(SUM(cash_nanos),0) FROM reservations WHERE project_id=@projectId AND state='held'", new { projectId });
			if(spentCash + heldCash + q.ReservedCashNanos > ceiling)
				throw new BudgetExhaustedException($"Cash ceiling reached: spent {Money.Format(spentCash)}, reserved {Money.Format(heldCash)}, request needs up to {Money.Format(q.ReservedCashNanos)} of {Money.Format(ceiling)}.");
		}
		var id = Ids.New("rsv");
		u.Execute("INSERT INTO reservations(id,project_id,model_request_id,effective_nanos,cash_nanos,state,created_at) VALUES(@id,@projectId,@modelRequestId,@e,@c,'held',@now)",
			new { id, projectId, modelRequestId, e = q.ReservedEffectiveNanos, c = q.ReservedCashNanos, now = Clock.Now });
		return id;
	}

	/// <summary>Replaces the reservation with one committed charge. Idempotent per model request.</summary>
	public CostEvent? Settle(Db.Unit u, ModelRequestRecord r, Charge charge, string category, string? sponsorAgentId) {
		if(u.Scalar<long>("SELECT COUNT(*) FROM cost_events WHERE model_request_id=@Id", r) > 0) {
			u.Execute("UPDATE reservations SET state='settled', settled_at=@now WHERE model_request_id=@id AND state='held'", new { id = r.Id, now = Clock.Now });
			return null;
		}
		var e = new CostEvent {
			Id = Ids.New("cst"), ProjectId = r.ProjectId, ObjectiveId = r.ObjectiveId, AgentId = r.AgentId, SponsorAgentId = sponsorAgentId,
			SessionId = r.SessionId, ModelRequestId = r.Id, Category = category, CashNanos = charge.CashNanos, CashBasis = charge.CashBasis,
			EffectiveNanos = charge.EffectiveNanos, Valuation = charge.Detail, CreatedAt = Clock.Now,
		};
		store.InsertCost(u, e);
		u.Execute("UPDATE reservations SET state='settled', settled_at=@now WHERE model_request_id=@id AND state='held'", new { id = r.Id, now = Clock.Now });
		return e;
	}

	public void Release(Db.Unit u, string modelRequestId) =>
		u.Execute("UPDATE reservations SET state='released', settled_at=@now WHERE model_request_id=@modelRequestId AND state='held'", new { modelRequestId, now = Clock.Now });

	public CostSummary Summary(string projectId, string? agentId = null) => Db.Read(c => {
		var project = c.QuerySingle<Project>("SELECT * FROM projects WHERE id=@projectId", new { projectId });
		var filter = agentId is null ? "" : " AND agent_id=@agentId";
		var row = c.QuerySingle<(long Known, long Unknown, long Effective)>(
			$"SELECT COALESCE(SUM(cash_nanos),0), COALESCE(SUM(CASE WHEN cash_nanos IS NULL THEN 1 ELSE 0 END),0), COALESCE(SUM(effective_nanos),0) FROM cost_events WHERE project_id=@projectId{filter}",
			new { projectId, agentId });
		var held = c.QuerySingle<(long E, long C)>("SELECT COALESCE(SUM(effective_nanos),0), COALESCE(SUM(cash_nanos),0) FROM reservations WHERE project_id=@projectId AND state='held'", new { projectId });
		return new CostSummary {
			CashKnownNanos = row.Known, CashUnknownCount = (int) row.Unknown, CashNanos = row.Unknown > 0 ? null : row.Known,
			EffectiveNanos = row.Effective, ReservedEffectiveNanos = held.E, ReservedCashNanos = held.C,
			BudgetNanos = project.EffectiveBudgetNanos, CashCeilingNanos = project.CashCeilingNanos,
		};
	});

	/// <summary>Per-agent direct spend plus delegated spend (everything performed by agents in its reporting subtree).</summary>
	public Dictionary<string, (long Direct, long Delegated, long? CashDirect)> ByAgent(string projectId) {
		var agents = store.ListAgents(projectId);
		var direct = Db.Read(c => c.Query<(string AgentId, long Effective, long Cash, long Unknown)>(
			"SELECT agent_id, COALESCE(SUM(effective_nanos),0), COALESCE(SUM(cash_nanos),0), SUM(CASE WHEN cash_nanos IS NULL THEN 1 ELSE 0 END) FROM cost_events WHERE project_id=@projectId AND agent_id IS NOT NULL GROUP BY agent_id",
			new { projectId }).ToDictionary(r => r.AgentId));
		var children = agents.Where(a => a.ManagerId is not null).ToLookup(a => a.ManagerId!);
		long Subtree(string id) => children[id].Sum(c => (direct.TryGetValue(c.Id, out var d) ? d.Effective : 0) + Subtree(c.Id));
		return agents.ToDictionary(a => a.Id, a => {
			var d = direct.TryGetValue(a.Id, out var v) ? v : default;
			return (d.Effective, Subtree(a.Id), d.Unknown > 0 ? (long?) null : d.Cash);
		});
	}
}
