using Ainur.Core.Model;
using Dapper;

namespace Ainur.Core.Persistence;

public class DomainException(string message) : Exception(message);

/// <summary>Typed access to current-state tables. Mutations take a write unit so callers can compose them atomically.</summary>
public sealed class Store(Db db) {
	public readonly Db Db = db;

	// ---- Projects ----

	public Project? GetProject(string id) => Db.Read(c => c.QuerySingleOrDefault<Project>("SELECT * FROM projects WHERE id=@id", new { id }));
	public List<Project> ListProjects() => Db.Read(c => c.Query<Project>("SELECT * FROM projects ORDER BY created_at").AsList());

	public void InsertProject(Db.Unit u, Project p) {
		u.Execute("""
			INSERT INTO projects(id,name,description,workspace_path,root_agent_id,root_objective_id,state,effective_budget_nanos,cash_ceiling_nanos,created_at,updated_at)
			VALUES(@Id,@Name,@Description,@WorkspacePath,@RootAgentId,@RootObjectiveId,@State,@EffectiveBudgetNanos,@CashCeilingNanos,@CreatedAt,@UpdatedAt)
			""", p);
		u.Journal("project.created", p.Id, "project", p.Id, payload: new { p.Name, p.Description, p.WorkspacePath });
	}

	public void UpdateProject(Db.Unit u, Project p) {
		p.UpdatedAt = Clock.Now;
		u.Execute("""
			UPDATE projects SET name=@Name, description=@Description, workspace_path=@WorkspacePath, root_agent_id=@RootAgentId,
				root_objective_id=@RootObjectiveId, state=@State, effective_budget_nanos=@EffectiveBudgetNanos,
				cash_ceiling_nanos=@CashCeilingNanos, updated_at=@UpdatedAt WHERE id=@Id
			""", p);
	}

	// ---- Agents ----

	public Agent? GetAgent(string id) => Db.Read(c => GetAgent(c, id));
	static Agent? GetAgent(Microsoft.Data.Sqlite.SqliteConnection c, string id) => c.QuerySingleOrDefault<Agent>("SELECT * FROM agents WHERE id=@id", new { id });
	public Agent? GetAgent(Db.Unit u, string id) => u.Single<Agent>("SELECT * FROM agents WHERE id=@id", new { id });
	public List<Agent> ListAgents(string projectId) => Db.Read(c => c.Query<Agent>("SELECT * FROM agents WHERE project_id=@projectId ORDER BY created_at", new { projectId }).AsList());
	public List<Agent> ListLiveAgents() => Db.Read(c => c.Query<Agent>("SELECT * FROM agents WHERE state NOT IN ('retired','terminated')").AsList());

	public Agent? FindAgent(string projectId, string idOrName) => Db.Read(c => c.QuerySingleOrDefault<Agent>(
		"SELECT * FROM agents WHERE project_id=@projectId AND (id=@idOrName OR lower(name)=lower(@idOrName)) ORDER BY state IN ('retired','terminated') LIMIT 1",
		new { projectId, idOrName }));

	public void InsertAgent(Db.Unit u, Agent a) {
		if(a.ManagerId is not null) {
			var manager = GetAgent(u, a.ManagerId) ?? throw new DomainException($"Manager {a.ManagerId} does not exist");
			if(manager.ProjectId != a.ProjectId) throw new DomainException("Manager belongs to another project");
			if(!AgentStates.IsLive(manager.State)) throw new DomainException($"Manager {manager.Name} is not active");
		}
		u.Execute("""
			INSERT INTO agents(id,project_id,name,title,role,lifetime,manager_id,model_id,reasoning_effort,state,compaction_mode,instructions,termination_condition,primary_session_id,created_by,created_at,updated_at)
			VALUES(@Id,@ProjectId,@Name,@Title,@Role,@Lifetime,@ManagerId,@ModelId,@ReasoningEffort,@State,@CompactionMode,@Instructions,@TerminationCondition,@PrimarySessionId,@CreatedBy,@CreatedAt,@UpdatedAt)
			""", a);
		u.Journal("agent.created", a.ProjectId, "agent", a.Id, a.CreatedBy, new { a.Name, a.Title, a.Role, a.Lifetime, a.ManagerId, a.ModelId });
	}

	public void UpdateAgent(Db.Unit u, Agent a) {
		a.UpdatedAt = Clock.Now;
		u.Execute("""
			UPDATE agents SET name=@Name, title=@Title, role=@Role, manager_id=@ManagerId, model_id=@ModelId, reasoning_effort=@ReasoningEffort,
				state=@State, compaction_mode=@CompactionMode, instructions=@Instructions, termination_condition=@TerminationCondition,
				primary_session_id=@PrimarySessionId, updated_at=@UpdatedAt, retired_at=@RetiredAt WHERE id=@Id
			""", a);
	}

	public void SetAgentState(Db.Unit u, string agentId, string state) =>
		u.Execute("UPDATE agents SET state=@state, updated_at=@now WHERE id=@agentId", new { agentId, state, now = Clock.Now });

	/// <summary>Moves an agent under a new manager, rejecting any change that would create a reporting cycle.</summary>
	public void Reassign(Db.Unit u, string agentId, string newManagerId, string? actorId) {
		var agent = GetAgent(u, agentId) ?? throw new DomainException($"Unknown agent {agentId}");
		var manager = GetAgent(u, newManagerId) ?? throw new DomainException($"Unknown manager {newManagerId}");
		if(agent.ProjectId != manager.ProjectId) throw new DomainException("Agents belong to different projects");
		if(!AgentStates.IsLive(manager.State)) throw new DomainException($"{manager.Name} is not active");
		if(agent.ManagerId is null) throw new DomainException("The root manager cannot report to another agent; replace the root explicitly");
		for(var cursor = manager; cursor is not null; cursor = cursor.ManagerId is null ? null : GetAgent(u, cursor.ManagerId))
			if(cursor.Id == agent.Id)
				throw new DomainException($"Moving {agent.Name} under {manager.Name} would create a reporting cycle");
		var previous = agent.ManagerId;
		agent.ManagerId = manager.Id;
		UpdateAgent(u, agent);
		u.Journal("agent.reassigned", agent.ProjectId, "agent", agent.Id, actorId, new { from = previous, to = manager.Id });
	}

	public List<Agent> Subordinates(string agentId) => Db.Read(c => c.Query<Agent>("SELECT * FROM agents WHERE manager_id=@agentId AND state NOT IN ('retired','terminated')", new { agentId }).AsList());

	/// <summary>True when <paramref name="agentId"/> is <paramref name="managerId"/> or reports to it, directly or indirectly.</summary>
	public bool IsInSubtree(string managerId, string agentId) {
		for(var cursor = GetAgent(agentId); cursor is not null; cursor = cursor.ManagerId is null ? null : GetAgent(cursor.ManagerId))
			if(cursor.Id == managerId) return true;
		return false;
	}

	// ---- Identity ----

	public IdentityRevision? CurrentIdentity(string agentId) => Db.Read(c => c.QuerySingleOrDefault<IdentityRevision>(
		"SELECT * FROM identity_revisions WHERE agent_id=@agentId ORDER BY revision DESC LIMIT 1", new { agentId }));

	public List<IdentityRevision> IdentityHistory(string agentId) => Db.Read(c => c.Query<IdentityRevision>(
		"SELECT * FROM identity_revisions WHERE agent_id=@agentId ORDER BY revision", new { agentId }).AsList());

	public IdentityRevision WriteIdentity(Db.Unit u, string agentId, string content, string? authorId, int? expectedRevision = null) {
		var current = u.Scalar<int?>("SELECT MAX(revision) FROM identity_revisions WHERE agent_id=@agentId", new { agentId }) ?? 0;
		if(expectedRevision is not null && expectedRevision != current)
			throw new DomainException($"Identity revision conflict: expected {expectedRevision}, current is {current}");
		var rev = new IdentityRevision { Id = Ids.New("idr"), AgentId = agentId, Revision = current + 1, Content = content, AuthorAgentId = authorId, CreatedAt = Clock.Now };
		u.Execute("INSERT INTO identity_revisions(id,agent_id,revision,content,author_agent_id,created_at) VALUES(@Id,@AgentId,@Revision,@Content,@AuthorAgentId,@CreatedAt)", rev);
		var projectId = u.Scalar<string>("SELECT project_id FROM agents WHERE id=@agentId", new { agentId });
		u.Journal("identity.revised", projectId, "agent", agentId, authorId, new { rev.Revision });
		return rev;
	}

	// ---- Objectives ----

	public Objective? GetObjective(string id) => Db.Read(c => c.QuerySingleOrDefault<Objective>("SELECT * FROM objectives WHERE id=@id", new { id }));
	public Objective? GetObjective(Db.Unit u, string id) => u.Single<Objective>("SELECT * FROM objectives WHERE id=@id", new { id });
	public List<Objective> ListObjectives(string projectId) => Db.Read(c => c.Query<Objective>("SELECT * FROM objectives WHERE project_id=@projectId ORDER BY sort_order, created_at", new { projectId }).AsList());
	public List<Objective> ObjectivesOwnedBy(string agentId) => Db.Read(c => c.Query<Objective>("SELECT * FROM objectives WHERE owner_id=@agentId ORDER BY created_at", new { agentId }).AsList());
	public List<Objective> Children(string objectiveId) => Db.Read(c => c.Query<Objective>("SELECT * FROM objectives WHERE parent_id=@objectiveId ORDER BY sort_order, created_at", new { objectiveId }).AsList());
	public List<(string ObjectiveId, string DependsOnId)> ListDependencies(string projectId) => Db.Read(c => c.Query<(string, string)>(
		"SELECT d.objective_id, d.depends_on_id FROM objective_dependencies d JOIN objectives o ON o.id=d.objective_id WHERE o.project_id=@projectId", new { projectId }).AsList());

	public void InsertObjective(Db.Unit u, Objective o, string? actorId) {
		if(o.ParentId is not null) {
			var parent = GetObjective(u, o.ParentId) ?? throw new DomainException($"Unknown parent objective {o.ParentId}");
			if(parent.ProjectId != o.ProjectId) throw new DomainException("Parent objective belongs to another project");
		}
		if(o.OwnerId is not null && GetAgent(u, o.OwnerId) is not { } owner)
			throw new DomainException($"Unknown owner {o.OwnerId}");
		u.Execute("""
			INSERT INTO objectives(id,project_id,parent_id,owner_id,delegated_by_id,title,description,completion_conditions,state,required,evidence,sort_order,created_at,updated_at)
			VALUES(@Id,@ProjectId,@ParentId,@OwnerId,@DelegatedById,@Title,@Description,@CompletionConditions,@State,@Required,@Evidence,@SortOrder,@CreatedAt,@UpdatedAt)
			""", o);
		u.Journal("objective.created", o.ProjectId, "objective", o.Id, actorId, new { o.Title, o.ParentId, o.OwnerId, o.State });
	}

	public void UpdateObjective(Db.Unit u, Objective o, string? actorId, object? change = null) {
		if(!ObjectiveStates.All.Contains(o.State)) throw new DomainException($"Unknown objective state '{o.State}'");
		if(o.State == ObjectiveStates.Complete) {
			var open = u.Query<Objective>("SELECT * FROM objectives WHERE parent_id=@Id AND required=1 AND state NOT IN ('complete','canceled')", o);
			if(open.Count > 0)
				throw new DomainException($"Cannot complete '{o.Title}': required children are still open: {string.Join(", ", open.Select(c => $"{c.Title} [{c.Id}, {c.State}]"))}. Complete them or rescope them (mark not required or canceled).");
			if(o.Evidence is "[]" or "")
				throw new DomainException($"Cannot complete '{o.Title}' without evidence. Record evidence appropriate to the outcome first.");
		}
		o.UpdatedAt = Clock.Now;
		u.Execute("""
			UPDATE objectives SET parent_id=@ParentId, owner_id=@OwnerId, delegated_by_id=@DelegatedById, title=@Title, description=@Description,
				completion_conditions=@CompletionConditions, state=@State, required=@Required, evidence=@Evidence, sort_order=@SortOrder, updated_at=@UpdatedAt WHERE id=@Id
			""", o);
		u.Journal("objective.updated", o.ProjectId, "objective", o.Id, actorId, change ?? new { o.State, o.OwnerId });
	}

	public void AddDependency(Db.Unit u, string objectiveId, string dependsOnId, string? actorId) {
		if(objectiveId == dependsOnId) throw new DomainException("An objective cannot depend on itself");
		// Reject cycles: walk from dependsOn through its dependencies looking for objectiveId.
		var seen = new HashSet<string>();
		var stack = new Stack<string>([dependsOnId]);
		while(stack.TryPop(out var cur)) {
			if(cur == objectiveId) throw new DomainException("That dependency would create a cycle");
			if(!seen.Add(cur)) continue;
			foreach(var next in u.Query<string>("SELECT depends_on_id FROM objective_dependencies WHERE objective_id=@cur", new { cur }))
				stack.Push(next);
		}
		u.Execute("INSERT OR IGNORE INTO objective_dependencies(objective_id, depends_on_id) VALUES(@objectiveId, @dependsOnId)", new { objectiveId, dependsOnId });
		var projectId = u.Scalar<string>("SELECT project_id FROM objectives WHERE id=@objectiveId", new { objectiveId });
		u.Journal("objective.dependency_added", projectId, "objective", objectiveId, actorId, new { depends_on = dependsOnId });
	}

	// ---- Sessions and transcript ----

	public Session? GetSession(string id) => Db.Read(c => c.QuerySingleOrDefault<Session>("SELECT * FROM sessions WHERE id=@id", new { id }));
	public Session? GetSession(Db.Unit u, string id) => u.Single<Session>("SELECT * FROM sessions WHERE id=@id", new { id });
	public List<Session> SessionsForAgent(string agentId) => Db.Read(c => c.Query<Session>("SELECT * FROM sessions WHERE agent_id=@agentId ORDER BY created_at", new { agentId }).AsList());
	public List<Session> ActiveSessions() => Db.Read(c => c.Query<Session>("SELECT * FROM sessions WHERE state != 'finished'").AsList());

	public void InsertSession(Db.Unit u, Session s) {
		u.Execute("""
			INSERT INTO sessions(id,project_id,agent_id,kind,state,model_id,compaction_mode,parent_session_id,checkpoint_seq,turn_count,next_seq,context_revision,token_ratio,purpose,result,created_at,updated_at)
			VALUES(@Id,@ProjectId,@AgentId,@Kind,@State,@ModelId,@CompactionMode,@ParentSessionId,@CheckpointSeq,@TurnCount,@NextSeq,@ContextRevision,@TokenRatio,@Purpose,@Result,@CreatedAt,@UpdatedAt)
			""", s);
		u.Journal("session.created", s.ProjectId, "session", s.Id, s.AgentId, new { s.Kind, s.ParentSessionId, s.Purpose });
	}

	public void UpdateSession(Db.Unit u, Session s) {
		s.UpdatedAt = Clock.Now;
		u.Execute("""
			UPDATE sessions SET state=@State, model_id=@ModelId, compaction_mode=@CompactionMode, turn_count=@TurnCount, next_seq=@NextSeq,
				context_revision=@ContextRevision, token_ratio=@TokenRatio, result=@Result, updated_at=@UpdatedAt WHERE id=@Id
			""", s);
	}

	public SessionItem AppendItem(Db.Unit u, string sessionId, string kind, object payload, int tokenEstimate) {
		var s = GetSession(u, sessionId) ?? throw new DomainException($"Unknown session {sessionId}");
		var item = new SessionItem {
			Id = Ids.New("itm"), SessionId = sessionId, Seq = s.NextSeq, Kind = kind, Turn = s.TurnCount,
			Payload = payload as string ?? Json.Serialize(payload), TokenEstimate = tokenEstimate, CreatedAt = Clock.Now,
		};
		u.Execute("INSERT INTO session_items(id,session_id,seq,kind,turn,payload,token_estimate,created_at) VALUES(@Id,@SessionId,@Seq,@Kind,@Turn,@Payload,@TokenEstimate,@CreatedAt)", item);
		u.Execute("UPDATE sessions SET next_seq=next_seq+1, updated_at=@now WHERE id=@sessionId", new { sessionId, now = Clock.Now });
		u.Journal("session.item", s.ProjectId, "session", sessionId, s.AgentId, new { item.Id, item.Seq, item.Kind });
		return item;
	}

	public List<SessionItem> Items(string sessionId, long afterSeq = 0, long? throughSeq = null) => Db.Read(c => c.Query<SessionItem>(
		"SELECT * FROM session_items WHERE session_id=@sessionId AND seq>@afterSeq AND (@throughSeq IS NULL OR seq<=@throughSeq) ORDER BY seq",
		new { sessionId, afterSeq, throughSeq }).AsList());

	public SessionItem? GetItem(string id) => Db.Read(c => c.QuerySingleOrDefault<SessionItem>("SELECT * FROM session_items WHERE id=@id", new { id }));

	public (int Revision, string State)? CurrentView(string sessionId) => Db.Read(c => c.QuerySingleOrDefault<(int, string)?>(
		"SELECT revision, state FROM context_views WHERE session_id=@sessionId ORDER BY revision DESC LIMIT 1", new { sessionId }));

	public int CommitView(Db.Unit u, string sessionId, string state, string reason) {
		var rev = (u.Scalar<int?>("SELECT MAX(revision) FROM context_views WHERE session_id=@sessionId", new { sessionId }) ?? 0) + 1;
		u.Execute("INSERT INTO context_views(session_id,revision,state,reason,created_at) VALUES(@sessionId,@rev,@state,@reason,@now)", new { sessionId, rev, state, reason, now = Clock.Now });
		u.Execute("UPDATE sessions SET context_revision=@rev WHERE id=@sessionId", new { sessionId, rev });
		return rev;
	}

	// ---- Notifications and conversation ----

	/// <summary>Inserts a notification. Duplicate delivery with the same dedupe key is a no-op returning the original.</summary>
	public Notification InsertNotification(Db.Unit u, Notification n) {
		if(n.DedupeKey is not null && u.Single<Notification>("SELECT * FROM notifications WHERE dedupe_key=@DedupeKey", n) is { } existing)
			return existing;
		u.Execute("""
			INSERT INTO notifications(id,project_id,type,from_agent_id,to_agent_id,objective_id,body,wakes,state,causal_parent_id,dedupe_key,created_at)
			VALUES(@Id,@ProjectId,@Type,@FromAgentId,@ToAgentId,@ObjectiveId,@Body,@Wakes,@State,@CausalParentId,@DedupeKey,@CreatedAt)
			""", n);
		u.Journal("notification.sent", n.ProjectId, "notification", n.Id, n.FromAgentId, new { n.Type, n.ToAgentId, n.ObjectiveId, preview = TextUtil.Truncate(n.Body, 200) });
		return n;
	}

	public List<Notification> PendingNotifications(string agentId) => Db.Read(c => c.Query<Notification>(
		"SELECT * FROM notifications WHERE to_agent_id=@agentId AND state='pending' ORDER BY created_at", new { agentId }).AsList());

	public List<Notification> ListNotifications(string projectId, int limit = 200) => Db.Read(c => c.Query<Notification>(
		"SELECT * FROM notifications WHERE project_id=@projectId ORDER BY created_at DESC LIMIT @limit", new { projectId, limit }).AsList());

	public void MarkDelivered(Db.Unit u, IEnumerable<string> ids) {
		foreach(var id in ids)
			u.Execute("UPDATE notifications SET state='delivered', delivered_at=@now WHERE id=@id", new { id, now = Clock.Now });
	}

	public List<ConversationEntry> Conversation(string projectId) => Db.Read(c => c.Query<ConversationEntry>(
		"SELECT * FROM conversation WHERE project_id=@projectId ORDER BY created_at, rowid", new { projectId }).AsList());

	public ConversationEntry AppendConversation(Db.Unit u, string projectId, string author, string? agentId, string body) {
		var e = new ConversationEntry { Id = Ids.New("cnv"), ProjectId = projectId, Author = author, AgentId = agentId, Body = body, CreatedAt = Clock.Now };
		u.Execute("INSERT INTO conversation(id,project_id,author,agent_id,body,created_at) VALUES(@Id,@ProjectId,@Author,@AgentId,@Body,@CreatedAt)", e);
		u.Journal("conversation.message", projectId, "conversation", e.Id, agentId, new { e.Author, e.Body });
		return e;
	}

	// ---- Models ----

	public List<ModelInfo> ListModels() => Db.Read(c => c.Query<ModelInfo>("SELECT * FROM models ORDER BY provider, id").AsList());
	public ModelInfo? GetModel(string id) => Db.Read(c => c.QuerySingleOrDefault<ModelInfo>("SELECT * FROM models WHERE id=@id", new { id }));

	public void UpsertModel(Db.Unit u, ModelInfo m) => u.Execute("""
		INSERT INTO models(id,provider,upstream_model,display_name,context_tokens,max_output_tokens,input_per_million,cached_input_per_million,output_per_million,price_provenance,billing,premium,enabled,notes)
		VALUES(@Id,@Provider,@UpstreamModel,@DisplayName,@ContextTokens,@MaxOutputTokens,@InputPerMillion,@CachedInputPerMillion,@OutputPerMillion,@PriceProvenance,@Billing,@Premium,@Enabled,@Notes)
		ON CONFLICT(id) DO NOTHING
		""", m);

	// ---- Tool invocations, model requests, costs ----

	public void InsertInvocation(Db.Unit u, ToolInvocation i) {
		u.Execute("""
			INSERT INTO tool_invocations(id,project_id,session_id,agent_id,call_id,tool_name,tool_version,arguments,state,result_artifact,result_chars,error,parent_invocation_id,deadline_at,started_at,finished_at,created_at)
			VALUES(@Id,@ProjectId,@SessionId,@AgentId,@CallId,@ToolName,@ToolVersion,@Arguments,@State,@ResultArtifact,@ResultChars,@Error,@ParentInvocationId,@DeadlineAt,@StartedAt,@FinishedAt,@CreatedAt)
			""", i);
		u.Journal("tool.invoked", i.ProjectId, "tool_invocation", i.Id, i.AgentId, new { i.ToolName, i.CallId, i.State });
	}

	public void UpdateInvocation(Db.Unit u, ToolInvocation i) {
		u.Execute("UPDATE tool_invocations SET state=@State, result_artifact=@ResultArtifact, result_chars=@ResultChars, error=@Error, deadline_at=@DeadlineAt, started_at=@StartedAt, finished_at=@FinishedAt WHERE id=@Id", i);
		if(i.State is not InvocationStates.Running)
			u.Journal("tool.finished", i.ProjectId, "tool_invocation", i.Id, i.AgentId, new { i.ToolName, i.State, i.ResultChars, error = i.Error is null ? null : TextUtil.Truncate(i.Error, 300) });
	}

	public ToolInvocation? GetInvocation(string id) => Db.Read(c => c.QuerySingleOrDefault<ToolInvocation>("SELECT * FROM tool_invocations WHERE id=@id", new { id }));
	public List<ToolInvocation> InvocationsInState(params string[] states) => Db.Read(c => c.Query<ToolInvocation>("SELECT * FROM tool_invocations WHERE state IN @states", new { states }).AsList());

	public void InsertModelRequest(Db.Unit u, ModelRequestRecord r) {
		u.Execute("""
			INSERT INTO model_requests(id,project_id,session_id,agent_id,objective_id,purpose,model_id,provider,upstream_model,state,quote,usage,request_artifact,response_artifact,error,context_revision,started_at,finished_at)
			VALUES(@Id,@ProjectId,@SessionId,@AgentId,@ObjectiveId,@Purpose,@ModelId,@Provider,@UpstreamModel,@State,@Quote,@Usage,@RequestArtifact,@ResponseArtifact,@Error,@ContextRevision,@StartedAt,@FinishedAt)
			""", r);
	}

	public void UpdateModelRequest(Db.Unit u, ModelRequestRecord r) =>
		u.Execute("UPDATE model_requests SET state=@State, usage=@Usage, response_artifact=@ResponseArtifact, error=@Error, finished_at=@FinishedAt WHERE id=@Id", r);

	public List<ModelRequestRecord> ModelRequestsInState(string state) => Db.Read(c => c.Query<ModelRequestRecord>("SELECT * FROM model_requests WHERE state=@state", new { state }).AsList());

	public void InsertCost(Db.Unit u, CostEvent e) {
		u.Execute("""
			INSERT INTO cost_events(id,project_id,objective_id,agent_id,sponsor_agent_id,session_id,model_request_id,category,cash_nanos,cash_basis,effective_nanos,valuation,created_at)
			VALUES(@Id,@ProjectId,@ObjectiveId,@AgentId,@SponsorAgentId,@SessionId,@ModelRequestId,@Category,@CashNanos,@CashBasis,@EffectiveNanos,@Valuation,@CreatedAt)
			""", e);
		u.Journal("cost.recorded", e.ProjectId, "cost_event", e.Id, e.AgentId, new { e.Category, e.CashNanos, e.CashBasis, e.EffectiveNanos, e.ModelRequestId });
	}

	public List<CostEvent> CostEvents(string projectId) => Db.Read(c => c.Query<CostEvent>("SELECT * FROM cost_events WHERE project_id=@projectId ORDER BY created_at", new { projectId }).AsList());

	// ---- Journal ----

	public List<JournalEvent> Events(string? projectId, long afterId = 0, int limit = 500) => Db.Read(c => c.Query<JournalEvent>(
		"SELECT * FROM events WHERE (@projectId IS NULL OR project_id=@projectId) AND id>@afterId ORDER BY id DESC LIMIT @limit",
		new { projectId, afterId, limit }).Reverse().AsList());
}
