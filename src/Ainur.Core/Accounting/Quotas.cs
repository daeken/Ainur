using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Dapper;

namespace Ainur.Core.Accounting;

public sealed class QuotaExhaustedException(string message) : BudgetExhaustedException(message);

public sealed class ScarcityBand {
	/// <summary>Applies when the remaining usable fraction of the window is below this value.</summary>
	public decimal RemainingBelow { get; set; }
	public decimal Multiplier { get; set; } = 1;
}

/// <summary>An account-wide subscription quota window shared by every project using that account.</summary>
public sealed class QuotaWindowConfig {
	public string Id { get; set; } = "";
	public string Provider { get; set; } = "";
	/// <summary>Models that consume this window (null: every model of the provider).</summary>
	public List<string>? Models { get; set; }
	public string Unit { get; set; } = "tokens"; // tokens | requests
	public long Capacity { get; set; }
	public long PeriodSeconds { get; set; } = 5 * 3600;
	/// <summary>Configured dollar value of one complete window.</summary>
	public decimal WindowValueDollars { get; set; }
	public decimal Headroom { get; set; } = 0.1m;
	public List<ScarcityBand> ScarcityBands { get; set; } = [];

	public bool Applies(ModelInfo m) => m.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase) && (Models is null || Models.Contains(m.Id));
}

/// <summary>
/// Reserves and settles consumption in every applicable quota window independently, before dispatch and atomically
/// with the dollar reservation. Valuation takes the maximum across overlapping windows rather than adding them.
/// Configured in &lt;home&gt;/quotas.json; windows reset on fixed cycles aligned to their period.
/// </summary>
public sealed class QuotaManager(string home) {
	public sealed record Hold(QuotaWindowConfig Window, long CycleStart, long Units, decimal Scarcity);

	public List<QuotaWindowConfig> Windows() {
		var path = Path.Combine(home, "quotas.json");
		if(!File.Exists(path)) return [];
		try {
			return JsonUtil.Deserialize<Dictionary<string, List<QuotaWindowConfig>>>(File.ReadAllText(path))?.GetValueOrDefault("windows") ?? [];
		} catch {
			return [];
		}
	}

	public static long CycleStart(QuotaWindowConfig w, long nowMs) {
		var period = Math.Max(1, w.PeriodSeconds) * 1000;
		return nowMs / period * period;
	}

	public static long Units(QuotaWindowConfig w, long inputTokens, long outputTokens) => w.Unit == "requests" ? 1 : inputTokens + outputTokens;

	/// <summary>Holds the request's worst-case units in every applicable window, failing admission if any lacks room.</summary>
	public List<Hold> Reserve(Db.Unit u, ModelInfo model, string modelRequestId, long estimatedInput, long maxOutput) {
		var holds = new List<Hold>();
		foreach(var w in Windows().Where(w => w.Applies(model))) {
			var cycle = CycleStart(w, Clock.Now);
			var units = Units(w, estimatedInput, maxOutput);
			var used = u.Scalar<long>("SELECT COALESCE(SUM(units),0) FROM quota_usage WHERE window_id=@Id AND cycle_start=@cycle AND state IN ('held','committed')", new { w.Id, cycle });
			var usable = (long) (w.Capacity * (1 - w.Headroom));
			if(used + units > usable)
				throw new QuotaExhaustedException($"Quota window {w.Id} has {Math.Max(0, usable - used)} of {w.Capacity} {w.Unit} usable this cycle (after {w.Headroom:P0} headroom); this request may need {units}. It resets at {DateTimeOffset.FromUnixTimeMilliseconds(cycle + w.PeriodSeconds * 1000):u}.");
			var remainingFraction = w.Capacity == 0 ? 0 : (decimal) (usable - used - units) / w.Capacity;
			var scarcity = w.ScarcityBands.Where(b => remainingFraction < b.RemainingBelow).Select(b => b.Multiplier).DefaultIfEmpty(1).Max();
			u.Execute("INSERT INTO quota_usage(id,window_id,cycle_start,model_request_id,units,scarcity,state,created_at) VALUES(@id,@wid,@cycle,@modelRequestId,@units,@s,'held',@now)",
				new { id = Ids.New("qu"), wid = w.Id, cycle, modelRequestId, units, s = Math.Max(1, scarcity).ToString(System.Globalization.CultureInfo.InvariantCulture), now = Clock.Now });
			holds.Add(new(w, cycle, units, Math.Max(1, scarcity)));
		}
		return holds;
	}

	/// <summary>Replaces holds with actual consumption and returns the windows' valuation inputs.</summary>
	public List<QuotaWindow> Commit(Db.Unit u, string modelRequestId, Usage usage) {
		var result = new List<QuotaWindow>();
		var windows = Windows().ToDictionary(w => w.Id);
		foreach(var (id, windowId, scarcity) in u.Query<(string, string, string)>("SELECT id, window_id, scarcity FROM quota_usage WHERE model_request_id=@modelRequestId AND state='held'", new { modelRequestId })) {
			if(!windows.TryGetValue(windowId, out var w)) continue;
			var actual = Units(w, usage.InputTokens, usage.OutputTokens);
			u.Execute("UPDATE quota_usage SET units=@actual, state='committed' WHERE id=@id", new { actual, id });
			// Valuation uses the scarcity frozen in the quote; new telemetry only affects future quotes.
			result.Add(new QuotaWindow(w.Id, w.Capacity == 0 ? 0 : (decimal) actual / w.Capacity, w.WindowValueDollars, decimal.Parse(scarcity, System.Globalization.CultureInfo.InvariantCulture)));
		}
		return result;
	}

	public void Release(Db.Unit u, string modelRequestId) =>
		u.Execute("UPDATE quota_usage SET state='released' WHERE model_request_id=@modelRequestId AND state='held'", new { modelRequestId });

	public List<object> Status(Db db) => Windows().Select(w => {
		var cycle = CycleStart(w, Clock.Now);
		var (held, committed) = db.Read(c => c.QuerySingle<(long, long)>(
			"SELECT COALESCE(SUM(CASE WHEN state='held' THEN units END),0), COALESCE(SUM(CASE WHEN state='committed' THEN units END),0) FROM quota_usage WHERE window_id=@Id AND cycle_start=@cycle", new { w.Id, cycle }));
		return (object) new { w.Id, w.Provider, w.Unit, w.Capacity, held, committed, w.Headroom, w.WindowValueDollars, resets_at = cycle + w.PeriodSeconds * 1000 };
	}).ToList();
}
