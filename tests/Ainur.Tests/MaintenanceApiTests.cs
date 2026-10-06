using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ainur.Server;
using Ainur.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Ainur.Tests;

public class MaintenanceApiTests {
	[Fact]
	public async Task OperatorHttpControlsAreAuthenticatedAndIndependentOfHeldLauncherSession() {
		if(!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
		using var home = new TempHome();
		using var rt = home.Runtime(configure: o => o.AutoStartHosts = false);
		var project = rt.CreateProject("launcher", "", null, managerModelId: "deepseek-v4.1-flash");
		var sid = rt.Store.GetAgent(project.RootAgentId!)!.PrimarySessionId!;
		var keyDir = Path.Combine(home.Path, "supervisor", "receipt-secrets");
		Directory.CreateDirectory(keyDir);
		File.SetUnixFileMode(keyDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		var key = new string('a', 64);
		var keyPath = Path.Combine(keyDir, "route-receipt.key");
		File.WriteAllText(keyPath, key + "\n");
		File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		var builder = WebApplication.CreateBuilder();
		builder.WebHost.UseUrls("http://127.0.0.1:0");
		builder.Services.AddSingleton(rt);
		builder.Services.AddSingleton(new ServerOptions { Home = home.Path, Port = 0 });
		await using var app = builder.Build();
		app.MapMaintenance();
		await app.StartAsync();
		var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
		using var client = new HttpClient { BaseAddress = new Uri(address) };
		const string path = "/api/v1/control/maintenance";
		Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(path, new BeginMaintenanceRequest("blocked anonymous"))).StatusCode);
		Assert.False(rt.Maintenance.Fenced);
		client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
		var begin = await client.PostAsJsonAsync(path, new BeginMaintenanceRequest("operator upgrade", 1));
		Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
		Assert.Null(rt.GetHost(sid));
		var read = await client.GetAsync(path);
		Assert.Equal(HttpStatusCode.OK, read.StatusCode);
		Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
		using var json = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
		Assert.True(json.RootElement.GetProperty("verifiedQuiescent").GetBoolean());
		var id = json.RootElement.GetProperty("operation").GetProperty("id").GetString();
		Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"{path}/{id}/abort", null)).StatusCode);
		Assert.False(rt.Maintenance.Fenced);
		Assert.NotNull(rt.GetHost(sid));
		await app.StopAsync();
	}
}
