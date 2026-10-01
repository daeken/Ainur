using Ainur.Core.Model;
using Ainur.Core.Providers;

namespace Ainur.Core.Accounting;

/// <summary>Frozen pricing parameters for one request, captured before dispatch and reused at settlement.</summary>
public sealed class Quote {
	public string ModelId { get; set; } = "";
	public string Billing { get; set; } = "api";
	public decimal? InputRate { get; set; }
	public decimal? CachedInputRate { get; set; }
	public decimal? OutputRate { get; set; }
	public decimal ReferenceInputRate { get; set; }
	public decimal ReferenceCachedInputRate { get; set; }
	public decimal ReferenceOutputRate { get; set; }
	public string ReferenceSource { get; set; } = "";
	public decimal Premium { get; set; } = 1;
	public decimal ScarcityFloor { get; set; } = 1;
	public long EstimatedInputTokens { get; set; }
	public long MaxOutputTokens { get; set; }
	public long ReservedCashNanos { get; set; }
	public long ReservedEffectiveNanos { get; set; }
	public string ValuationVersion { get; set; } = Pricing.ValuationVersion;
	public long QuotedAt { get; set; }
}

public sealed record Charge(long? CashNanos, string CashBasis, long EffectiveNanos, string Detail);

/// <summary>A quota window that constrains a subscription request, with its estimated consumption share.</summary>
public sealed record QuotaWindow(string Id, decimal WindowFraction, decimal WindowValueDollars, decimal Scarcity);

public static class Pricing {
	public const string ValuationVersion = "v1";

	/// <summary>
	/// Conservative fallback reference schedule (USD per million tokens) for models without a validated
	/// comparable API price. Deliberately high; configure real schedules to replace it.
	/// </summary>
	public static (decimal Input, decimal CachedInput, decimal Output) FallbackSchedule = (15m, 15m, 75m);

	public static Quote Quote(ModelInfo model, long estimatedInputTokens, long maxOutputTokens, decimal scarcityFloor = 1) {
		var hasPrices = model.InputRate is > 0 && model.OutputRate is > 0;
		var q = new Quote {
			ModelId = model.Id, Billing = model.Billing,
			InputRate = model.InputRate, CachedInputRate = model.CachedInputRate, OutputRate = model.OutputRate,
			Premium = Math.Max(1, model.PremiumValue), ScarcityFloor = Math.Max(1, scarcityFloor),
			EstimatedInputTokens = estimatedInputTokens, MaxOutputTokens = maxOutputTokens, QuotedAt = Clock.Now,
		};
		if(hasPrices) {
			q.ReferenceInputRate = model.InputRate!.Value;
			q.ReferenceCachedInputRate = model.CachedInputRate ?? model.InputRate!.Value;
			q.ReferenceOutputRate = model.OutputRate!.Value;
			q.ReferenceSource = model.PriceProvenance;
		} else {
			(q.ReferenceInputRate, q.ReferenceCachedInputRate, q.ReferenceOutputRate) = FallbackSchedule;
			q.ReferenceSource = "configured conservative fallback schedule";
		}
		// Upper bound: every input token uncached plus the full output allowance.
		var referenceUpper = Money.ForTokens(estimatedInputTokens, q.ReferenceInputRate) + Money.ForTokens(maxOutputTokens, q.ReferenceOutputRate);
		q.ReservedCashNanos = model.Billing == "api" && hasPrices ? referenceUpper : 0;
		q.ReservedEffectiveNanos = Scale(referenceUpper, q.Premium * q.ScarcityFloor);
		return q;
	}

	public static Charge Settle(Quote q, Usage usage, IReadOnlyList<QuotaWindow>? windows = null) {
		var cached = Math.Min(usage.CachedInputTokens, usage.InputTokens);
		var uncached = usage.InputTokens - cached;
		var reference = Money.ForTokens(uncached, q.ReferenceInputRate) + Money.ForTokens(cached, q.ReferenceCachedInputRate) + Money.ForTokens(usage.OutputTokens, q.ReferenceOutputRate);
		var basis = usage.Reported ? "usage_priced" : "estimated";
		if(q.Billing == "subscription") {
			var effective = SubscriptionEffective(reference, q.Premium, q.ScarcityFloor, windows ?? []);
			return new Charge(0, "subscription_marginal", effective, $"subscription: reference {Money.Format(reference)} x premium {q.Premium} ({q.ReferenceSource})");
		}
		if(q.InputRate is null || q.OutputRate is null || q.InputRate == 0 || q.OutputRate == 0) {
			// Unvalidated or zero prices do not prove the request was free.
			return new Charge(null, "unknown", Scale(reference, q.Premium * q.ScarcityFloor), $"cash price unknown; effective from {q.ReferenceSource}");
		}
		var cash = Money.ForTokens(uncached, q.InputRate.Value) + Money.ForTokens(cached, q.CachedInputRate ?? q.InputRate.Value) + Money.ForTokens(usage.OutputTokens, q.OutputRate.Value);
		return new Charge(cash, basis, Scale(cash, q.Premium * q.ScarcityFloor), $"api: {uncached} in, {cached} cached, {usage.OutputTokens} out");
	}

	/// <summary>
	/// effective = max(premium * reference * scarcity_floor, max over windows(window_fraction * window_value * window_scarcity)).
	/// Overlapping windows take the maximum rather than adding the same consumption twice. Always positive for nonzero use.
	/// </summary>
	public static long SubscriptionEffective(long referenceNanos, decimal premium, decimal scarcityFloor, IReadOnlyList<QuotaWindow> windows) {
		var floor = Scale(referenceNanos, Math.Max(1, premium) * Math.Max(1, scarcityFloor));
		var windowMax = windows.Count == 0 ? 0 : windows.Max(w => Money.FromDollars(w.WindowFraction * w.WindowValueDollars * Math.Max(1, w.Scarcity)));
		var result = Math.Max(floor, windowMax);
		return referenceNanos > 0 ? Math.Max(1, result) : result;
	}

	static long Scale(long nanos, decimal factor) => (long) decimal.Ceiling(nanos * factor);
}
