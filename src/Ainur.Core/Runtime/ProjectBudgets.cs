using Ainur.Core.Model;
using Ainur.Core.Persistence;

namespace Ainur.Core.Runtime;

/// <summary>Explicit controls over independent effective and cash envelopes; zero effective retains its legacy no-limit meaning.</summary>
public static class ProjectBudgets {
	public static long Amount(decimal dollars, string field) {
		if(dollars < 0) throw new DomainException($"{field} must be nonnegative.");
		try {
			var nanos = Money.FromDollars(dollars);
			if(dollars > 0 && nanos == 0) throw new DomainException($"{field} must be at least one nanodollar or exactly zero.");
			return nanos;
		} catch(OverflowException) { throw new DomainException($"{field} exceeds the supported monetary range."); }
	}

	public static void Apply(Project project, decimal? budgetDollars = null, bool? noEffectiveLimit = null,
		decimal? cashCeilingDollars = null, bool clearCashCeiling = false) {
		// Validate every field before mutating the caller's object.
		var effective = budgetDollars is { } b ? Amount(b, "budget_dollars") : project.EffectiveBudgetNanos;
		var cash = cashCeilingDollars is { } c ? Amount(c, "cash_ceiling_dollars") : project.CashCeilingNanos;
		if(noEffectiveLimit == true && budgetDollars is > 0)
			throw new DomainException("no_effective_limit conflicts with a positive budget_dollars.");
		if(noEffectiveLimit == true) effective = 0;
		if(noEffectiveLimit == false && effective == 0)
			throw new DomainException("A finite effective limit requires positive budget_dollars.");
		if(clearCashCeiling && cashCeilingDollars is not null)
			throw new DomainException("clear_cash_ceiling conflicts with cash_ceiling_dollars.");
		project.EffectiveBudgetNanos = effective;
		project.CashCeilingNanos = clearCashCeiling ? null : cash;
	}
}

public sealed partial class AinurRuntime {
	public Project UpdateProjectBudget(string projectId, decimal? budgetDollars = null, bool? noEffectiveLimit = null,
		decimal? cashCeilingDollars = null, bool clearCashCeiling = false, string? description = null) => Db.Write(u => {
		var project = u.Single<Project>("SELECT * FROM projects WHERE id=@projectId", new { projectId })
			?? throw new DomainException($"Unknown project {projectId}");
		ProjectBudgets.Apply(project, budgetDollars, noEffectiveLimit, cashCeilingDollars, clearCashCeiling);
		if(description is not null) project.Description = description;
		Store.UpdateProject(u, project);
		u.Journal("project.updated", project.Id, "project", project.Id, payload: new {
			budget = project.EffectiveBudgetNanos, no_effective_limit = project.EffectiveBudgetNanos == 0, cash_ceiling = project.CashCeilingNanos,
		});
		return project;
	});
}
