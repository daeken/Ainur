using System.Text.Json.Nodes;
using Ainur.Core;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;

namespace Ainur.Tests;

public class RuntimeTests {
	static string LastUserText(ProviderRequest r) => r.Messages.Last(m => m.Role == "user").Content ?? "";

	[Fact]
	public async Task ProjectStateSurvivesRestart() {
		using var home = new TempHome();
		string projectId;
		using(var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("ok")))) {
			var p = rt.CreateProject("Test", "A test project", home.Workspace);
			projectId = p.Id;
		}
		using var rt2 = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("ok")));
		var project = rt2.Store.GetProject(projectId)!;
		Assert.Equal("Test", project.Name);
		var root = rt2.Store.GetAgent(project.RootAgentId!)!;
		Assert.Equal(Roles.Manager, root.Role);
		Assert.Null(root.ManagerId);
		var objective = rt2.Store.GetObjective(project.RootObjectiveId!)!;
		Assert.Equal(root.Id, objective.OwnerId);
		Assert.NotNull(rt2.Store.CurrentIdentity(root.Id));
		Assert.Contains(rt2.Store.Events(projectId), e => e.Kind == "project.created");
		await Task.CompletedTask;
	}

	[Fact]
	public async Task UserMessageRunsToolsAndRepliesToUser() {
		using var home = new TempHome();
		var provider = new FakeProvider((req, n) => n switch {
			1 => FakeProvider.Call("write_file", """{"path":"hello.txt","content":"hi there\n"}"""),
			2 => FakeProvider.Call("multi_edit", """{"path":"hello.txt","edits":[{"old_text":"hi","new_text":"hello"}],"append":"bye\n"}"""),
			_ => FakeProvider.Text("Done: hello.txt written."),
		});
		using var rt = home.Runtime(provider);
		var p = rt.CreateProject("T", "d", home.Workspace);
		rt.PostUserMessage(p.Id, "write hello.txt please");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Count >= 2, TimeSpan.FromSeconds(20), "manager reply");
		Assert.Equal("hello there\nbye\n", File.ReadAllText(Path.Combine(home.Workspace, "hello.txt")));
		var conv = rt.Store.Conversation(p.Id);
		Assert.Equal("Done: hello.txt written.", conv[^1].Body);
		// Every call got a result, and costs were recorded once per request.
		var costs = rt.Store.CostEvents(p.Id);
		Assert.Equal(3, costs.Count);
		Assert.All(costs, c => Assert.True(c.EffectiveNanos > 0));
		var reqs = provider.Requests.ToList();
		Assert.Contains(reqs[1].Messages, m => m.Role == "tool");
	}

	[Fact]
	public async Task DelegationWakesSpecialistAndReturnsResult() {
		using var home = new TempHome();
		var provider = new FakeProvider((req, n) => {
			var system = req.Messages[0].Content!;
			var last = LastUserText(req);
			if(system.Contains("You are Manwë")) {
				if(last.Contains("[Inbox]") && last.Contains("user_message") && !req.Messages.Any(m => m.Role == "tool"))
					return FakeProvider.Call("create_agent", """{"name":"Aulë","title":"Implementer","role":"specialist","instructions":"Implement things."}""");
				if(req.Messages[^1].Role == "tool" && req.Messages[^1].Content!.Contains("Created"))
					return FakeProvider.Call("assign_work", """{"agent":"Aulë","title":"Write impl.txt","completion_conditions":"impl.txt exists","message":"Write impl.txt with the word forged."}""");
				if(req.Messages[^1].Role == "tool")
					return FakeProvider.Text("");
				if(last.Contains("result from Aulë"))
					return FakeProvider.Text("Aulë finished: impl.txt forged.");
				return FakeProvider.Text("");
			}
			// Specialist
			if(req.Messages[^1].Role == "user")
				return FakeProvider.Call("write_file", """{"path":"impl.txt","content":"forged"}""");
			var tools = req.Messages.Count(m => m.Role == "tool");
			if(tools == 1) {
				var objectiveId = System.Text.RegularExpressions.Regex.Match(last, @"obj_[0-9a-f]+").Value;
				return FakeProvider.Call("update_objective", $$"""{"objective_id":"{{objectiveId}}","state":"complete","add_evidence":"impl.txt written"}""");
			}
			if(tools == 2) return FakeProvider.Call("send_message", """{"to":"Manwë","type":"result","body":"impl.txt forged"}""");
			return FakeProvider.Text("reported");
		});
		using var rt = home.Runtime(provider);
		var p = rt.CreateProject("T", "d", home.Workspace);
		rt.PostUserMessage(p.Id, "get impl.txt made");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Body.Contains("forged")), TimeSpan.FromSeconds(30), "delegated result");
		var agents = rt.Store.ListAgents(p.Id);
		var aule = agents.Single(a => a.Name == "Aulë");
		Assert.Equal(p.RootAgentId, aule.ManagerId);
		Assert.Equal("forged", File.ReadAllText(Path.Combine(home.Workspace, "impl.txt")));
		var obj = rt.Store.ObjectivesOwnedBy(aule.Id).Single();
		Assert.Equal(ObjectiveStates.Verifying, obj.State); // owners cannot accept their own delegated work
		Assert.Equal(p.RootObjectiveId, obj.ParentId);
		var byAgent = rt.Ledger.ByAgent(p.Id);
		Assert.True(byAgent[aule.Id].Direct > 0);
		Assert.Equal(byAgent[aule.Id].Direct, byAgent[p.RootAgentId!].Delegated);
	}

	[Fact]
	public void ReorganizationRejectsCycles() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("")), o => o.AutoStartHosts = false);
		var p = rt.CreateProject("T", "d", home.Workspace);
		var a = rt.CreateAgent(p.Id, new() { Name = "A", Role = Roles.Manager, ManagerId = p.RootAgentId }, null);
		var b = rt.CreateAgent(p.Id, new() { Name = "B", Role = Roles.Manager, ManagerId = a.Id }, null);
		var ex = Assert.Throws<DomainException>(() => rt.Db.Write(u => rt.Store.Reassign(u, a.Id, b.Id, null)));
		Assert.Contains("cycle", ex.Message);
		Assert.Throws<DomainException>(() => rt.Db.Write(u => rt.Store.Reassign(u, p.RootAgentId!, a.Id, null)));
		rt.RetireAgent(a.Id, p.RootAgentId!, null, "test");
		Assert.Equal(p.RootAgentId, rt.Store.GetAgent(b.Id)!.ManagerId);
	}

	[Fact]
	public void ObjectiveRulesHold() {
		using var home = new TempHome();
		using var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("")), o => o.AutoStartHosts = false);
		var p = rt.CreateProject("T", "d", home.Workspace);
		Objective New(string title, string? parent) => new() { Id = Ids.New("obj"), ProjectId = p.Id, ParentId = parent, OwnerId = p.RootAgentId, Title = title, State = "active", CreatedAt = 1, UpdatedAt = 1 };
		var parent = New("parent", p.RootObjectiveId);
		var child = New("child", parent.Id);
		var other = New("other", p.RootObjectiveId);
		rt.Db.Write(u => { rt.Store.InsertObjective(u, parent, null); rt.Store.InsertObjective(u, child, null); rt.Store.InsertObjective(u, other, null); });
		rt.Db.Write(u => rt.Store.AddDependency(u, parent.Id, other.Id, null));
		Assert.Throws<DomainException>(() => rt.Db.Write(u => rt.Store.AddDependency(u, other.Id, parent.Id, null)));
		parent.State = "complete";
		parent.Evidence = """[{"evidence":"x"}]""";
		var ex = Assert.Throws<DomainException>(() => rt.Db.Write(u => rt.Store.UpdateObjective(u, parent, null)));
		Assert.Contains("required children", ex.Message);
		child.Required = false;
		rt.Db.Write(u => rt.Store.UpdateObjective(u, child, null));
		rt.Db.Write(u => rt.Store.UpdateObjective(u, parent, null));
		Assert.Equal("complete", rt.Store.GetObjective(parent.Id)!.State);
		other.State = "complete";
		Assert.Throws<DomainException>(() => rt.Db.Write(u => rt.Store.UpdateObjective(u, other, null)));
	}

	[Fact]
	public async Task RecoveryMarksInterruptedInvocationUnknownAndAnswersCall() {
		using var home = new TempHome();
		string sessionId, projectId;
		using(var rt = home.Runtime(new FakeProvider((_, _) => FakeProvider.Text("")), o => o.AutoStartHosts = false)) {
			var p = rt.CreateProject("T", "d", home.Workspace);
			projectId = p.Id;
			var root = rt.Store.GetAgent(p.RootAgentId!)!;
			sessionId = root.PrimarySessionId!;
			// Simulate a crash mid-tool: an assistant call with a running invocation and no result.
			rt.Db.Write(u => {
				rt.Store.AppendItem(u, sessionId, ItemKinds.User, new UserPayload { Text = "do it" }, 5);
				rt.Store.AppendItem(u, sessionId, ItemKinds.Assistant, new AssistantPayload { ToolCalls = [new ToolCall("c1", "powershell", "{\"script\":\"x\"}"), new ToolCall("c2", "read_file", "{}")] }, 10);
				rt.Store.InsertInvocation(u, new ToolInvocation { Id = "inv_crash", ProjectId = p.Id, SessionId = sessionId, AgentId = root.Id, CallId = "c1", ToolName = "powershell", ToolVersion = "powershell@x", State = "running", CreatedAt = 1 });
			});
		}
		var calls = 0;
		using var rt2 = home.Runtime(new FakeProvider((req, _) => { Interlocked.Increment(ref calls); return FakeProvider.Text("reconciled"); }));
		Assert.Equal("unknown", rt2.Store.GetInvocation("inv_crash")!.State);
		var results = rt2.Store.Items(sessionId).Where(i => i.Kind == ItemKinds.ToolResult).Select(i => Ainur.Core.Json.Deserialize<ToolResultPayload>(i.Payload)!).ToList();
		Assert.Contains(results, r => r.CallId == "c1" && r.Text.StartsWith("OUTCOME UNKNOWN"));
		Assert.Contains(results, r => r.CallId == "c2" && r.Text.StartsWith("NOT EXECUTED"));
		// The session resumes and answers; the hung invocation was not retried.
		await Wait.Until(() => rt2.Store.Conversation(projectId).Any(c => c.Body == "reconciled"), TimeSpan.FromSeconds(10), "resume");
	}

	[Fact]
	public async Task BudgetExhaustionPausesInsteadOfLooping() {
		using var home = new TempHome();
		var provider = new FakeProvider((_, _) => FakeProvider.Text("hi"));
		using var rt = home.Runtime(provider);
		var p = rt.CreateProject("T", "d", home.Workspace, budgetDollars: 0.000001m);
		rt.PostUserMessage(p.Id, "hello");
		var rootId = p.RootAgentId!;
		await Wait.Until(() => rt.Store.GetAgent(rootId)!.State == AgentStates.Paused, TimeSpan.FromSeconds(10), "pause");
		Assert.Empty(provider.Requests);
		Assert.Contains(rt.Store.Conversation(p.Id), c => c.Author == "system" && c.Body.Contains("budget"));
	}

	[Fact]
	public async Task RollingCompactionPreservesSuffixVerbatim() {
		using var home = new TempHome();
		var turns = 0;
		var provider = new FakeProvider((req, n) => {
			if(req.Messages[0].Content!.StartsWith("You are the context compactor"))
				return FakeProvider.Text("SUMMARY: earlier work wrote files.");
			return Interlocked.Increment(ref turns) <= 12 ? FakeProvider.Call("powershell", $$"""{"script":"'{{new string('x', 6000)}}{{n}}'"}""") : FakeProvider.Text("finished");
		});
		using var rt = home.Runtime(provider, o => o.PolicyOverride = (a, s) => new ContextPolicy { MaxContextTokens = 26_000, ReservedOutputTokens = 2_000, TriggerFraction = 0.6, RollingFraction = 0.5, ToolTokenBudget = 30_000, ElideAfterTurns = 100 });
		var p = rt.CreateProject("T", "d", home.Workspace);
		rt.PostUserMessage(p.Id, "loop");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Body == "finished"), TimeSpan.FromSeconds(60), "finish");
		var root = rt.Store.GetAgent(p.RootAgentId!)!;
		var view = rt.CurrentView(root.PrimarySessionId!);
		Assert.NotNull(view.SummaryItemId);
		Assert.True(view.CutoffSeq > 0);
		var last = provider.Requests.Last(r => !r.Messages[0].Content!.StartsWith("You are the context compactor"));
		Assert.Contains(last.Messages, m => m.Role == "user" && m.Content!.Contains("SUMMARY: earlier work"));
		// The newest exchanges are verbatim and every tool message follows its assistant call.
		Assert.Contains(last.Messages, m => m.Role == "tool" && m.Content!.Contains(new string('x', 100)));
		for(var i = 0; i < last.Messages.Count; i++)
			if(last.Messages[i].Role == "tool")
				Assert.True(last.Messages[i - 1].Role is "tool" or "assistant");
		var items = rt.Store.Items(root.PrimarySessionId!);
		Assert.Contains(items, i => i.Seq <= view.CutoffSeq && i.Kind == ItemKinds.ToolResult);
	}

	[Fact]
	public async Task AutomaticElisionAfterNTurns() {
		using var home = new TempHome();
		var provider = new FakeProvider((req, n) => n <= 5 ? FakeProvider.Call("powershell", $$"""{"script":"'result-{{n}}'"}""") : FakeProvider.Text("done"));
		using var rt = home.Runtime(provider, o => o.PolicyOverride = (a, s) => new ContextPolicy { ElideAfterTurns = 2 });
		var p = rt.CreateProject("T", "d", home.Workspace);
		rt.PostUserMessage(p.Id, "go");
		await Wait.Until(() => rt.Store.Conversation(p.Id).Any(c => c.Body == "done"), TimeSpan.FromSeconds(30), "done");
		var last = provider.Requests.Last();
		var tools = last.Messages.Where(m => m.Role == "tool").ToList();
		Assert.Equal(5, tools.Count);
		Assert.StartsWith("[elided result", tools[0].Content);
		Assert.StartsWith("[elided result", tools[1].Content);
		Assert.Contains("result-3", tools[2].Content);
		Assert.Contains("result-4", tools[3].Content);
		Assert.Contains("result-5", tools[4].Content);
	}
}
