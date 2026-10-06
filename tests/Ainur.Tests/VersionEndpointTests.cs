using Ainur.Server;

namespace Ainur.Tests;

public class VersionEndpointTests {
	[Fact]
	public void ReleaseDefaultsToDevWhenUnset() {
		using var home = new TempHome();
		using var rt = home.Runtime();
		var info = Api.GetVersionInfo(new ServerOptions { Release = null }, rt);
		Assert.Equal("dev", info.Release);
		Assert.Equal(rt.Generation, info.Generation);
		Assert.Equal(rt.Db.SchemaVersion, info.Schema);
	}

	[Fact]
	public void ExplicitReleaseIsUsed() {
		using var home = new TempHome();
		using var rt = home.Runtime();
		var info = Api.GetVersionInfo(new ServerOptions { Release = "rel_2026.10.01" }, rt);
		Assert.Equal("rel_2026.10.01", info.Release);
		Assert.Equal(rt.Generation, info.Generation);
		Assert.Equal(rt.Db.SchemaVersion, info.Schema);
	}
}
