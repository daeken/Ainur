using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Ainur.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ainur.Tests;

public class RouteReceiptTests {
	static readonly string KeyHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

	static async Task<(int Status, string Body, string? Cache)> Get(TempHome home, string? header, string? query = null,
		IPAddress? peer = null, Func<string?>? route = null) {
		using var rt = home.Runtime(start: false);
		var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
		ctx.Connection.RemoteIpAddress = peer ?? IPAddress.Loopback;
		ctx.Response.Body = new MemoryStream();
		if(header is not null) ctx.Request.Headers.Authorization = header;
		if(query is not null) ctx.Request.QueryString = new QueryString(query);
		await RouteReceipt.Get(ctx, new ServerOptions { Home = home.Path, Release = "test-release" }, rt, route).ExecuteAsync(ctx);
		ctx.Response.Body.Position = 0;
		return (ctx.Response.StatusCode, await new StreamReader(ctx.Response.Body).ReadToEndAsync(), ctx.Response.Headers.CacheControl);
	}

	[SupportedOSPlatform("macos")]
	[SupportedOSPlatform("linux")]
	static void Key(TempHome home, string? contents = null, UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite) {
		Directory.CreateDirectory(Path.Combine(home.Path, "supervisor"));
		var key = Path.Combine(home.Path, "supervisor", "route-receipt.key");
		File.WriteAllText(key, contents ?? KeyHex + "\n");
		File.SetUnixFileMode(key, mode);
	}

	[Fact]
	public async Task AuthorizedLocalReceiptContainsOnlyAllowlistedFieldsAndNeverCallsProvider() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var home = new TempHome();
		Key(home);
		foreach(var (route, expected) in new (string?, string)[] {
			(null, "unset"), ("subscription", "subscription"), (" SuBsCrIpTiOn ", "subscription"),
			("api", "api"), ("auto", "auto"), ("unexpected-sensitive-input", "other"), ("", "other")
		}) {
			var (status, body, cache) = await Get(home, "Bearer " + KeyHex, route: () => route);
			Assert.Equal(200, status);
			Assert.Equal("no-store", cache);
			using var json = JsonDocument.Parse(body);
			Assert.Equal(new[] { "route_class", "process_id", "process_started_utc", "release", "generation", "provider_policy" },
				json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
			Assert.Equal(expected, json.RootElement.GetProperty("route_class").GetString());
			Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("process_id").GetInt32());
			Assert.Equal("test-release", json.RootElement.GetProperty("release").GetString());
			Assert.Equal(RouteReceipt.ProviderPolicy, json.RootElement.GetProperty("provider_policy").GetString());
			Assert.DoesNotContain(KeyHex, body, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain("unexpected-sensitive-input", body, StringComparison.OrdinalIgnoreCase);
		}
	}

	[Fact]
	public async Task DisabledWithoutValid0600RegularKeyAndRejectsSymlink() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var home = new TempHome();
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
		Key(home, "not-a-key\n");
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
		Key(home, mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
		Key(home);
		var key = Path.Combine(home.Path, "supervisor", "route-receipt.key");
		var original = Path.Combine(home.Path, "supervisor", "original.key");
		File.Move(key, original);
		File.CreateSymbolicLink(key, original);
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
	}

	[Fact]
	public async Task RejectsMissingWrongMalformedRemoteOrQueryCapabilityWithoutReadingRoute() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var home = new TempHome();
		Key(home);
		var calls = 0;
		Func<string?> route = () => { calls++; return "subscription"; };
		foreach(var (header, query, remote) in new (string?, string?, IPAddress?)[] {
			(null, null, null), ("Bearer " + new string('0', 64), null, null),
			("Bearer " + new string('Z', 64), null, null), ("Bearer " + KeyHex, "?token=forbidden", null),
			("Bearer " + KeyHex, null, IPAddress.Parse("192.0.2.1"))
		}) {
			var (status, body, cache) = await Get(home, header, query, remote, route);
			Assert.Equal(403, status);
			Assert.Empty(body);
			Assert.Equal("no-store", cache);
		}
		Assert.Equal(0, calls);
	}
}
