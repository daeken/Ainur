using Ainur.Core.Providers;
using Ainur.Server;

namespace Ainur.Tests;

public class SubscriptionOnlyStartupTests {
	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("subscription")]
	[InlineData(" SuBsCrIpTiOn ")]
	public void NormalStartupPinsSubscriptionBeforeOpeningHome(string? inheritedRoute) =>
		Assert.Equal("subscription", SubscriptionOnlyStartup.Route(inheritedRoute));

	[Theory]
	[InlineData("auto")]
	[InlineData("api")]
	[InlineData("garbage")]
	public void NormalStartupRejectsConflictingInheritedRoute(string inheritedRoute) =>
		Assert.Throws<InvalidOperationException>(() => SubscriptionOnlyStartup.Route(inheritedRoute));

	[Fact]
	public void ProductionProviderIgnoresInheritedApiSelectionAndPinsSubscription() {
		var existing = Environment.GetEnvironmentVariable("AINUR_OPENAI_ROUTE");
		try {
			Environment.SetEnvironmentVariable("AINUR_OPENAI_ROUTE", "api");
			Assert.Equal(OpenAiRoutePolicy.Subscription, OpenAiProvider.CreateDefault().SnapshotRoutePolicy());
			Environment.SetEnvironmentVariable("AINUR_OPENAI_ROUTE", "auto");
			Assert.Equal(OpenAiRoutePolicy.Subscription, OpenAiProvider.CreateDefault().SnapshotRoutePolicy());
		} finally {
			Environment.SetEnvironmentVariable("AINUR_OPENAI_ROUTE", existing);
		}
	}
}
