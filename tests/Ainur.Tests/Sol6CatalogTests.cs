using Ainur.Core.Accounting;
using Ainur.Core.Persistence;

namespace Ainur.Tests;

public class Sol6CatalogTests {
	[Fact]
	public void SeedAndReseedExactSol6RoutesPreserveHonestPricingAndHistoricalIds() {
		using var home = new TempHome();
		var store = new Store(new Db(Path.Combine(home.Path, "catalog.db")));
		ModelCatalog.EnsureSeeded(store);
		ModelCatalog.EnsureSeeded(store); // idempotent startup reseed
		var primary = store.GetModel("gpt-6-sol");
		var api = store.GetModel("gpt-6-sol-api");
		Assert.NotNull(primary);
		Assert.NotNull(api);
		Assert.Equal("subscription", primary.Billing);
		Assert.Equal(api.Id, primary.FallbackModelId);
		Assert.Equal("api", api.Billing);
		Assert.Null(api.FallbackModelId); // no reverse fallback
		foreach(var model in new[] { primary, api }) {
			Assert.True(model.Enabled);
			Assert.Equal("openai", model.Provider);
			Assert.Equal("gpt-6-sol", model.UpstreamModel);
			Assert.Null(model.InputPerMillion);
			Assert.Null(model.CachedInputPerMillion);
			Assert.Null(model.OutputPerMillion);
		}
		Assert.Equal("gpt-6", store.GetModel("gpt-6")!.UpstreamModel);
		Assert.Equal("gpt-6.1-sol", store.GetModel("gpt-6.1-sol")!.UpstreamModel);
		Assert.Equal("gpt-6.1-sol-api", store.GetModel("gpt-6.1-sol")!.FallbackModelId);
		Assert.Equal("gpt-6.1-sol", store.GetModel("gpt-6.1-sol-api")!.UpstreamModel);
		Assert.True(store.GetModel("gpt-6.1-sol")!.Enabled);
		Assert.True(store.GetModel("gpt-6.1-sol-api")!.Enabled);
	}
}
