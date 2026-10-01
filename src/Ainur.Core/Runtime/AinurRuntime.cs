using System.Collections.Concurrent;
using System.Text;
using Ainur.Core.Accounting;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Tools;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed class RuntimeOptions {
	public string Home { get; set; } = DefaultHome();
	public string ManagerModelId { get; set; } = "deepseek-v4-pro";
	public string SpecialistModelId { get; set; } = "deepseek-v4-pro";
	public string CheapModelId { get; set; } = "deepseek-v4-flash";
	public decimal DefaultBudgetDollars { get; set; } = 5m;
	public int MaxStepsPerWake { get; set; } = 80;
	public int MaxOutputTokens { get; set; } = 32_000;
	public ContextPolicy PersistentPolicy { get; set; } = new();
	public ContextPolicy EphemeralPolicy { get; set; } = new() { ElideAfterTurns = 4 };
	/// <summary>Optional per-session override, e.g. to give a test session a tiny window.</summary>
	public Func<Agent, Session, ContextPolicy?>? PolicyOverride { get; set; }
	public bool AutoStartHosts { get; set; } = true;

	public static string DefaultHome() => Environment.GetEnvironmentVariable("AINUR_HOME") is { Length: > 0 } h ? h
		: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ainur");
}

/// <summary>
/// The scheduler and organization runtime: owns session hosts, recovery, communication, and the operations
/// that tools and the API perform on projects, agents, objectives, and sessions.
/// </summary>
public sealed partial class AinurRuntime : IDisposable {
	public readonly RuntimeOptions Options;
	public readonly Db Db;
	public readonly Store Store;
	public readonly Ledger Ledger;
	public readonly ArtifactStore Artifacts;
	public readonly ProviderRegistry Providers;
	public readonly ModelGateway Gateway;
	public readonly ToolRegistry Tools;
	public readonly Compactor Compactor;
	public readonly Watchdog Watchdog;
	public long Generation { get; private set; }
	public volatile bool Draining;
	readonly ConcurrentDictionary<string, SessionHost> Hosts = new();
	readonly ConcurrentDictionary<string, int> Failures = new();
	public event Action<string, StreamDelta>? Delta;

	public AinurRuntime(RuntimeOptions options, ProviderRegistry? providers = null) {
		Options = options;
		Directory.CreateDirectory(options.Home);
		Db = new Db(Path.Combine(options.Home, "ainur.db"));
		Store = new Store(Db);
		Ledger = new Ledger(Store);
		Artifacts = new ArtifactStore(Path.Combine(options.Home, "artifacts"));
		Providers = providers ?? DefaultProviders();
		Gateway = new ModelGateway(Store, Ledger, Artifacts, Providers);
		Tools = new ToolRegistry(Store);
		Compactor = new Compactor(Store, Gateway);
		Watchdog = new Watchdog(Path.Combine(options.Home, "runtime", "inflight.json"));
		ModelCatalog.EnsureSeeded(Store);
		BuiltinTools.RegisterAll(this);
		AgentTools.LoadPersisted(this);
	}

	public static ProviderRegistry DefaultProviders() {
		var registry = new ProviderRegistry();
		registry.Register(DeepSeekProvider.CreateDefault());
		return registry;
	}

	// ---- Lifecycle and recovery ----

	public void Start(string? release = null) {
		Generation = Db.Write(u => {
			var gen = (u.Scalar<long?>("SELECT MAX(generation) FROM runtime_generations") ?? 0) + 1;
			u.Execute("UPDATE runtime_generations SET stopped_at=COALESCE(stopped_at,@now), stop_reason=COALESCE(stop_reason,'unclean') WHERE stopped_at IS NULL", new { now = Clock.Now });
			u.Execute("INSERT INTO runtime_generations(generation,release,started_at) VALUES(@gen,@release,@now)", new { gen, release, now = Clock.Now });
			u.Journal("runtime.started", null, payload: new { generation = gen, release });
			return gen;
		});
		Recover();
		if(Options.AutoStartHosts)
			foreach(var agent in Store.ListLiveAgents())
				if(agent.PrimarySessionId is not null && agent.State != AgentStates.Paused)
					Wake(agent.Id);
		foreach(var session in Store.ActiveSessions().Where(s => s.Kind != "primary"))
			GetHost(session.Id)?.Wake();
	}

	/// <summary>
	/// Reconciles state left by an unclean stop: interrupted invocations become unknown (never blindly retried),
	/// dispatched model requests keep conservative charges, and affected sessions receive an explicit notice.
	/// </summary>
	public void Recover() {
		var restartReason = ReadRestartReason();
		var interrupted = Store.InvocationsInState(InvocationStates.Running, InvocationStates.Queued);
		foreach(var inv in interrupted) {
			var culprit = restartReason?.InvocationId == inv.Id;
			Db.Write(u => {
				inv.State = InvocationStates.Unknown;
				inv.Error = culprit ? $"Runtime was force-restarted while this invocation ran past its deadline ({restartReason!.Reason})." : "Runtime stopped while this invocation was running; its outcome is unknown.";
				inv.FinishedAt = Clock.Now;
				Store.UpdateInvocation(u, inv);
				if(culprit) {
					u.Execute("UPDATE tool_versions SET state='quarantined' WHERE id=@ToolVersion AND kind != 'builtin'", inv);
					u.Journal("tool.quarantine_considered", inv.ProjectId, "tool_invocation", inv.Id, inv.AgentId, new { inv.ToolVersion });
				}
			});
			if(inv.ParentInvocationId is not null) continue;
			// Give the model a result for the dangling call so its exchange stays valid, stating the uncertainty.
			var session = Store.GetSession(inv.SessionId);
			if(session is null) continue;
			var hasResult = Store.Items(inv.SessionId).Any(i => i.Kind == ItemKinds.ToolResult && JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!.CallId == inv.CallId);
			if(hasResult) continue;
			var text = $"OUTCOME UNKNOWN: the runtime restarted while {inv.ToolName} was running (invocation {inv.Id}). " +
				(culprit ? "The supervisor terminated the runtime because this invocation exceeded its deadline. Do not simply retry it; find a bounded approach. " : "") +
				"Any side effects may or may not have happened. Check the destination state before relying on or repeating it.";
			Db.Write(u => Store.AppendItem(u, inv.SessionId, ItemKinds.ToolResult, new ToolResultPayload {
				CallId = inv.CallId, ToolName = inv.ToolName, ToolVersion = inv.ToolVersion, InvocationId = inv.Id, IsError = true, Text = text, Chars = text.Length,
				Description = "outcome unknown after restart",
			}, Tokens.Estimate(text)));
			// Fill any other unanswered calls from the same assistant message as not started.
			FillUnansweredCalls(inv.SessionId);
		}
		foreach(var session in Store.ActiveSessions())
			FillUnansweredCalls(session.Id);
		foreach(var req in Store.ModelRequestsInState("dispatched")) {
			Db.Write(u => {
				req.State = "unknown";
				req.Error = "Runtime stopped while the request was in flight; usage is estimated pending reconciliation.";
				req.FinishedAt = Clock.Now;
				Store.UpdateModelRequest(u, req);
				var quote = JsonUtil.Deserialize<Quote>(req.Quote)!;
				var charge = Pricing.Settle(quote, new Usage { InputTokens = quote.EstimatedInputTokens, Reported = false });
				Ledger.Settle(u, req, charge with { CashBasis = charge.CashNanos is null ? "unknown" : "estimated" }, req.Purpose == "turn" ? "direct" : req.Purpose, null);
				u.Journal("model.reconciled", req.ProjectId, "model_request", req.Id, req.AgentId, new { state = "unknown" });
			});
		}
		Db.Write(u => u.Execute("UPDATE sessions SET state='idle' WHERE state='running'"));
		if(restartReason is not null) File.Delete(RestartReasonPath);
	}

	void FillUnansweredCalls(string sessionId) {
		var items = Store.Items(sessionId);
		var lastAssistant = items.LastOrDefault(i => i.Kind == ItemKinds.Assistant);
		if(lastAssistant is null) return;
		var a = JsonUtil.Deserialize<AssistantPayload>(lastAssistant.Payload)!;
		var answered = items.Where(i => i.Seq > lastAssistant.Seq && i.Kind == ItemKinds.ToolResult).Select(i => JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!.CallId).ToHashSet();
		foreach(var call in a.ToolCalls.Where(c => !answered.Contains(c.Id))) {
			var text = $"NOT EXECUTED: the runtime restarted before {call.Name} ran. It had no effect; call it again if still needed.";
			Db.Write(u => Store.AppendItem(u, sessionId, ItemKinds.ToolResult, new ToolResultPayload {
				CallId = call.Id, ToolName = call.Name, ToolVersion = "unknown", InvocationId = "none", IsError = true, Text = text, Chars = text.Length, Description = "not executed",
			}, Tokens.Estimate(text)));
		}
	}

	public string RestartReasonPath => Path.Combine(Options.Home, "runtime", "restart-reason.json");
	public sealed record RestartReason(string? InvocationId, string Reason, long At);
	RestartReason? ReadRestartReason() {
		try { return File.Exists(RestartReasonPath) ? JsonUtil.Deserialize<RestartReason>(File.ReadAllText(RestartReasonPath)) : null; } catch { return null; }
	}

	/// <summary>Stops dispatching new steps and waits for sessions to reach safe boundaries.</summary>
	public async Task<bool> DrainAsync(TimeSpan timeout) {
		Draining = true;
		Db.Write(u => u.Journal("runtime.draining", null, payload: new { timeout_ms = timeout.TotalMilliseconds }));
		var deadline = DateTime.UtcNow + timeout;
		while(DateTime.UtcNow < deadline) {
			if(Hosts.Values.All(h => !h.IsRunning)) return true;
			await Task.Delay(100);
		}
		return Hosts.Values.All(h => !h.IsRunning);
	}

	public void Undrain() {
		Draining = false;
		foreach(var h in Hosts.Values) h.Wake();
	}

	public void Dispose() {
		foreach(var host in Hosts.Values) host.Dispose();
		Hosts.Clear();
		Db.Write(u => {
			u.Execute("UPDATE runtime_generations SET stopped_at=@now, stop_reason='clean' WHERE generation=@Generation", new { now = Clock.Now, Generation });
			u.Journal("runtime.stopped", null, payload: new { Generation });
		});
	}

	// ---- Hosts ----

	public SessionHost? GetHost(string sessionId) {
		if(Hosts.TryGetValue(sessionId, out var h)) return h;
		var session = Store.GetSession(sessionId);
		if(session is null || session.State == "finished") return null;
		var host = Hosts.GetOrAdd(sessionId, _ => new SessionHost(this, session));
		host.Delta += (sid, d) => Delta?.Invoke(sid, d);
		return host;
	}

	public IReadOnlyCollection<SessionHost> LiveHosts => Hosts.Values.ToList();

	public void Wake(string agentId) {
		if(Draining) return;
		var agent = Store.GetAgent(agentId);
		if(agent?.PrimarySessionId is null || !AgentStates.IsLive(agent.State)) return;
		GetHost(agent.PrimarySessionId)?.Wake();
	}

	void ReleaseHost(string sessionId) {
		if(Hosts.TryRemove(sessionId, out var host))
			// Disposal must not run on the host's own thread while it is still unwinding.
			_ = Task.Run(() => host.Dispose());
	}

	public bool IsPaused(string sessionId) {
		var s = Store.GetSession(sessionId);
		if(s is null) return true;
		return s.State == "paused" || Store.GetAgent(s.AgentId)?.State == AgentStates.Paused;
	}

	// ---- Policy and context ----

	public ContextPolicy PolicyFor(Agent agent, Session session) =>
		Options.PolicyOverride?.Invoke(agent, session) ?? (agent.Lifetime == Lifetimes.Ephemeral ? Options.EphemeralPolicy : Options.PersistentPolicy);

	public ContextViewState CurrentView(string sessionId) =>
		Store.CurrentView(sessionId) is { } v ? JsonUtil.Deserialize<ContextViewState>(v.State)! : new ContextViewState();

	public BuiltContext BuildContext(SessionHost host, ContextPolicy policy) {
		var session = host.Session;
		var agent = host.Agent;
		var view = CurrentView(session.Id);
		var items = Store.Items(session.Id);
		var summary = view.SummaryItemId is null ? null : items.FirstOrDefault(i => i.Id == view.SummaryItemId);
		return ContextBuilder.Build(Prompts.System(this, agent, session, host), view, items, summary, session.TurnCount, policy, session.TokenRatio, session.ContextRevision);
	}

	public void CommitView(string sessionId, ContextViewState view, string reason) =>
		Db.Write(u => Store.CommitView(u, sessionId, JsonUtil.Serialize(view), reason));

	public void InitializeToolCache(SessionHost host, Agent agent, ContextPolicy policy) {
		var names = Tools.DefaultsFor(agent);
		host.Cache.Load(ToolRegistry.CoreTools, policy.ToolTokenBudget, pinned: true);
		var goal = $"{agent.Title} {agent.Instructions} {host.Session.Purpose}";
		var extra = Tools.Search(host.Session.ProjectId, goal, 6).Select(x => x.Tool.Name).Where(n => !names.Contains(n)).Take(3);
		host.Cache.Load(names.Concat(extra), policy.ToolTokenBudget);
	}

	public bool ToolAllowed(Agent agent, Session session, string tool) {
		if(session.Kind == "service")
			return Prompts.ServiceTools.Contains(tool);
		return tool switch {
			"reply_to_user" => agent.ManagerId is null && session.Kind == "primary",
			"create_agent" or "retire_agent" or "reassign_agent" or "assign_work" or "pause_agent" or "resume_agent" => agent.Role == Roles.Manager,
			_ => true,
		};
	}

	public string? CurrentObjective(string agentId) => Db.Read(c => c.ExecuteScalar<string?>(
		"SELECT id FROM objectives WHERE owner_id=@agentId AND state IN ('active','verifying','ready','blocked') ORDER BY updated_at DESC LIMIT 1", new { agentId }));

	public string? SponsorFor(Session session) =>
		session.Kind is "consultation" or "service" && JsonUtil.Parse(session.Purpose)?["requester_agent_id"]?.GetValue<string>() is { } r ? r : null;

	// ---- Turn completion and failure handling ----

	public void OnTurnFinished(SessionHost host, string? content) {
		Failures.TryRemove(host.SessionId, out _);
		var session = host.Session;
		var agent = host.Agent;
		if(session.Kind == "consultation") {
			FinishConsultation(host, session, agent, content);
			return;
		}
		if(session.Kind == "service") {
			FinishService(host, session, content);
			return;
		}
		if(agent.Lifetime == Lifetimes.Ephemeral) {
			FinishEphemeral(host, session, agent, content);
			return;
		}
		if(agent.ManagerId is null && !string.IsNullOrWhiteSpace(content))
			Db.Write(u => Store.AppendConversation(u, agent.ProjectId, "manager", agent.Id, content.Trim()));
	}

	public void HandleSessionFailure(SessionHost host, Exception e) {
		var count = Failures.AddOrUpdate(host.SessionId, 1, (_, c) => c + 1);
		var agent = host.Agent;
		var retryable = e is ProviderException { Retryable: true } && count < 5;
		Db.Write(u => u.Journal("session.failed", agent.ProjectId, "session", host.SessionId, agent.Id, new { error = TextUtil.Truncate(e.ToString(), 2000), attempt = count, retryable }));
		Console.Error.WriteLine($"[{agent.Name}] step failed (attempt {count}): {e.Message}");
		if(retryable) {
			_ = Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 2 << count))).ContinueWith(_ => host.Wake());
			return;
		}
		Failures.TryRemove(host.SessionId, out _);
		PauseAgent(agent.Id, null, $"Execution failed and was paused: {TextUtil.Truncate(e.Message, 600)}");
	}

	public void HandleBudgetExhausted(SessionHost host, string message) {
		var agent = host.Agent;
		PauseAgent(agent.Id, null, message);
	}

	public void PauseAgent(string agentId, string? actorId, string reason) {
		var agent = Store.GetAgent(agentId)!;
		Db.Write(u => {
			Store.SetAgentState(u, agentId, AgentStates.Paused);
			u.Journal("agent.paused", agent.ProjectId, "agent", agentId, actorId, new { reason });
			if(agent.ManagerId is { } managerId && actorId != managerId)
				Store.InsertNotification(u, NewNotification(agent.ProjectId, NotificationTypes.Escalation, agentId, managerId, $"{agent.Name} was paused: {reason}. Resume it with resume_agent when appropriate.", null));
			else if(agent.ManagerId is null && actorId is null)
				Store.AppendConversation(u, agent.ProjectId, "system", agentId, $"{agent.Name} (root manager) was paused: {reason}");
		});
		if(agent.ManagerId is { } m) Wake(m);
	}

	public void ResumeAgent(string agentId, string? actorId) {
		var agent = Store.GetAgent(agentId)!;
		if(agent.State != AgentStates.Paused) return;
		Db.Write(u => {
			Store.SetAgentState(u, agentId, AgentStates.Sleeping);
			u.Journal("agent.resumed", agent.ProjectId, "agent", agentId, actorId, new { });
		});
		Failures.TryRemove(agent.PrimarySessionId ?? "", out _);
		Wake(agentId);
	}

	// ---- Communication ----

	public static Notification NewNotification(string projectId, string type, string? from, string to, string body, string? objectiveId, string? causalParent = null, string? dedupe = null) => new() {
		Id = Ids.New("ntf"), ProjectId = projectId, Type = type, FromAgentId = from, ToAgentId = to, ObjectiveId = objectiveId, Body = body,
		Wakes = NotificationTypes.Wakes(type), State = "pending", CausalParentId = causalParent, DedupeKey = dedupe, CreatedAt = Clock.Now,
	};

	public Notification Notify(string projectId, string type, string? from, string to, string body, string? objectiveId = null, string? causalParent = null, string? dedupe = null) {
		var n = Db.Write(u => Store.InsertNotification(u, NewNotification(projectId, type, from, to, body, objectiveId, causalParent, dedupe)));
		if(n.Wakes) Wake(to);
		return n;
	}

	public ConversationEntry PostUserMessage(string projectId, string text) {
		var project = Store.GetProject(projectId) ?? throw new DomainException($"Unknown project {projectId}");
		var root = project.RootAgentId ?? throw new DomainException("Project has no root manager");
		var entry = Db.Write(u => {
			var e = Store.AppendConversation(u, projectId, "user", null, text);
			Store.InsertNotification(u, NewNotification(projectId, NotificationTypes.UserMessage, null, root, text, null, e.Id, $"conv:{e.Id}"));
			return e;
		});
		var rootAgent = Store.GetAgent(root)!;
		// A user message is direction: it resumes a paused root manager.
		if(rootAgent.State == AgentStates.Paused) ResumeAgent(root, null);
		Wake(root);
		return entry;
	}

	public string FormatInbox(List<Notification> pending) {
		var sb = new StringBuilder("[Inbox]\n");
		foreach(var n in pending) {
			var from = n.FromAgentId is null ? (n.Type == NotificationTypes.UserMessage ? "the user" : "the runtime") : AgentLabel(n.FromAgentId);
			sb.Append($"--- {n.Type} from {from}{(n.ObjectiveId is null ? "" : $" regarding objective {n.ObjectiveId}")} (message {n.Id}) ---\n");
			sb.Append(n.Body.Trim()).Append("\n\n");
		}
		return sb.ToString().TrimEnd();
	}

	public string AgentLabel(string agentId) => Store.GetAgent(agentId) is { } a ? $"{a.Name} ({a.Id}, {a.Title})" : agentId;
}
