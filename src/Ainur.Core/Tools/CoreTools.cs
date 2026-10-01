using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Providers;
using Ainur.Core.Runtime;

namespace Ainur.Core.Tools;

public static class BuiltinTools {
	public static void RegisterAll(AinurRuntime rt) {
		ITool[] tools = [
			new PowerShellTool(), new MultiEditTool(), new ReadFileTool(), new WriteFileTool(), new ListFilesTool(), new SearchTextTool(),
			new FindToolsTool(), new LoadToolsTool(), new ReadResultTool(), new ElideResultsTool(), new RetainResultTool(), new ReadHistoryTool(),
			new SendMessageTool(), new ReplyToUserTool(), new TeamTool(), new CreateAgentTool(), new AssignWorkTool(), new RetireAgentTool(),
			new ReassignAgentTool(), new PauseAgentTool(), new ResumeAgentTool(), new ConsultTool(),
			new ObjectivesTool(), new CreateObjectiveTool(), new UpdateObjectiveTool(),
			new ReadIdentityTool(), new WriteIdentityTool(), new CostsTool(),
			new KnowledgeSearchTool(), new KnowledgeReadTool(), new KnowledgeWriteTool(), new AskKnowledgeTool(),
			new RegisterToolTool(), new ListObjectsTool(),
		];
		foreach(var tool in tools) rt.Tools.Register(tool);
	}
}

public sealed class PowerShellTool : BuiltinTool {
	public override string Name => "powershell";
	public override string Description => """
		Run a script in your persistent embedded PowerShell 7 session (the only shell). Variables and location persist across calls.
		Every tool available to you is a function with the same name and parameters (e.g. multi_edit -path f -edits @(@{old_text='a';new_text='b'})),
		returning live .NET objects; Invoke-AinurTool -Name n -Arguments @{...} also works. Native programs (git, dotnet, npm, rg) run normally;
		check $LASTEXITCODE. Output is formatted text; save objects for later turns with Save-AinurObject (returns a handle) and Get-AinurObject.
		""";
	public override TimeSpan Timeout => TimeSpan.FromMinutes(30);
	public override IReadOnlyList<string> Tags => ["shell", "command", "script", "run", "git", "build", "test", "dotnet", "npm", "process", "terminal"];
	public override JsonObject InputSchema => Schema.Object(
		("script", Schema.String("PowerShell script to run."), true),
		("timeout_seconds", Schema.Integer("Deadline in seconds (default 600, max 1800)."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var script = Str(args, "script");
		var timeout = TimeSpan.FromSeconds(Math.Clamp(OptInt(args, "timeout_seconds") ?? 600, 1, 1800));
		var result = ctx.Host.PowerShell.Run(script, timeout, ctx.CancellationToken);
		return Task.FromResult(new ToolResult {
			Text = result.Text, IsError = false, Value = result.LastValue,
			Description = $"powershell: {TextUtil.Truncate(script.Split('\n')[0], 80)}{(result.HadErrors ? " (with errors)" : "")}",
		});
	}
}

public sealed class FindToolsTool : BuiltinTool {
	public override string Name => "find_tools";
	public override string Description => """
		Search the complete tool registry (built-in and agent-authored tools) for tools matching a need. A small, inexpensive model
		selects the best candidates; selected tools are loaded into your tool set for the next step, with their exact schemas.
		""";
	public override TimeSpan Timeout => TimeSpan.FromMinutes(3);
	public override IReadOnlyList<string> Tags => ["tools", "discover", "search", "registry"];
	public override JsonObject InputSchema => Schema.Object(
		("need", Schema.String("What you are trying to do."), true),
		("load", Schema.Boolean("Load the selected tools (default true)."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var need = Str(args, "need");
		var rt = ctx.Runtime;
		var candidates = rt.Tools.Search(ctx.Project.Id, need, 15);
		if(candidates.Count == 0)
			candidates = rt.Tools.Available(ctx.Project.Id).Select(t => (t, 0.0)).ToList();
		var selected = candidates.Take(3).Select(c => c.Tool.Name).ToList();
		var rationale = "keyword ranking";
		var model = rt.Store.GetModel(rt.Options.CheapModelId);
		if(model is not null && candidates.Count > 1) {
			var listing = string.Join("\n", candidates.Select(c => $"- {c.Tool.Name}: {TextUtil.Truncate(c.Tool.Description.ReplaceLineEndings(" "), 300)}"));
			try {
				var r = await rt.Gateway.CallAsync(new ModelCall {
					ProjectId = ctx.Project.Id, AgentId = ctx.Agent.Id, SessionId = ctx.Session.Id, Purpose = "tool_finder", Category = "tool_finder", Model = model,
					Messages = [
						ChatMessage.System("You select tools from a registry. Reply with JSON only: {\"tools\":[names...],\"why\":\"one sentence\"}. Choose at most 4 tools that directly serve the need, best first. Choose none if nothing fits. Never invent names."),
						ChatMessage.User($"Need: {need}\n\nCandidates:\n{listing}"),
					],
					MaxOutputTokens = 400, ReasoningEffort = "none",
				}, ctx.CancellationToken);
				var text = r.Response.Content ?? "";
				var json = JsonNode.Parse(text[text.IndexOf('{')..(text.LastIndexOf('}') + 1)]);
				var names = (json?["tools"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).Where(n => n is not null && candidates.Any(c => c.Tool.Name == n)).Select(n => n!).ToList();
				selected = names;
				rationale = json?["why"]?.GetValue<string>() ?? "model selection";
			} catch(Exception e) when(e is not OperationCanceledException) {
				rationale = $"keyword ranking (finder model unavailable: {TextUtil.Truncate(e.Message, 120)})";
			}
		}
		if(selected.Count == 0)
			return ToolResult.Ok($"No registered tool fits \"{need}\". Consider a different approach, powershell, or register_tool to create one.", description: "find_tools: no match");
		var sb = new StringBuilder($"Selected ({rationale}):\n");
		foreach(var name in selected) {
			var tool = rt.Tools.Get(name)!;
			sb.Append($"\n## {tool.Name} [{tool.Version}]\n{tool.Description.Trim()}\nInput schema: {tool.InputSchema.ToJsonString()}\n");
		}
		if(OptBool(args, "load") ?? true) {
			var (loaded, evicted, _) = ctx.Host.Cache.Load(selected, rt.PolicyFor(ctx.Agent, ctx.Session).ToolTokenBudget);
			sb.Append($"\nLoaded for your next step: {string.Join(", ", loaded)}.");
			if(evicted.Count > 0) sb.Append($" Evicted (least recently used): {string.Join(", ", evicted)}; find_tools or load_tools reloads them.");
		}
		var others = candidates.Select(c => c.Tool.Name).Except(selected).Take(8).ToList();
		if(others.Count > 0) sb.Append($"\nOther candidates: {string.Join(", ", others)}.");
		return ToolResult.Ok(sb.ToString(), selected, $"find_tools \"{TextUtil.Truncate(need, 60)}\" → {string.Join(", ", selected)}");
	}
}

public sealed class LoadToolsTool : BuiltinTool {
	public override string Name => "load_tools";
	public override string Description => "Load registered tools by exact name into your tool set for the next step (least recently used tools are evicted to stay within the tool budget), or list what is loaded.";
	public override IReadOnlyList<string> Tags => ["tools", "load", "cache"];
	public override JsonObject InputSchema => Schema.Object(
		("names", Schema.Array("Tool names to load. Omit to list loaded tools.", Schema.String("tool name")), false),
		("unload", Schema.Array("Tool names to unload.", Schema.String("tool name")), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var budget = ctx.Runtime.PolicyFor(ctx.Agent, ctx.Session).ToolTokenBudget;
		var sb = new StringBuilder();
		var unload = StrList(args, "unload");
		if(unload.Count > 0) {
			var pinned = ctx.Host.Cache.Entries().Where(e => e.Pinned).Select(e => e.Name).ToHashSet();
			ctx.Runtime.Store.Db.Write(u => {
				foreach(var name in unload.Where(n => !pinned.Contains(n)))
					u.Execute("DELETE FROM tool_cache WHERE session_id=@sid AND tool_name=@name", new { sid = ctx.Session.Id, name });
			});
			sb.Append($"Unloaded: {string.Join(", ", unload.Where(n => !pinned.Contains(n)))}\n");
		}
		var names = StrList(args, "names");
		if(names.Count > 0) {
			var (loaded, evicted, missing) = ctx.Host.Cache.Load(names, budget);
			sb.Append($"Loaded: {string.Join(", ", loaded)}\n");
			if(missing.Count > 0) sb.Append($"Unknown: {string.Join(", ", missing)} (use find_tools)\n");
			if(evicted.Count > 0) sb.Append($"Evicted: {string.Join(", ", evicted)}\n");
		}
		var entries = ctx.Host.Cache.Entries();
		sb.Append($"Loaded tools ({ctx.Host.Cache.RenderedTokens()} of {budget} tool tokens): {string.Join(", ", entries.OrderBy(e => e.Name).Select(e => e.Name + (e.Pinned ? "*" : "")))} (* pinned)");
		return Task.FromResult(ToolResult.Ok(sb.ToString(), description: "load_tools"));
	}
}

public sealed class ReadResultTool : BuiltinTool {
	public override string Name => "read_result";
	public override string Description => "Retrieve a bounded excerpt of an earlier tool result by invocation id (works for elided or truncated results). Choose a character range or a line range, or a regex to show matching lines.";
	public override IReadOnlyList<string> Tags => ["context", "result", "excerpt", "elided"];
	public override JsonObject InputSchema => Schema.Object(
		("invocation_id", Schema.String("Invocation id (inv_...)."), true),
		("start_line", Schema.Integer("First line (1-based)."), false),
		("line_count", Schema.Integer("Number of lines (default 200)."), false),
		("offset", Schema.Integer("Character offset (alternative to lines)."), false),
		("length", Schema.Integer("Characters to return (default 8000, max 20000)."), false),
		("grep", Schema.String("Regex; return matching lines with line numbers instead of a range."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var id = Str(args, "invocation_id");
		var inv = ctx.Runtime.Store.GetInvocation(id) ?? throw new ToolException($"Unknown invocation {id}");
		if(inv.ProjectId != ctx.Project.Id) throw new ToolException("That invocation belongs to another project");
		if(inv.ResultArtifact is null) throw new ToolException($"Invocation {id} has no stored result ({inv.State})");
		var text = ctx.Runtime.Artifacts.GetText(inv.ResultArtifact);
		string excerpt;
		if(OptStr(args, "grep") is { } pattern) {
			var re = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
			var lines = text.Split('\n');
			excerpt = string.Join("\n", lines.Select((l, i) => (l, i)).Where(x => re.IsMatch(x.l)).Take(300).Select(x => $"{x.i + 1}: {TextUtil.Truncate(x.l, 400)}"));
			if(excerpt.Length == 0) excerpt = "[no matching lines]";
		} else if(OptInt(args, "start_line") is { } start) {
			var lines = text.Split('\n');
			var count = Math.Clamp(OptInt(args, "line_count") ?? 200, 1, 2000);
			excerpt = string.Join("\n", lines.Skip(start - 1).Take(count)) + $"\n[lines {start}-{Math.Min(lines.Length, start + count - 1)} of {lines.Length}]";
		} else {
			var offset = Math.Clamp(OptInt(args, "offset") ?? 0, 0, text.Length);
			var length = Math.Clamp(OptInt(args, "length") ?? 8000, 1, 20_000);
			var end = Math.Min(text.Length, offset + length);
			excerpt = text[offset..end] + $"\n[chars {offset}-{end} of {text.Length}]";
		}
		return Task.FromResult(ToolResult.Ok(TextUtil.Truncate(excerpt, 20_000), description: $"excerpt of {id} ({inv.ToolName})"));
	}
}

public sealed class ElideResultsTool : BuiltinTool {
	public override string Name => "elide_results";
	public override string Description => "Replace earlier tool results in your context with short elision records (the raw results stay retrievable with read_result). Use after extracting what you need from large output.";
	public override IReadOnlyList<string> Tags => ["context", "elide", "forget", "compact"];
	public override JsonObject InputSchema => Schema.Object(
		("invocation_ids", Schema.Array("Invocations to elide.", Schema.String("inv_...")), false),
		("all_before_latest", Schema.Boolean("Elide every completed result except this call's batch."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var ids = StrList(args, "invocation_ids").ToHashSet();
		var all = OptBool(args, "all_before_latest") ?? false;
		var items = ctx.Runtime.Store.Items(ctx.Session.Id).Where(i => i.Kind == ItemKinds.ToolResult).ToList();
		var view = ctx.Runtime.CurrentView(ctx.Session.Id);
		var elided = new List<string>();
		foreach(var item in items) {
			var p = JsonUtil.Deserialize<ToolResultPayload>(item.Payload)!;
			if(all || ids.Contains(p.InvocationId)) {
				view.Elided.Add(item.Id);
				view.RetainedUntil.Remove(item.Id);
				elided.Add(p.InvocationId);
			}
		}
		var missing = ids.Except(elided).ToList();
		ctx.Runtime.CommitView(ctx.Session.Id, view, $"explicit elision of {elided.Count} result(s)");
		return Task.FromResult(ToolResult.Ok($"Elided {elided.Count} result(s).{(missing.Count > 0 ? $" Not found in this session: {string.Join(", ", missing)}" : "")}", description: "elide_results"));
	}
}

public sealed class RetainResultTool : BuiltinTool {
	public override string Name => "retain_result";
	public override string Description => "Keep specific tool results visible beyond the automatic elision age for a number of additional turns (within the context budget).";
	public override IReadOnlyList<string> Tags => ["context", "retain", "keep", "pin"];
	public override JsonObject InputSchema => Schema.Object(
		("invocation_ids", Schema.Array("Invocations to retain.", Schema.String("inv_...")), true),
		("turns", Schema.Integer("Additional turns to keep them (default 10, max 50)."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var ids = StrList(args, "invocation_ids").ToHashSet();
		var until = ctx.Session.TurnCount + Math.Clamp(OptInt(args, "turns") ?? 10, 1, 50);
		var view = ctx.Runtime.CurrentView(ctx.Session.Id);
		var n = 0;
		foreach(var item in ctx.Runtime.Store.Items(ctx.Session.Id).Where(i => i.Kind == ItemKinds.ToolResult))
			if(ids.Contains(JsonUtil.Deserialize<ToolResultPayload>(item.Payload)!.InvocationId)) {
				view.RetainedUntil[item.Id] = until;
				view.Elided.Remove(item.Id);
				n++;
			}
		ctx.Runtime.CommitView(ctx.Session.Id, view, $"retain {n} result(s) until turn {until}");
		return Task.FromResult(ToolResult.Ok($"Retaining {n} result(s) through turn {until}.", description: "retain_result"));
	}
}

public sealed class ReadHistoryTool : BuiltinTool {
	public override string Name => "read_history";
	public override string Description => "Read original transcript items of your session by sequence number (#N), including items folded into a compaction summary.";
	public override IReadOnlyList<string> Tags => ["context", "history", "transcript", "summary", "compaction"];
	public override JsonObject InputSchema => Schema.Object(
		("from_seq", Schema.Integer("First item number."), true),
		("to_seq", Schema.Integer("Last item number (default from_seq + 20)."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var from = OptInt(args, "from_seq") ?? 1;
		var to = OptInt(args, "to_seq") ?? from + 20;
		var items = ctx.Runtime.Store.Items(ctx.Session.Id, from - 1, Math.Min(to, from + 100));
		return Task.FromResult(ToolResult.Ok(TextUtil.Truncate(ContextBuilder.RenderTranscript(items, 3000), 30_000), description: $"history #{from}-#{to}"));
	}
}

public sealed class ListObjectsTool : BuiltinTool {
	public override string Name => "list_objects";
	public override string Description => "List live .NET objects retained in this session (handles usable from PowerShell via Get-AinurObject), or release one.";
	public override IReadOnlyList<string> Tags => ["objects", "handles", "powershell", "live"];
	public override JsonObject InputSchema => Schema.Object(("release", Schema.String("Handle to release."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		if(OptStr(args, "release") is { } handle)
			return Task.FromResult(ToolResult.Ok(ctx.Host.Objects.Release(handle) ? $"Released {handle}" : $"{handle} was not registered"));
		var list = ctx.Host.Objects.List();
		return Task.FromResult(ToolResult.Ok(list.Count == 0 ? "[no live objects]" : string.Join("\n", list.Select(o => $"{o.Handle}: {o.TypeName} — {o.Summary}"))));
	}
}

/// <summary>Registers an agent-authored PowerShell tool. Versions are content-addressed and immutable.</summary>
public sealed class RegisterToolTool : BuiltinTool {
	public override string Name => "register_tool";
	public override string Description => """
		Create or revise a tool for this project, implemented as a PowerShell script block that receives its arguments as
		parameters (declare them in a param() block matching the schema) and returns objects or text. It runs inline in the
		caller's PowerShell session with access to every other tool. Provide a clear description and JSON input schema.
		A new definition with an existing name becomes a new immutable version. Validate with test_arguments before relying on it.
		""";
	public override IReadOnlyList<string> Tags => ["tools", "create", "register", "author", "script", "extend"];
	public override JsonObject InputSchema => Schema.Object(
		("name", Schema.String("Tool name: lowercase letters, digits, underscores."), true),
		("description", Schema.String("What the tool does, for the model and the tool finder."), true),
		("input_schema", Schema.String("JSON Schema object for the arguments, as a JSON string."), true),
		("script", Schema.String("PowerShell script: param(...) block then body."), true),
		("tags", Schema.Array("Search tags.", Schema.String("tag")), false),
		("timeout_seconds", Schema.Integer("Deadline (default 300)."), false),
		("test_arguments", Schema.String("Optional JSON object to run the new tool with once as validation."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var name = Str(args, "name");
		if(!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z][a-z0-9_]{1,48}$")) throw new ToolException("Tool names must match ^[a-z][a-z0-9_]{1,48}$");
		if(ctx.Runtime.Tools.Get(name) is BuiltinTool) throw new ToolException($"'{name}' is a built-in tool; choose another name");
		var schema = JsonNode.Parse(Str(args, "input_schema")) as JsonObject ?? throw new ToolException("input_schema must be a JSON object");
		var tool = new ScriptTool(name, Str(args, "description"), schema, Str(args, "script"), StrList(args, "tags"), TimeSpan.FromSeconds(Math.Clamp(OptInt(args, "timeout_seconds") ?? 300, 1, 1800)));
		var parse = System.Management.Automation.Language.Parser.ParseInput(tool.Script, out _, out var errors);
		if(errors.Length > 0) throw new ToolException("Script does not parse:\n" + string.Join("\n", errors.Select(e => $"line {e.Extent.StartLineNumber}: {e.Message}")));
		var previous = ctx.Runtime.Tools.Get(name);
		ctx.Runtime.Tools.Register(tool, ctx.Project.Id, "powershell", JsonUtil.Serialize(tool.Definition), ctx.Agent.Id);
		var sb = new StringBuilder($"Registered {tool.Version}{(previous is null ? "" : $" (previous version {previous.Version} remains available to sessions that pinned it)")}.");
		ctx.Host.Cache.Load([name], ctx.Runtime.PolicyFor(ctx.Agent, ctx.Session).ToolTokenBudget);
		if(OptStr(args, "test_arguments") is { } test) {
			var r = await ctx.InvokeToolAsync(name, Schema.ParseArguments(test));
			sb.Append($"\nValidation run {(r.IsError ? "FAILED" : "succeeded")}:\n{TextUtil.Truncate(r.Text, 4000)}");
		}
		return ToolResult.Ok(sb.ToString(), description: $"register_tool {name}");
	}
}

public sealed class ScriptTool(string name, string description, JsonObject schema, string script, IReadOnlyList<string> tags, TimeSpan timeout) : ITool {
	public string Name => name;
	public string Description => description;
	public JsonObject InputSchema => schema;
	public string Script => script;
	public TimeSpan Timeout => timeout;
	public IReadOnlyList<string> Tags => tags;
	public string Version { get; } = $"{name}@{Hash.Sha256(description + "\n" + schema.ToJsonString() + "\n" + script)[..12]}";
	public object Definition => new { name, description, input_schema = schema, script, tags, timeout_seconds = (int) timeout.TotalSeconds };

	public Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		// Bind arguments by splatting a hashtable into the script block, inside the caller's runspace.
		var argJson = args.ToJsonString().Replace("'", "''");
		var wrapper = $"$__args = ConvertFrom-Json -AsHashtable -InputObject '{argJson}'; & {{\n{script}\n}} @__args";
		var result = ctx.Host.PowerShell.Run(wrapper, timeout, ctx.CancellationToken);
		return Task.FromResult(new ToolResult { Text = result.Text, Value = result.LastValue, IsError = result.HadErrors && result.LastValue is null, Description = $"{name} (script tool)" });
	}
}

public static class AgentTools {
	/// <summary>Reloads the latest active version of every agent-authored script tool after a restart.</summary>
	public static void LoadPersisted(AinurRuntime rt) {
		var rows = rt.Store.Db.Read(c => Dapper.SqlMapper.Query<(string Id, string Name, string? ProjectId, string Source)>(c,
			"SELECT id, name, project_id, source FROM tool_versions WHERE kind='powershell' AND state='active' ORDER BY created_at").ToList());
		foreach(var row in rows) {
			var def = JsonNode.Parse(row.Source)!;
			var tool = new ScriptTool(row.Name, def["description"]!.GetValue<string>(), (JsonObject) def["input_schema"]!.DeepClone(), def["script"]!.GetValue<string>(),
				(def["tags"] as JsonArray ?? []).Select(t => t!.GetValue<string>()).ToList(), TimeSpan.FromSeconds(def["timeout_seconds"]?.GetValue<int>() ?? 300));
			rt.Tools.Register(tool, row.ProjectId, "powershell", row.Source);
		}
	}
}
