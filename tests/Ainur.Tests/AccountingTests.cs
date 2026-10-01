using Ainur.Core;
using Ainur.Core.Accounting;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;

namespace Ainur.Tests;

public class AccountingTests {
	static ModelInfo Api => new() { Id = "m", Provider = "p", UpstreamModel = "m", InputPerMillion = "1.00", CachedInputPerMillion = "0.10", OutputPerMillion = "4.00", Billing = "api", Premium = "1", PriceProvenance = "test" };
	static ModelInfo Sub => new() { Id = "s", Provider = "p", UpstreamModel = "s", InputPerMillion = "2.00", OutputPerMillion = "8.00", Billing = "subscription", Premium = "1.5", PriceProvenance = "test" };

	[Fact]
	public void ApiChargesUsagePricedCashAndEqualEffective() {
		var q = Pricing.Quote(Api, 10_000, 1_000);
		Assert.Equal(Money.ForTokens(10_000, 1m) + Money.ForTokens(1_000, 4m), q.ReservedCashNanos);
		var c = Pricing.Settle(q, new Usage { InputTokens = 1_000_000, CachedInputTokens = 500_000, OutputTokens = 100_000 });
		Assert.Equal(Money.FromDollars(0.5m + 0.05m + 0.4m), c.CashNanos);
		Assert.Equal(c.CashNanos, c.EffectiveNanos);
		Assert.Equal("usage_priced", c.CashBasis);
	}

	[Fact]
	public void SubscriptionUseHasNoMarginalCashButPositiveEffectiveCharge() {
		var q = Pricing.Quote(Sub, 1000, 1000);
		Assert.Equal(0, q.ReservedCashNanos);
		Assert.True(q.ReservedEffectiveNanos > 0);
		var c = Pricing.Settle(q, new Usage { InputTokens = 1_000_000, OutputTokens = 0 });
		Assert.Equal(0, c.CashNanos);
		Assert.Equal(Money.FromDollars(2m * 1.5m), c.EffectiveNanos);
		Assert.True(Pricing.Settle(q, new Usage { InputTokens = 1 }).EffectiveNanos > 0);
	}

	[Fact]
	public void OverlappingWindowsTakeTheMaximumNotTheSum() {
		var reference = Money.FromDollars(0.10m);
		var windows = new List<QuotaWindow> { new("5h", 0.01m, 20m, 1m), new("week", 0.002m, 50m, 2m) };
		// floor 0.15 (premium 1.5); windows 0.20 and 0.20 → max 0.20, never 0.40.
		Assert.Equal(Money.FromDollars(0.20m), Pricing.SubscriptionEffective(reference, 1.5m, 1m, windows));
		Assert.Equal(Money.FromDollars(0.30m), Pricing.SubscriptionEffective(reference, 1.5m, 2m, windows));
	}

	[Fact]
	public void UnknownPricesAreNotTreatedAsFree() {
		var m = new ModelInfo { Id = "u", Provider = "p", UpstreamModel = "u", Billing = "api", Premium = "1", PriceProvenance = "none" };
		var c = Pricing.Settle(Pricing.Quote(m, 100, 100), new Usage { InputTokens = 100, OutputTokens = 100 });
		Assert.Null(c.CashNanos);
		Assert.Equal("unknown", c.CashBasis);
		Assert.True(c.EffectiveNanos > 0);
	}

	[Fact]
	public async Task SharedQuotaWindowsAdmitAcrossProjectsWithoutDoubleCounting() {
		using var home = new TempHome();
		File.WriteAllText(Path.Combine(home.Path, "quotas.json"), JsonUtil.Serialize(new {
			windows = new object[] {
				new { id = "zai-5h", provider = "zai", unit = "tokens", capacity = 60_000, period_seconds = 18_000, window_value_dollars = 10m, headroom = 0.0m,
					scarcity_bands = new[] { new { remaining_below = 0.5m, multiplier = 2m } } },
				new { id = "zai-week", provider = "zai", unit = "requests", capacity = 1000, period_seconds = 604_800, window_value_dollars = 50m, headroom = 0.0m },
			},
		}));
		var provider = new FakeProvider((_, _) => new ProviderResponse { Content = "ok", Usage = new Usage { InputTokens = 5_000, OutputTokens = 1_000 } }, "zai");
		using var rt = home.Runtime(provider, o => o.AutoStartHosts = false);
		var a = rt.CreateProject("A", "d", home.Workspace, budgetDollars: 100m, managerModelId: "glm-5.3");
		var b = rt.CreateProject("B", "d", home.Workspace, budgetDollars: 100m, managerModelId: "glm-5.3");
		var model = rt.Store.GetModel("glm-5.3")!;
		ModelCall Call(Project p, int maxOut) => new() { ProjectId = p.Id, Purpose = "turn", Category = "direct", Model = model, Messages = [ChatMessage.User("hi")], EstimatedInputTokens = 5_000, MaxOutputTokens = maxOut };

		// Worst case 5k + 20k = 25k tokens each: two fit in 60k, a third concurrent reservation would not.
		var r1 = await rt.Gateway.CallAsync(Call(a, 20_000), default);
		var r2 = await rt.Gateway.CallAsync(Call(b, 20_000), default);
		// Settled at actual usage (6k each), so plenty of room remains for a third.
		var r3 = await rt.Gateway.CallAsync(Call(a, 20_000), default);
		var status = rt.Quotas.Status(rt.Db);
		Assert.Contains("\"committed\":18000", JsonUtil.Serialize(status[0]));
		Assert.Contains("\"committed\":3", JsonUtil.Serialize(status[1]));
		// Each request is charged once: max over windows (6k/60k * $10 = $1.00 in the 5h window), not the sum with the weekly window.
		Assert.Equal(Money.FromDollars(1.00m), r1.Cost!.EffectiveNanos);
		Assert.Equal(0, r1.Cost.CashNanos);
		// A request that cannot fit is refused before dispatch.
		await Assert.ThrowsAsync<QuotaExhaustedException>(() => rt.Gateway.CallAsync(Call(b, 50_000), default));
		Assert.Equal(3, provider.Requests.Count);
		Assert.Equal(0, rt.Db.Read(c => Dapper.SqlMapper.ExecuteScalar<long>(c, "SELECT COUNT(*) FROM quota_usage WHERE state='held'")));
	}
}
