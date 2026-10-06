using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Ainur.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Ainur.Tests;

public class RouteReceiptTests {
	[DllImport("libc", SetLastError = true, EntryPoint = "mkfifo")]
	static extern int MakeFifo(string path, uint permissions);
	[DllImport("libc", SetLastError = true, EntryPoint = "link")]
	static extern int MakeHardLink(string existing, string newPath);
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
		var supervisor = Path.Combine(home.Path, "supervisor");
		var parent = Path.Combine(supervisor, "receipt-secrets");
		Directory.CreateDirectory(parent);
		File.SetUnixFileMode(home.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		File.SetUnixFileMode(supervisor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		File.SetUnixFileMode(parent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var key = Path.Combine(parent, "route-receipt.key");
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
			Assert.Equal(new[] { "route_class", "process_id", "process_started_utc", "release", "generation", "provider_policy", "core_sha256" },
				json.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
			Assert.Equal(expected, json.RootElement.GetProperty("route_class").GetString());
			Assert.Equal(Environment.ProcessId, json.RootElement.GetProperty("process_id").GetInt32());
			Assert.Equal("test-release", json.RootElement.GetProperty("release").GetString());
			Assert.Equal(RouteReceipt.ProviderPolicy, json.RootElement.GetProperty("provider_policy").GetString());
			Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Ainur.Core.Runtime.AinurRuntime).Assembly.Location))),
				json.RootElement.GetProperty("core_sha256").GetString());
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
		var key = Path.Combine(home.Path, "supervisor", "receipt-secrets", "route-receipt.key");
		var original = Path.Combine(home.Path, "supervisor", "receipt-secrets", "original.key");
		File.Move(key, original);
		File.CreateSymbolicLink(key, original);
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
	}

	[Fact]
	public async Task AdversarialFifoAndHardlinkAndParentSymlinkFailClosedWithoutBlocking() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var home = new TempHome();
		Key(home);
		var parent = Path.Combine(home.Path, "supervisor", "receipt-secrets");
		var key = Path.Combine(parent, "route-receipt.key");
		File.Delete(key);
		Assert.Equal(0, MakeFifo(key, 0x180));
		File.SetUnixFileMode(key, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		var fifoGet = Get(home, "Bearer " + KeyHex);
		var finished = await Task.WhenAny(fifoGet, Task.Delay(TimeSpan.FromMilliseconds(600)));
		Assert.Same(fifoGet, finished); // No FIFO writer; a blocking open hangs here.
		Assert.Equal(404, (await fifoGet).Status);
		Assert.Equal(403, (await Get(home, null)).Status); // Malformed bearer must not touch FIFO.
		File.Delete(key);
		Key(home);
		Assert.Equal(0, MakeHardLink(key, Path.Combine(parent, "second-link")));
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
		File.Delete(Path.Combine(parent, "second-link"));
		Directory.Move(parent, Path.Combine(home.Path, "real-secrets"));
		Directory.CreateSymbolicLink(parent, Path.Combine(home.Path, "real-secrets"));
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
		Directory.Delete(parent);
		Directory.Move(Path.Combine(home.Path, "real-secrets"), parent);
		var supervisor = Path.Combine(home.Path, "supervisor");
		Directory.Move(supervisor, Path.Combine(home.Path, "real-supervisor"));
		Directory.CreateSymbolicLink(supervisor, Path.Combine(home.Path, "real-supervisor"));
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
	}

	[Fact]
	public async Task DeniesWorldWritableSupervisorOrSecretsAndWrongOwnershipOrPath() {
		if(!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
		using var home = new TempHome();
		Key(home);
		var supervisor = Path.Combine(home.Path, "supervisor");
		var secrets = Path.Combine(supervisor, "receipt-secrets");
		File.SetUnixFileMode(supervisor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
		File.SetUnixFileMode(supervisor, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherWrite);
		Assert.Equal(404, (await Get(home, "Bearer " + KeyHex)).Status);
		File.SetUnixFileMode(secrets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		File.SetUnixFileMode(home.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupWrite);
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
