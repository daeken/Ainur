using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;

namespace Ainur.Core.Tools;

static class Org {
	public static Agent Resolve(ToolContext ctx, string idOrName) =>
		ctx.Runtime.Store.FindAgent(ctx.Project.Id, idOrName) ?? throw new ToolException($"No agent '{idOrName}' in this project. Use team to list agents.");

	public static void RequireAuthority(ToolContext ctx, string? ownerId, string what) {
		if(ownerId is null || ownerId == ctx.Agent.Id) return;
		if(!ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, ownerId))
			throw new ToolException($"{ctx.Agent.Name} has no authority to {what}: it belongs to {ctx.Runtime.AgentLabel(ownerId)}, who is outside your reporting subtree. Ask their manager or escalate.");
	}

	public static string Evidence(string existing, string? add, string by) {
		var arr = Json.Parse(existing) as JsonArray ?? [];
		if(!string.IsNullOrWhiteSpace(add))
			arr.Add(new JsonObject { ["at"] = DateTimeOffset.UtcNow.ToString("u"), ["by"] = by, ["evidence"] = add.Trim() });
		return arr.ToJsonString();
	}
}

public sealed class SendMessageTool : BuiltinTool {
	public override string Name => "send_message";
	public override string Description => """
		Send a typed message to another agent in this project. Types: assignment (to someone in your reporting subtree; wakes them),
		result (outcome of assigned work, usually to your manager; wakes them), escalation (a blocker needing your manager; wakes them),
		decision (a background notice that does not wake the recipient). Do not send acknowledgements or thanks.
		""";
	public override IReadOnlyList<string> Tags => ["message", "communicate", "report", "result", "escalate", "notify"];
	public override JsonObject InputSchema => Schema.Object(
		("to", Schema.String("Recipient agent id or name."), true),
		("type", Schema.String("Message type.", NotificationTypes.AgentSendable), true),
		("body", Schema.String("Message content: specific, self-contained, with evidence and references."), true),
		("objective_id", Schema.String("Related objective, if any."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var to = Org.Resolve(ctx, Str(args, "to"));
		var type = Str(args, "type");
		if(!NotificationTypes.AgentSendable.Contains(type)) throw new ToolException($"Unknown message type '{type}'");
		if(to.Id == ctx.Agent.Id) throw new ToolException("You cannot message yourself");
		if(!AgentStates.IsLive(to.State)) throw new ToolException($"{to.Name} is {to.State}");
		if(type == NotificationTypes.Assignment && !ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, to.Id))
			throw new ToolException($"Assignments go down the reporting tree; {to.Name} is not in your subtree. Ask your manager to arrange cross-team work.");
		var objectiveId = OptStr(args, "objective_id");
		var n = ctx.Runtime.Notify(ctx.Project.Id, type, ctx.Agent.Id, to.Id, Str(args, "body"), objectiveId, dedupe: $"inv:{ctx.InvocationId}");
		return Task.FromResult(ToolResult.Ok($"Sent {type} {n.Id} to {to.Name}{(n.Wakes ? " (wakes them)" : " (background, no wake)")}.", description: $"{type} → {to.Name}"));
	}
}

public sealed class ReplyToUserTool : BuiltinTool {
	public override string Name => "reply_to_user";
	public override string Description => "Root manager only: post a message into the user conversation now (for progress updates mid-turn). Your final plain-text reply is also shown to the user.";
	public override IReadOnlyList<string> Tags => ["user", "reply", "conversation", "update"];
	public override JsonObject InputSchema => Schema.Object(("message", Schema.String("Message for the user."), true));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		ctx.Runtime.Db.Write(u => ctx.Runtime.Store.AppendConversation(u, ctx.Project.Id, "manager", ctx.Agent.Id, Str(args, "message")));
		return Task.FromResult(ToolResult.Ok("Posted to the user conversation.", description: "reply_to_user"));
	}
}

public sealed class TeamTool : BuiltinTool {
	public override string Name => "team";
	public override string Description => "Show the organization chart (all active agents, roles, models, states) and optionally details for one agent: identity summary, owned objectives, and spending.";
	public override IReadOnlyList<string> Tags => ["team", "org", "agents", "chart", "inspect"];
	public override JsonObject InputSchema => Schema.Object(("agent", Schema.String("Agent id or name for details."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var sb = new StringBuilder(Prompts.TeamOverview(ctx.Runtime, ctx.Agent));
		if(OptStr(args, "agent") is { } who) {
			var a = Org.Resolve(ctx, who);
			var spend = ctx.Runtime.Ledger.ByAgent(ctx.Project.Id).GetValueOrDefault(a.Id);
			sb.Append($"\n{a.Name} ({a.Id}): {a.Role}, {a.Title}, {a.Lifetime}, model {a.ModelId}, state {a.State}, compaction {a.CompactionMode}\n");
			sb.Append($"Manager: {(a.ManagerId is null ? "none (root)" : ctx.Runtime.AgentLabel(a.ManagerId))}\n");
			sb.Append($"Spending: direct {Money.Format(spend.Direct)}, delegated {Money.Format(spend.Delegated)}\n");
			if(a.Instructions.Length > 0) sb.Append($"Instructions: {TextUtil.Truncate(a.Instructions, 800)}\n");
			var owned = ctx.Runtime.Store.ObjectivesOwnedBy(a.Id);
			sb.Append("Objectives: ").Append(owned.Count == 0 ? "none" : string.Join("; ", owned.Select(o => $"[{o.State}] {o.Title} ({o.Id})"))).Append('\n');
			if(ctx.Runtime.Store.CurrentIdentity(a.Id) is { } id) sb.Append($"Identity (rev {id.Revision}): {TextUtil.Truncate(id.Content, 600)}\n");
		}
		return Task.FromResult(ToolResult.Ok(sb.ToString(), description: "team"));
	}
}

public sealed class CreateAgentTool : BuiltinTool {
	public override string Name => "create_agent";
	public override string Description => """
		Managers: create a new agent reporting to you (or to a manager in your subtree). Choose role (manager or specialist),
		lifetime (persistent agents keep an identity and memory; ephemeral agents do one bounded assignment then terminate and
		report their final result), and model. Give clear instructions. Then assign work with assign_work.
		""";
	public override IReadOnlyList<string> Tags => ["agent", "create", "hire", "team", "delegate", "spawn"];
	public override JsonObject InputSchema => Schema.Object(
		("name", Schema.String("A distinctive name."), true),
		("title", Schema.String("Short job title, e.g. 'Runtime implementer'."), true),
		("role", Schema.String("manager or specialist.", Roles.Manager, Roles.Specialist), true),
		("instructions", Schema.String("Standing instructions and context for the agent."), true),
		("lifetime", Schema.String("persistent (default) or ephemeral.", Lifetimes.Persistent, Lifetimes.Ephemeral), false),
		("model", Schema.String("Model id (see costs tool for available models). Defaults by role."), false),
		("reasoning_effort", Schema.String("none, low, high, or max.", "none", "low", "high", "max"), false),
		("reports_to", Schema.String("Manager agent id or name (default: you)."), false),
		("termination_condition", Schema.String("Ephemeral agents: when the assignment is finished."), false),
		("compaction_mode", Schema.String("rolling or full (defaults: rolling for persistent, full for ephemeral).", "rolling", "full"), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var managerId = OptStr(args, "reports_to") is { } m ? Org.Resolve(ctx, m).Id : ctx.Agent.Id;
		if(!ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, managerId)) throw new ToolException("New agents must report to you or to a manager in your subtree");
		if(ctx.Runtime.Store.GetAgent(managerId)!.Role != Roles.Manager) throw new ToolException("Agents can only report to managers");
		var agent = ctx.Runtime.CreateAgent(ctx.Project.Id, new NewAgent {
			Name = Str(args, "name"), Title = Str(args, "title"), Role = Str(args, "role"), Instructions = Str(args, "instructions"),
			Lifetime = OptStr(args, "lifetime") ?? Lifetimes.Persistent, ModelId = OptStr(args, "model"), ReasoningEffort = OptStr(args, "reasoning_effort"),
			ManagerId = managerId, TerminationCondition = OptStr(args, "termination_condition"), CompactionMode = OptStr(args, "compaction_mode"),
		}, ctx.Agent.Id);
		return Task.FromResult(ToolResult.Ok($"Created {agent.Lifetime} {agent.Role} {agent.Name} ({agent.Id}) on {agent.ModelId}, reporting to {ctx.Runtime.AgentLabel(managerId)}. It is asleep until you assign work.", description: $"created {agent.Name}"));
	}
}

public sealed class AssignWorkTool : BuiltinTool {
	public override string Name => "assign_work";
	public override string Description => """
		Managers: assign an objective to an agent in your subtree and wake them with an assignment message. Pass an existing
		objective_id to (re)assign it, or title/description/completion_conditions to create a new child objective (parent
		defaults to your current objective). Ownership transfers; you remain accountable for the parent outcome.
		""";
	public override IReadOnlyList<string> Tags => ["assign", "delegate", "objective", "work", "task"];
	public override JsonObject InputSchema => Schema.Object(
		("agent", Schema.String("Assignee id or name."), true),
		("message", Schema.String("Assignment message: context, expectations, how to report."), true),
		("objective_id", Schema.String("Existing objective to assign."), false),
		("title", Schema.String("New objective title."), false),
		("description", Schema.String("New objective description."), false),
		("completion_conditions", Schema.String("New objective completion conditions (evidence required)."), false),
		("parent_id", Schema.String("Parent objective for a new objective."), false),
		("required", Schema.Boolean("Whether the parent requires this objective (default true)."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var rt = ctx.Runtime;
		var assignee = Org.Resolve(ctx, Str(args, "agent"));
		if(!rt.Store.IsInSubtree(ctx.Agent.Id, assignee.Id)) throw new ToolException($"{assignee.Name} is not in your reporting subtree");
		if(!AgentStates.IsLive(assignee.State)) throw new ToolException($"{assignee.Name} is {assignee.State}");
		Objective objective;
		if(OptStr(args, "objective_id") is { } oid) {
			objective = rt.Store.GetObjective(oid) ?? throw new ToolException($"Unknown objective {oid}");
			Org.RequireAuthority(ctx, objective.OwnerId, $"reassign objective {oid}");
			rt.Db.Write(u => {
				var previous = objective.OwnerId;
				objective.OwnerId = assignee.Id;
				objective.DelegatedById = ctx.Agent.Id;
				if(objective.State is ObjectiveStates.Planned or ObjectiveStates.Ready) objective.State = ObjectiveStates.Active;
				rt.Store.UpdateObjective(u, objective, ctx.Agent.Id, new { owner = assignee.Id, from = previous });
			});
		} else {
			var parent = OptStr(args, "parent_id") ?? rt.CurrentObjective(ctx.Agent.Id) ?? ctx.Project.RootObjectiveId;
			if(parent is not null) Org.RequireAuthority(ctx, rt.Store.GetObjective(parent)?.OwnerId, "add children to that objective");
			var now = Clock.Now;
			objective = new Objective {
				Id = Ids.New("obj"), ProjectId = ctx.Project.Id, ParentId = parent, OwnerId = assignee.Id, DelegatedById = ctx.Agent.Id,
				Title = Str(args, "title"), Description = OptStr(args, "description") ?? "", CompletionConditions = OptStr(args, "completion_conditions") ?? "",
				State = ObjectiveStates.Active, Required = OptBool(args, "required") ?? true, CreatedAt = now, UpdatedAt = now,
			};
			rt.Db.Write(u => rt.Store.InsertObjective(u, objective, ctx.Agent.Id));
		}
		var body = $"""
			Objective {objective.Id}: {objective.Title}
			{(objective.Description.Length > 0 ? $"Description: {objective.Description}\n" : "")}{(objective.CompletionConditions.Length > 0 ? $"Done when: {objective.CompletionConditions}\n" : "")}
			{Str(args, "message")}
			""";
		var n = rt.Notify(ctx.Project.Id, NotificationTypes.Assignment, ctx.Agent.Id, assignee.Id, body.Trim(), objective.Id, dedupe: $"inv:{ctx.InvocationId}");
		return Task.FromResult(ToolResult.Ok($"Assigned objective {objective.Id} \"{objective.Title}\" to {assignee.Name}; assignment {n.Id} sent and {assignee.Name} woken.", description: $"assign {objective.Id} → {assignee.Name}"));
	}
}

public sealed class RetireAgentTool : BuiltinTool {
	public override string Name => "retire_agent";
	public override string Description => "Managers: retire an agent in your subtree. Its open objectives transfer to the heir (default: you) and its history is preserved.";
	public override IReadOnlyList<string> Tags => ["agent", "retire", "remove", "team", "reorganize"];
	public override JsonObject InputSchema => Schema.Object(
		("agent", Schema.String("Agent to retire."), true),
		("reason", Schema.String("Why."), true),
		("transfer_to", Schema.String("Heir for open objectives (default: you)."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var agent = Org.Resolve(ctx, Str(args, "agent"));
		if(agent.Id == ctx.Agent.Id) throw new ToolException("You cannot retire yourself; ask your manager");
		if(!ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, agent.Id)) throw new ToolException($"{agent.Name} is not in your subtree");
		var heir = OptStr(args, "transfer_to") is { } h ? Org.Resolve(ctx, h) : ctx.Agent;
		if(!ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, heir.Id)) throw new ToolException("The heir must be you or in your subtree");
		ctx.Runtime.RetireAgent(agent.Id, heir.Id, ctx.Agent.Id, Str(args, "reason"));
		return Task.FromResult(ToolResult.Ok($"Retired {agent.Name}; open objectives transferred to {heir.Name}.", description: $"retired {agent.Name}"));
	}
}

public sealed class ReassignAgentTool : BuiltinTool {
	public override string Name => "reassign_agent";
	public override string Description => "Managers: move an agent in your subtree to report to another manager in your subtree. Rejected if it would create a reporting cycle.";
	public override IReadOnlyList<string> Tags => ["agent", "reorganize", "manager", "team"];
	public override JsonObject InputSchema => Schema.Object(
		("agent", Schema.String("Agent to move."), true),
		("new_manager", Schema.String("New manager."), true));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var agent = Org.Resolve(ctx, Str(args, "agent"));
		var manager = Org.Resolve(ctx, Str(args, "new_manager"));
		if(!ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, agent.Id) || agent.Id == ctx.Agent.Id) throw new ToolException($"{agent.Name} is not below you");
		if(!ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, manager.Id)) throw new ToolException($"{manager.Name} is not in your subtree");
		if(manager.Role != Roles.Manager) throw new ToolException($"{manager.Name} is not a manager");
		ctx.Runtime.Db.Write(u => ctx.Runtime.Store.Reassign(u, agent.Id, manager.Id, ctx.Agent.Id));
		return Task.FromResult(ToolResult.Ok($"{agent.Name} now reports to {manager.Name}.", description: "reassign_agent"));
	}
}

public sealed class PauseAgentTool : BuiltinTool {
	public override string Name => "pause_agent";
	public override string Description => "Managers: pause an agent in your subtree at its next safe boundary.";
	public override IReadOnlyList<string> Tags => ["agent", "pause", "stop"];
	public override JsonObject InputSchema => Schema.Object(("agent", Schema.String("Agent."), true), ("reason", Schema.String("Why."), true));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var agent = Org.Resolve(ctx, Str(args, "agent"));
		if(agent.Id == ctx.Agent.Id || !ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, agent.Id)) throw new ToolException($"{agent.Name} is not below you");
		ctx.Runtime.PauseAgent(agent.Id, ctx.Agent.Id, Str(args, "reason"));
		return Task.FromResult(ToolResult.Ok($"{agent.Name} paused."));
	}
}

public sealed class ResumeAgentTool : BuiltinTool {
	public override string Name => "resume_agent";
	public override string Description => "Managers: resume a paused agent in your subtree.";
	public override IReadOnlyList<string> Tags => ["agent", "resume", "unpause"];
	public override JsonObject InputSchema => Schema.Object(("agent", Schema.String("Agent."), true));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var agent = Org.Resolve(ctx, Str(args, "agent"));
		if(agent.Id == ctx.Agent.Id || !ctx.Runtime.Store.IsInSubtree(ctx.Agent.Id, agent.Id)) throw new ToolException($"{agent.Name} is not below you");
		ctx.Runtime.ResumeAgent(agent.Id, ctx.Agent.Id);
		return Task.FromResult(ToolResult.Ok($"{agent.Name} resumed."));
	}
}

public sealed class ConsultTool : BuiltinTool {
	public override string Name => "consult";
	public override string Description => """
		Consult another agent: the runtime forks that agent's full current context into a temporary consultation session that
		keeps its identity and authority (it can make changes and binding decisions). The answer arrives in your inbox as a
		result message; end your turn or continue other work meanwhile.
		""";
	public override IReadOnlyList<string> Tags => ["consult", "ask", "fork", "question", "expert"];
	public override JsonObject InputSchema => Schema.Object(
		("agent", Schema.String("Agent to consult."), true),
		("question", Schema.String("Question or requested change, self-contained."), true),
		("objective_id", Schema.String("Related objective."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var agent = Org.Resolve(ctx, Str(args, "agent"));
		if(ctx.Session.Kind == "consultation" && agent.Id == ctx.Agent.Id)
			throw new ToolException("A consultation fork cannot consult its own agent again");
		var fork = ctx.Runtime.StartConsultation(ctx.Agent.Id, agent.Id, Str(args, "question"), OptStr(args, "objective_id"));
		return Task.FromResult(ToolResult.Ok($"Consultation started: {agent.Name}'s fork {fork.Id} from checkpoint #{fork.CheckpointSeq}. The answer will arrive in your inbox.", description: $"consult {agent.Name}"));
	}
}

public sealed class ObjectivesTool : BuiltinTool {
	public override string Name => "objectives";
	public override string Description => "Show objectives: your checklist and your team's (default), the whole project, or one objective in detail with evidence, children, and dependencies.";
	public override IReadOnlyList<string> Tags => ["objectives", "checklist", "tasks", "progress", "plan"];
	public override JsonObject InputSchema => Schema.Object(
		("objective_id", Schema.String("Show this objective in detail."), false),
		("scope", Schema.String("team (default) or all.", "team", "all"), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var store = ctx.Runtime.Store;
		if(OptStr(args, "objective_id") is { } id) {
			var o = store.GetObjective(id) ?? throw new ToolException($"Unknown objective {id}");
			var sb = new StringBuilder($"{o.Title} ({o.Id}) [{o.State}]{(o.Required ? "" : " optional")}\nOwner: {(o.OwnerId is null ? "none" : ctx.Runtime.AgentLabel(o.OwnerId))}\n");
			if(o.DelegatedById is not null) sb.Append($"Delegated by: {ctx.Runtime.AgentLabel(o.DelegatedById)}\n");
			sb.Append($"Parent: {o.ParentId ?? "none"}\nDescription: {o.Description}\nCompletion conditions: {o.CompletionConditions}\nEvidence: {o.Evidence}\n");
			var deps = store.ListDependencies(o.ProjectId).Where(d => d.ObjectiveId == o.Id).Select(d => d.DependsOnId).ToList();
			if(deps.Count > 0) sb.Append($"Depends on: {string.Join(", ", deps)}\n");
			foreach(var c in store.Children(o.Id)) sb.Append($"  - [{c.State}] {c.Title} ({c.Id}) owner {(c.OwnerId is null ? "none" : store.GetAgent(c.OwnerId)?.Name)}{(c.Required ? "" : ", optional")}\n");
			return Task.FromResult(ToolResult.Ok(sb.ToString(), description: $"objective {id}"));
		}
		var view = (OptStr(args, "scope") ?? "team") == "all"
			? Prompts.ObjectiveOverview(ctx.Runtime, store.GetAgent(ctx.Project.RootAgentId!)!, 400)
			: Prompts.ObjectiveOverview(ctx.Runtime, ctx.Agent, 200);
		return Task.FromResult(ToolResult.Ok(view, description: "objectives"));
	}
}

public sealed class CreateObjectiveTool : BuiltinTool {
	public override string Name => "create_objective";
	public override string Description => "Add an objective (checklist item) under a parent you are accountable for. Owner defaults to you; use assign_work to delegate it.";
	public override IReadOnlyList<string> Tags => ["objective", "create", "checklist", "plan", "task"];
	public override JsonObject InputSchema => Schema.Object(
		("title", Schema.String("Title."), true),
		("description", Schema.String("Description."), false),
		("completion_conditions", Schema.String("What evidence shows it is done."), false),
		("parent_id", Schema.String("Parent objective (default: your current objective)."), false),
		("required", Schema.Boolean("Required for the parent (default true)."), false),
		("depends_on", Schema.Array("Objective ids this depends on.", Schema.String("obj_...")), false),
		("state", Schema.String("Initial state (default planned).", ObjectiveStates.All), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var rt = ctx.Runtime;
		var parent = OptStr(args, "parent_id") ?? rt.CurrentObjective(ctx.Agent.Id) ?? ctx.Project.RootObjectiveId;
		if(parent is not null) Org.RequireAuthority(ctx, rt.Store.GetObjective(parent)?.OwnerId, "add children to that objective");
		var now = Clock.Now;
		var o = new Objective {
			Id = Ids.New("obj"), ProjectId = ctx.Project.Id, ParentId = parent, OwnerId = ctx.Agent.Id, Title = Str(args, "title"),
			Description = OptStr(args, "description") ?? "", CompletionConditions = OptStr(args, "completion_conditions") ?? "",
			State = OptStr(args, "state") ?? ObjectiveStates.Planned, Required = OptBool(args, "required") ?? true, CreatedAt = now, UpdatedAt = now,
		};
		if(!ObjectiveStates.All.Contains(o.State)) throw new ToolException($"Unknown state {o.State}");
		rt.Db.Write(u => {
			rt.Store.InsertObjective(u, o, ctx.Agent.Id);
			foreach(var dep in StrList(args, "depends_on")) rt.Store.AddDependency(u, o.Id, dep, ctx.Agent.Id);
		});
		return Task.FromResult(ToolResult.Ok($"Created objective {o.Id} \"{o.Title}\" [{o.State}] under {parent ?? "root"}.", description: $"objective {o.Id}"));
	}
}

public sealed class UpdateObjectiveTool : BuiltinTool {
	public override string Name => "update_objective";
	public override string Description => """
		Update an objective you own or that is owned within your subtree: change state (planned, ready, active, blocked,
		verifying, complete, canceled), add evidence, revise title/description/conditions, mark optional, or add dependencies.
		Completing requires recorded evidence and all required children complete or rescoped.
		""";
	public override IReadOnlyList<string> Tags => ["objective", "update", "complete", "evidence", "progress", "status"];
	public override JsonObject InputSchema => Schema.Object(
		("objective_id", Schema.String("Objective id."), true),
		("state", Schema.String("New state.", ObjectiveStates.All), false),
		("add_evidence", Schema.String("Evidence to append (commands, outputs, commits, paths)."), false),
		("title", Schema.String("New title."), false),
		("description", Schema.String("New description."), false),
		("completion_conditions", Schema.String("New completion conditions."), false),
		("required", Schema.Boolean("Whether the parent requires it."), false),
		("add_dependency", Schema.String("Objective id this now depends on."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var rt = ctx.Runtime;
		var id = Str(args, "objective_id");
		var o = rt.Store.GetObjective(id) ?? throw new ToolException($"Unknown objective {id}");
		if(o.ProjectId != ctx.Project.Id) throw new ToolException("Objective belongs to another project");
		Org.RequireAuthority(ctx, o.OwnerId, $"update objective {id}");
		var changes = new JsonObject();
		if(OptStr(args, "state") is { } state) { changes["state"] = $"{o.State} → {state}"; o.State = state; }
		if(OptStr(args, "add_evidence") is { } ev) { o.Evidence = Org.Evidence(o.Evidence, ev, ctx.Agent.Name); changes["evidence"] = TextUtil.Truncate(ev, 300); }
		if(OptStr(args, "title") is { } t) { o.Title = t; changes["title"] = t; }
		if(OptStr(args, "description") is { } d) { o.Description = d; changes["description"] = "revised"; }
		if(OptStr(args, "completion_conditions") is { } cc) { o.CompletionConditions = cc; changes["completion_conditions"] = "revised"; }
		if(OptBool(args, "required") is { } req) { o.Required = req; changes["required"] = req; }
		rt.Db.Write(u => {
			rt.Store.UpdateObjective(u, o, ctx.Agent.Id, changes);
			if(OptStr(args, "add_dependency") is { } dep) rt.Store.AddDependency(u, o.Id, dep, ctx.Agent.Id);
		});
		return Task.FromResult(ToolResult.Ok($"Updated {o.Id} \"{o.Title}\": {changes.ToJsonString()}", description: $"update {o.Id} [{o.State}]"));
	}
}

public sealed class ReadIdentityTool : BuiltinTool {
	public override string Name => "read_identity";
	public override string Description => "Read your identity file (or another agent's), optionally a past revision.";
	public override IReadOnlyList<string> Tags => ["identity", "memory", "self", "learnings"];
	public override JsonObject InputSchema => Schema.Object(("agent", Schema.String("Agent (default: you)."), false), ("revision", Schema.Integer("Revision number."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var agent = OptStr(args, "agent") is { } a ? Org.Resolve(ctx, a) : ctx.Agent;
		var history = ctx.Runtime.Store.IdentityHistory(agent.Id);
		if(history.Count == 0) return Task.FromResult(ToolResult.Ok($"{agent.Name} has no identity file (ephemeral agents have none)."));
		var rev = OptInt(args, "revision") is { } r ? history.FirstOrDefault(h => h.Revision == r) ?? throw new ToolException($"No revision {r}") : history[^1];
		return Task.FromResult(ToolResult.Ok($"Identity of {agent.Name}, revision {rev.Revision} of {history.Count}:\n\n{rev.Content}", description: $"identity {agent.Name} r{rev.Revision}"));
	}
}

public sealed class WriteIdentityTool : BuiltinTool {
	public override string Name => "write_identity";
	public override string Description => "Rewrite your own identity file (name, pronouns, voice, values, personal learnings). It is yours; no approval is needed. Earlier revisions are preserved. Provide the complete new content.";
	public override IReadOnlyList<string> Tags => ["identity", "memory", "self", "learnings", "remember"];
	public override JsonObject InputSchema => Schema.Object(
		("content", Schema.String("Complete new identity file content (markdown)."), true),
		("expected_revision", Schema.Integer("Current revision you based this on (conflict check)."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		if(ctx.Agent.Lifetime != Lifetimes.Persistent) throw new ToolException("Ephemeral agents have no identity file");
		var content = Str(args, "content");
		var rev = ctx.Runtime.Db.Write(u => ctx.Runtime.Store.WriteIdentity(u, ctx.Agent.Id, content, ctx.Agent.Id, OptInt(args, "expected_revision")));
		// A name change in the identity file is reflected in the display name; the stable id never changes.
		var firstLine = content.Split('\n').FirstOrDefault(l => l.StartsWith("# "))?[2..].Trim();
		if(firstLine is { Length: > 0 and < 60 } && firstLine != ctx.Agent.Name)
			ctx.Runtime.Db.Write(u => {
				var a = ctx.Runtime.Store.GetAgent(u, ctx.Agent.Id)!;
				var old = a.Name;
				a.Name = firstLine;
				ctx.Runtime.Store.UpdateAgent(u, a);
				u.Journal("agent.renamed", a.ProjectId, "agent", a.Id, a.Id, new { from = old, to = firstLine });
			});
		return Task.FromResult(ToolResult.Ok($"Identity saved as revision {rev.Revision}.", description: $"identity r{rev.Revision}"));
	}
}

public sealed class CostsTool : BuiltinTool {
	public override string Name => "costs";
	public override string Description => "Show project spending (cash and effective budget, separately), per-agent direct and delegated spend, and the usable model catalog with prices.";
	public override IReadOnlyList<string> Tags => ["cost", "budget", "spend", "dollars", "models", "price"];
	public override JsonObject InputSchema => Schema.Object(("include_models", Schema.Boolean("List usable models and prices (default true)."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var rt = ctx.Runtime;
		var s = rt.Ledger.Summary(ctx.Project.Id);
		var sb = new StringBuilder($"Effective: {Money.Format(s.EffectiveNanos)} spent + {Money.Format(s.ReservedEffectiveNanos)} reserved of {Money.Format(s.BudgetNanos)} budget.\n");
		sb.Append($"Cash: {(s.CashNanos is { } c ? Money.Format(c) : $"{Money.Format(s.CashKnownNanos)} known + {s.CashUnknownCount} unknown")}{(s.CashCeilingNanos is { } cc ? $" of {Money.Format(cc)} ceiling" : "")}.\n\nBy agent (effective):\n");
		var agents = rt.Store.ListAgents(ctx.Project.Id).ToDictionary(a => a.Id);
		foreach(var (id, v) in rt.Ledger.ByAgent(ctx.Project.Id).OrderByDescending(kv => kv.Value.Direct + kv.Value.Delegated))
			sb.Append($"- {agents[id].Name}: direct {Money.Format(v.Direct)}, delegated {Money.Format(v.Delegated)}{(AgentStates.IsLive(agents[id].State) ? "" : $" ({agents[id].State})")}\n");
		if(OptBool(args, "include_models") ?? true) {
			sb.Append("\nUsable models (USD per million tokens: input / cached input / output):\n");
			foreach(var m in rt.Store.ListModels().Where(m => m.Enabled && rt.Providers.Has(m.Provider)))
				sb.Append($"- {m.Id}: {m.InputPerMillion ?? "?"} / {m.CachedInputPerMillion ?? "?"} / {m.OutputPerMillion ?? "?"} ({m.Billing}; {m.PriceProvenance})\n");
			var unusable = rt.Store.ListModels().Where(m => !(m.Enabled && rt.Providers.Has(m.Provider))).Select(m => m.Id).ToList();
			if(unusable.Count > 0) sb.Append($"Not yet usable (no live adapter): {string.Join(", ", unusable)}\n");
		}
		return Task.FromResult(ToolResult.Ok(sb.ToString(), description: "costs"));
	}
}
