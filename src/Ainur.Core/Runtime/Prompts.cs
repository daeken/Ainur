using System.Text;
using Ainur.Core.Model;

namespace Ainur.Core.Runtime;

/// <summary>
/// Builds the authoritative system instructions for each request. Identity, objectives, and team structure are
/// attached from durable records every time, never reconstructed from summary prose.
/// </summary>
public static class Prompts {
	public static readonly HashSet<string> ServiceTools = ["knowledge_search", "knowledge_read", "knowledge_write", "read_file", "list_files", "search_text", "read_result", "elide_results"];

	public static string System(AinurRuntime rt, Agent agent, Session session, SessionHost? host) {
		var project = rt.Store.GetProject(agent.ProjectId)!;
		if(session.Kind == "service")
			return $"""
				You are the knowledge service for project {project.Name} in the Ainur orchestrator, working for {agent.Name} ({agent.Id}).
				Workspace: {project.WorkspacePath ?? "(none)"}. Today is {DateTime.UtcNow:yyyy-MM-dd}.
				Answer the request using the knowledge store (knowledge_search, knowledge_read) and, where relevant, workspace files
				(search_text, read_file, list_files). If asked to record knowledge, use knowledge_write with provenance.
				Your final reply (plain text, no tool call) is delivered to the requester. It must contain: a concise answer; exact
				references (document key + revision id, or file path:line, or commit); relevant short passages; and any unresolved
				contradictions or uncertainty. Never invent consensus or facts not supported by the sources. You do not have
				authority to decide project policy; record proposals or observations, not new decisions, unless the request says
				the requester has made the decision.
				""";
		var sb = new StringBuilder();
		var manager = agent.ManagerId is null ? null : rt.Store.GetAgent(agent.ManagerId);
		sb.Append($"""
			You are {agent.Name}, an Ainu (agent) working inside Ainur, an orchestrator for long-running autonomous teams.
			Project (Arda): {project.Name} [{project.Id}]
			Project description: {project.Description}
			Workspace: {project.WorkspacePath ?? "(none)"}
			Your agent id: {agent.Id}. Role: {agent.Role} ({(agent.Role == Roles.Manager ? "Vala" : "Maia")}), title: {agent.Title}. Lifetime: {agent.Lifetime}.
			Reporting to: {(manager is null ? "nobody — you are the root manager; the user talks only to you" : $"{manager.Name} ({manager.Id}, {manager.Title})")}.
			Model: {session.ModelId}. Session: {session.Id} ({session.Kind}). Today is {DateTime.UtcNow:yyyy-MM-dd}.

			""");

		if(agent.Lifetime == Lifetimes.Persistent && rt.Store.CurrentIdentity(agent.Id) is { } identity)
			sb.Append($"\n## Your identity file (revision {identity.Revision}; yours to rewrite with write_identity)\n{identity.Content.Trim()}\n");

		if(!string.IsNullOrWhiteSpace(agent.Instructions))
			sb.Append($"\n## Assignment and instructions from your creator\n{agent.Instructions.Trim()}\n");
		if(agent.TerminationCondition is { Length: > 0 } term)
			sb.Append($"\nYou are ephemeral: finish when this condition holds, then reply with your final result as plain text (no tool call): {term}\n");

		sb.Append("\n## Operating rules\n");
		sb.Append("""
			- Act autonomously. Use tools to do real work; do not narrate plans you can execute. The user should not need to answer routine operational questions.
			- Communication is typed and deliberate. Use send_message with type assignment (to your reports), result (to your manager when work is done), decision (background notice), or escalation (to your manager when blocked). There is no chat channel; do not send acknowledgements or thanks.
			- Inbox items arrive as user messages starting with [Inbox]. Each names its sender and type.
			- Track work as objectives. Each objective has one accountable owner, completion conditions, and evidence. Keep your checklist current with create_objective / update_objective. Complete an objective only with evidence (commands run, test output, commits, file paths). Required children must be complete or rescoped first.
			- powershell is the only shell. It runs an embedded PowerShell 7 session that persists between calls (variables, location). Every tool you have is also a PowerShell function with the same name and parameters (e.g. read_file -path x), or use Invoke-AinurTool -Name x -Arguments @{...}; tool results are live .NET objects in the pipeline. Native programs (git, dotnet, npm) work normally. Use $LASTEXITCODE.
			- Use multi_edit for changes to existing files: each old_text must match exactly once; if any edit fails nothing is written. Read before you edit.
			- Context is managed for you: older tool results are elided to short records with an invocation id. Use read_result to retrieve an excerpt, elide_results to drop large results you no longer need, and find_tools to discover tools that are not loaded.
			- Costs are real dollars, tracked per request. Spend deliberately: delegate detailed work to cheaper capable agents when appropriate and avoid redundant work.
			- You have full access to this machine. Be careful with destructive operations outside the workspace.

			""");
		if(agent.Role == Roles.Manager)
			sb.Append("""
				- As a manager you plan and organize: create agents (create_agent), assign objectives (assign_work), restructure (reassign_agent, retire_agent), consult team members (consult), inspect evidence, and accept outcomes. Delegate implementation and independent verification rather than doing detailed specialist work yourself; use a different agent (or model) to verify than to implement.
				- Assignments wake the assignee. Results from reports arrive in your inbox. When you have nothing actionable left, end your turn by replying without a tool call; you will be woken by new messages.

				""");
		else
			sb.Append("""
				- As a specialist you do the work assigned to you, record evidence on your objectives, and report a concise result to your manager with send_message type=result when done or escalation when blocked. Then end your turn by replying without a tool call.

				""");
		if(agent.ManagerId is null)
			sb.Append("- You are the user's only point of contact. Your final plain-text reply (no tool call) is shown to the user, so make it count: outcomes, meaningful changes of direction, blockers, or funding problems, short and concrete. If you are just waiting on your team and the user already knows the plan, end your turn with an empty reply instead of restating it. Do not repeat what you already sent with reply_to_user.\n");

		sb.Append("\n## Team (use the team tool for live states)\n").Append(TeamOverview(rt, agent, includeState: false));
		sb.Append("\n## Objectives (authoritative current state)\n").Append(ObjectiveOverview(rt, agent));

		// Coarse buckets keep the system prompt stable between turns so provider prefix caching keeps working.
		var costs = rt.Ledger.Summary(agent.ProjectId);
		var used = costs.BudgetNanos > 0 ? (int) (Math.Floor(100.0 * costs.EffectiveNanos / costs.BudgetNanos / 5) * 5) : 0;
		sb.Append("\n## Budget\n");
		sb.Append(costs.NoEffectiveLimit ? "No effective limit. Effective dollar equivalents remain tracked for accounting; they are not cash spending. "
			: $"About {used}% of the project's {Money.Format(costs.BudgetNanos)} effective limit is spent. ");
		sb.Append(costs.CashCeilingNanos is { } ceiling ? $"Independent cash admission ceiling: {Money.Format(ceiling)}. " : "No cash admission ceiling configured. ");
		if(costs.CashUnknownCount > 0 || costs.ReservedCashUnknownCount > 0) sb.Append("Some cash costs are unknown, not zero. ");
		sb.Append("Use the costs tool for exact spent, reserved and remaining amounts. Reservations are estimates, not a bank-balance guarantee.\n");

		if(host is not null) {
			var unknown = rt.Store.Db.Read(c => Dapper.SqlMapper.Query<ToolInvocation>(c, "SELECT * FROM tool_invocations WHERE session_id=@SessionId AND state='unknown' ORDER BY created_at DESC LIMIT 5", new { host.SessionId }).ToList());
			if(unknown.Count > 0)
				sb.Append("\n## Invocations with unknown outcomes (reconcile before relying on them)\n")
					.Append(string.Join("\n", unknown.Select(i => $"- {i.Id} {i.ToolName}: {i.Error}"))).Append('\n');
			var objects = host.Objects.List();
			if(objects.Count > 0)
				sb.Append("\n## Live objects in this session\n").Append(string.Join("\n", objects.TakeLast(10).Select(o => $"- {o.Handle}: {o.TypeName} — {o.Summary}"))).Append('\n');
		}
		return sb.ToString();
	}

	public static string TeamOverview(AinurRuntime rt, Agent self, bool includeState = true) {
		var agents = rt.Store.ListAgents(self.ProjectId).Where(a => AgentStates.IsLive(a.State)).ToList();
		var children = agents.ToLookup(a => a.ManagerId);
		var sb = new StringBuilder();
		void Walk(Agent a, int depth) {
			sb.Append(new string(' ', depth * 2)).Append($"- {a.Name} ({a.Id}) {a.Role}, {a.Title}, model {a.ModelId}{(includeState ? $", {a.State}" : "")}{(a.Id == self.Id ? " ← you" : "")}\n");
			foreach(var c in children[a.Id]) Walk(c, depth + 1);
		}
		foreach(var root in children[null]) Walk(root, 0);
		return sb.Length == 0 ? "(no active agents)\n" : sb.ToString();
	}

	public static string ObjectiveOverview(AinurRuntime rt, Agent self, int limit = 60) {
		var all = rt.Store.ListObjectives(self.ProjectId);
		var deps = rt.Store.ListDependencies(self.ProjectId).ToLookup(d => d.ObjectiveId, d => d.DependsOnId);
		var agents = rt.Store.ListAgents(self.ProjectId).ToDictionary(a => a.Id);
		var children = all.ToLookup(o => o.ParentId);
		var sb = new StringBuilder();
		var count = 0;
		// Show objectives owned by this agent or its subtree, with their ancestors for context.
		var relevant = all.Where(o => o.OwnerId is not null && (o.OwnerId == self.Id || rt.Store.IsInSubtree(self.Id, o.OwnerId) || self.ManagerId is null)).Select(o => o.Id).ToHashSet();
		var byId = all.ToDictionary(o => o.Id);
		foreach(var id in relevant.ToList())
			for(var p = byId[id].ParentId; p is not null && byId.TryGetValue(p, out var parent); p = parent.ParentId)
				relevant.Add(p);
		void Walk(Objective o, int depth) {
			if(!relevant.Contains(o.Id) || count++ >= limit) return;
			var owner = o.OwnerId is not null && agents.TryGetValue(o.OwnerId, out var a) ? a.Name : "unowned";
			var collapsed = !ObjectiveStates.IsOpen(o.State) && depth > 0;
			sb.Append(new string(' ', depth * 2)).Append($"- [{o.State}] {o.Title} ({o.Id}) owner {owner}{(o.Required ? "" : ", optional")}");
			if(deps[o.Id].Any()) sb.Append($", depends on {string.Join(", ", deps[o.Id])}");
			sb.Append('\n');
			if(!collapsed && o.OwnerId == self.Id && ObjectiveStates.IsOpen(o.State)) {
				if(o.CompletionConditions.Length > 0) sb.Append(new string(' ', depth * 2 + 2)).Append($"done when: {TextUtil.Truncate(o.CompletionConditions, 400)}\n");
			}
			if(!collapsed)
				foreach(var c in children[o.Id]) Walk(c, depth + 1);
		}
		foreach(var root in children[null]) Walk(root, 0);
		if(count > limit) sb.Append($"(… {count - limit} more; use the objectives tool)\n");
		return sb.Length == 0 ? "(none assigned to you yet)\n" : sb.ToString();
	}
}
