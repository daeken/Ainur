using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;

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
	public async Task CustomObjectsKeepPropertiesIdentityAndClrCollectionsAcrossCalls() {
		using var home = new TempHome();
		var (results, rt, _) = await RunCalls(home,
			("powershell", new { script = "$obj = [pscustomobject]@{ Name = 'smoke'; Nested = [pscustomobject]@{ Count = 7 }; Items = @([pscustomobject]@{ Label = 'first' }) }; $obj.PSObject.TypeNames.Insert(0, 'Ainur.Smoke'); $saved = Save-AinurObject -Value $obj -Summary 'custom'; $saved" }),
			("powershell", new { script = "$copy = Get-AinurObject -Handle $saved; $copy.Name; $copy.Nested.Count; $copy.Items[0].Label; $copy.PSObject.TypeNames[0]; $copy.Name = 'changed'; $obj.Name; $list = [System.Collections.Generic.List[int]]::new(); $list.Add(3); $lh = Save-AinurObject -Value $list; $lc = Get-AinurObject -Handle $lh; [object]::ReferenceEquals($list, $lc); $lc.Add(4); $list.Count" }));
		using var _ = rt;
		Assert.False(results[1].IsError);
		Assert.Equal("smoke\n7\nfirst\nAinur.Smoke\nchanged\nTrue\n2", results[1].Text.Trim());
	}

	[Fact]
	public async Task NestedCustomObjectsProjectIntoToolArguments() {
		using var home = new TempHome();
		File.WriteAllText(Path.Combine(home.Workspace, "custom.txt"), "before");
		var (results, rt, _) = await RunCalls(home,
			("powershell", new { script = "Invoke-AinurTool -Name multi_edit -Arguments ([pscustomobject]@{ path = 'custom.txt'; edits = @([pscustomobject]@{ old_text = 'before'; new_text = 'after' }) })" }));
		using var _ = rt;
		Assert.False(results[0].IsError);
		Assert.Equal("after", File.ReadAllText(Path.Combine(home.Workspace, "custom.txt")));
	}

	sealed class CustomResultTool : BuiltinTool {
		public override string Name => "custom_result";
		public override string Description => "Return a custom object";
		public override JsonObject InputSchema => new() { ["type"] = "object" };
		public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
			var value = new System.Management.Automation.PSObject();
			value.Properties.Add(new System.Management.Automation.PSNoteProperty("Name", "tool-return"));
			return Task.FromResult(new ToolResult { Text = "custom", Value = value });
		}
	}

	[Fact]
	public async Task LastOutputAndToolReturnKeepCustomObjectWrapper() {
		using var home = new TempHome();
		var (_, rt, projectId) = await RunCalls(home,
			("powershell", new { script = "[pscustomobject]@{ Name = 'last-output' }" }));
		using var _ = rt;
		var root = rt.Store.GetAgent(rt.Store.GetProject(projectId)!.RootAgentId!)!;
		var host = rt.GetHost(root.PrimarySessionId!)!;
		var saved = host.Objects.List().First();
		var restored = host.PowerShell.Run($"$stored = Get-AinurObject -Handle '{saved.Handle}'; $stored.Name", TimeSpan.FromSeconds(10), default);
		Assert.False(restored.HadErrors);
		Assert.Equal("last-output", restored.Text.Trim());
		rt.Tools.Register(new CustomResultTool());
		var returned = host.PowerShell.Run("$direct = custom_result; $viaInvoke = Invoke-AinurTool -Name custom_result; $direct.Name; $viaInvoke.Name", TimeSpan.FromSeconds(10), default);
		Assert.False(returned.HadErrors);
		Assert.Equal("tool-return\ntool-return", returned.Text.Trim());
	}

	[Fact]
	public void CustomObjectProjectionIsRecursiveAndLeavesClrCollectionsIntact() {
		var child = new System.Management.Automation.PSObject();
		child.Properties.Add(new System.Management.Automation.PSNoteProperty("count", 7));
		var parent = new System.Management.Automation.PSObject();
		parent.Properties.Add(new System.Management.Automation.PSNoteProperty("nested", child));
		parent.Properties.Add(new System.Management.Automation.PSNoteProperty("items", new object[] { child, new List<int> { 2, 3 } }));
		Assert.Equal("{\"nested\":{\"count\":7},\"items\":[{\"count\":7},[2,3]]}", PowerShellHost.ToJson(parent)!.ToJsonString());
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

public class ToolVersionTests {
	[Fact]
	public async Task CallsResolveAgainstTheVersionDeclaredInTheRequest() {
		using var home = new TempHome();
		Ainur.Core.Runtime.AinurRuntime? runtime = null;
		Ainur.Core.Tools.ScriptTool Make(string body) => new("greet", "Greets.", Ainur.Core.Tools.Schema.Object(), body, [], TimeSpan.FromSeconds(10));
		var provider = new FakeProvider((req, n) => {
			if(n == 1) {
				Assert.Contains(req.Tools, t => t.Name == "greet");
				// The tool is revised after the request was dispatched but before its call executes.
				runtime!.Tools.Register(Make("'v2'"), kind: "powershell");
				return FakeProvider.Call("greet", "{}");
			}
			return n == 2 ? FakeProvider.Call("greet", "{}") : FakeProvider.Text("done");
		});
		runtime = home.Runtime(provider, start: false);
		using var rt = runtime;
		rt.Tools.Register(Make("'v1'"), kind: "powershell");
		rt.Start("test");
		var p = rt.CreateProject("V", "d", home.Workspace);
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		new Ainur.Core.Tools.ToolCache(rt.Store, rt.Tools, root.PrimarySessionId!).Load(["greet"], 100_000);
		rt.PostUserMessage(p.Id, "go");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Body == "done"), TimeSpan.FromSeconds(20), "done");
		var results = rt.Store.Items(root.PrimarySessionId!).Where(i => i.Kind == "tool_result").Select(i => JsonUtil.Deserialize<Ainur.Core.Context.ToolResultPayload>(i.Payload)!).ToList();
		Assert.Equal("v1", results[0].Text.Trim());
		Assert.Equal("v2", results[1].Text.Trim());
	}
}
