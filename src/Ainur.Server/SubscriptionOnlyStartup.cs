namespace Ainur.Server;

/// <summary>Fail closed before acquiring a scheduler lock or opening persisted state.</summary>
public static class SubscriptionOnlyStartup {
	public static string Route(string? inheritedRoute) {
		if(!string.IsNullOrWhiteSpace(inheritedRoute) &&
			!string.Equals(inheritedRoute.Trim(), "subscription", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException("AINUR_OPENAI_ROUTE must be subscription for the normal server startup; API and auto routes are disabled.");
		return "subscription";
	}
}
