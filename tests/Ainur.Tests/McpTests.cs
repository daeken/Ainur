using Ainur.Core;
using Ainur.Core.Context;
using Ainur.Core.Tools;

namespace Ainur.Tests;

public class McpTests {
	static string ServerScript => Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_mcp_server.py");

	static void Configure(TempHome home, int tools) => File.WriteAllText(Path.Combine(home.Path, "mcp.json"), JsonUtil.Serialize(new {
		servers = new Dictionary<string, object> { ["warehouse"] = new { command = "python3", args = new[] { ServerScript, tools.ToString() } } },
	}));

	[Fact]
	public async Task LargeInventoryIsRegisteredButNotLoadedIntoContext() {
		using var home = new TempHome();
		Configure(home, 300);
		var calls = 0;
		var provider = new FakeProvider((req, n) => Interlocked.Increment(ref calls) switch {
			1 => FakeProvider.Call("load_tools", """{"names":["warehouse__echo_upper"]}"""),
			2 => FakeProvider.Call("warehouse__echo_upper", """{"text":"mcp works"}"""),
			3 => FakeProvider.Call("powershell", """{"script":"warehouse__inventory_007 -bin A7"}"""),
			_ => FakeProvider.Text("done"),
		});
		using var rt = home.Runtime(provider, o => o.PolicyOverride = (_, _) => new ContextPolicy { ToolTokenBudget = 6_000 });
		Assert.Equal(301, rt.Tools.Available("any").Count(t => t.Name.StartsWith("warehouse__")));
		var p = rt.CreateProject("MCP", "d", home.Workspace);
		rt.PostUserMessage(p.Id, "go");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Body == "done"), TimeSpan.FromSeconds(30), "done");
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var results = rt.Store.Items(root.PrimarySessionId!).Where(i => i.Kind == "tool_result").Select(i => JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!).ToList();
		Assert.Equal("MCP WORKS", results[1].Text);
		Assert.Contains("bin A7: 42 units", results[2].Text);
		// Every request kept the declared tool set bounded: never more than a handful of the 301 MCP tools.
		foreach(var req in provider.Requests)
			Assert.True(req.Tools.Count(t => t.Name.StartsWith("warehouse__")) <= 1, $"{req.Tools.Count} tools declared");
		var cache = new ToolCache(rt.Store, rt.Tools, root.PrimarySessionId!);
		Assert.True(cache.RenderedTokens() <= 6_000 + 1_500);
		Assert.True(rt.Tools.Search(p.Id, "warehouse shrinkage audit", 5).Count == 5);
	}
}
