using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Runtime;

namespace Ainur.Tests;

public class PowerShellTests {
	/// <summary>Runs the given tool calls in order through the root manager and returns their result texts.</summary>
	static async Task<(List<ToolResultPayload> Results, AinurRuntime Runtime, string ProjectId)> RunCalls(TempHome home, params (string Tool, object Args)[] calls) {
		var provider = new FakeProvider((req, n) => n <= calls.Length
			? FakeProvider.Call(calls[n - 1].Tool, JsonUtil.Serialize(calls[n - 1].Args), $"call_{n}")
			: FakeProvider.Text("done"));
		var rt = home.Runtime(provider);
		var p = rt.CreateProject("PS", "d", home.Workspace);
		rt.PostUserMessage(p.Id, "go");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Body == "done"), TimeSpan.FromSeconds(60), "calls");
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var results = rt.Store.Items(root.PrimarySessionId!).Where(i => i.Kind == ItemKinds.ToolResult).Select(i => JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!).ToList();
		return (results, rt, p.Id);
	}

	[Fact]
	public async Task ToolsAreFunctionsAndPassLiveObjects() {
		using var home = new TempHome();
		File.WriteAllText(Path.Combine(home.Workspace, "a.txt"), "alpha\nbeta\n");
		File.WriteAllText(Path.Combine(home.Workspace, "b.cs"), "class B {}\n");
		var (results, rt, _) = await RunCalls(home,
			("powershell", new { script = "$files = list_files -glob '**/*.txt'; $files.GetType().Name; $files.Count; $files[0]" }),
			("powershell", new { script = "multi_edit -path a.txt -edits @(@{ old_text = 'beta'; new_text = 'BETA' }) | Out-Null; (read_file -path a.txt)" }),
			("powershell", new { script = "Invoke-AinurTool -Name multi_edit -Arguments @{ path = 'a.txt'; edits = @(@{ old_text = 'missing'; new_text = 'x' }) }" }),
			("powershell", new { script = "$h = [System.Collections.Generic.List[int]]::new(); 1..5 | % { $h.Add($_ * 2) }; Save-AinurObject -Value $h -Summary 'doubles'" }),
			("powershell", new { script = "$h2 = Get-AinurObject -Handle ((list_objects).Split(':')[0..3] -join ':'); $h2.GetType().Name; $h2.Count; $h2[4]" }),
			("powershell", new { script = "Start-Sleep -Seconds 30", timeout_seconds = 2 }),
			("powershell", new { script = "$x = 41; 'set'" }),
			("powershell", new { script = "$x + 1; git --version; $LASTEXITCODE" }));
		using var _ = rt;
		Assert.Contains("List`1", results[0].Text);
		Assert.Contains("a.txt", results[0].Text);
		Assert.Contains("BETA", results[1].Text);
		Assert.Contains("old_text not found", results[2].Text);
		Assert.Equal("alpha\nBETA\n", File.ReadAllText(Path.Combine(home.Workspace, "a.txt")));
		Assert.Matches(@"obj:[0-9a-f]{8}:g1:\d+", results[3].Text);
		Assert.Equal("List`1\n5\n10", results[4].Text.Trim());
		Assert.Contains("timeout", results[5].Text);
		Assert.Contains("42", results[7].Text);
		Assert.Contains("git version", results[7].Text);
		// Nested tool calls made from PowerShell are recorded under the pipeline's invocation.
		var nested = rt.Store.Db.Read(c => Dapper.SqlMapper.Query<string>(c, "SELECT tool_name FROM tool_invocations WHERE parent_invocation_id IS NOT NULL").ToList());
		Assert.Contains("multi_edit", nested);
		Assert.Contains("list_files", nested);
	}

	[Fact]
	public async Task AgentAuthoredScriptToolsComposeAndSurviveRestart() {
		using var home = new TempHome();
		var schema = """{"type":"object","properties":{"word":{"type":"string"},"times":{"type":"integer"}},"required":["word"]}""";
		var (results, rt, projectId) = await RunCalls(home,
			("register_tool", new { name = "shout", description = "Upper-case a word, repeated.", input_schema = schema, script = "param([string]$word, [int]$times = 1)\n(@($word.ToUpper()) * $times) -join ' '", test_arguments = """{"word":"hey","times":2}""" }),
			("shout", new { word = "direct", times = 3 }),
			("powershell", new { script = "shout -word nested -times 2" }),
			("register_tool", new { name = "shout_twice", description = "Calls shout from inside a tool.", input_schema = """{"type":"object","properties":{"word":{"type":"string"}}}""", script = "param([string]$word)\n\"[\" + (shout -word $word -times 2) + \"]\"" }),
			("shout_twice", new { word = "deep" }));
		Assert.Contains("HEY HEY", results[0].Text);
		Assert.Equal("DIRECT DIRECT DIRECT", results[1].Text.Trim());
		Assert.Contains("NESTED NESTED", results[2].Text);
		Assert.Contains("[DEEP DEEP]", results[4].Text);
		var version = rt.Tools.Get("shout")!.Version;
		rt.Dispose();
		using var rt2 = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("x")));
		Assert.Equal(version, rt2.Tools.Get("shout")?.Version);
		Assert.Contains(rt2.Tools.Available(projectId), t => t.Name == "shout_twice");
	}

	[Fact]
	public async Task StaleHandlesAreDetectedAfterRestart() {
		using var home = new TempHome();
		var (results, rt, _) = await RunCalls(home, ("powershell", new { script = "Save-AinurObject -Value ([System.Text.StringBuilder]::new('x')) -Summary sb" }));
		var handle = System.Text.RegularExpressions.Regex.Match(results[0].Text, @"obj:[0-9a-f]{8}:g\d+:\d+").Value;
		rt.Dispose();
		var (results2, rt2, _) = await RunCalls(home, ("powershell", new { script = $"Get-AinurObject -Handle '{handle}'" }));
		using var _ = rt2;
		Assert.Contains("generation", results2.Last().Text);
		Assert.Contains("do not survive restarts", results2.Last().Text);
	}
}
