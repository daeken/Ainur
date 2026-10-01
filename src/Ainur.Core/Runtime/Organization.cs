using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Dapper;

namespace Ainur.Core.Runtime;

public sealed class NewAgent {
	public required string Name { get; init; }
	public string Title { get; init; } = "";
	public string Role { get; init; } = Roles.Specialist;
	public string Lifetime { get; init; } = Lifetimes.Persistent;
	public string? ManagerId { get; init; }
	public string? ModelId { get; init; }
	public string? ReasoningEffort { get; init; }
	public string? CompactionMode { get; init; }
	public string Instructions { get; init; } = "";
	public string? TerminationCondition { get; init; }
	public string? Pronouns { get; init; }
}

public sealed partial class AinurRuntime {
	public Project CreateProject(string name, string description, string? workspacePath, decimal? budgetDollars = null, string? managerModelId = null, string managerName = "Manwë") {
		if(workspacePath is not null) {
			workspacePath = Path.GetFullPath(workspacePath.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), workspacePath[2..]) : workspacePath);
			Directory.CreateDirectory(workspacePath);
		}
		var now = Clock.Now;
		var project = new Project {
			Id = Ids.New("prj"), Name = name, Description = description, WorkspacePath = workspacePath, State = "active",
			EffectiveBudgetNanos = Money.FromDollars(budgetDollars ?? Options.DefaultBudgetDollars), CreatedAt = now, UpdatedAt = now,
		};
		Db.Write(u => {
			Store.InsertProject(u, project);
			var root = InsertAgent(u, project.Id, new NewAgent {
				Name = managerName, Title = "Root manager", Role = Roles.Manager, ModelId = managerModelId ?? Options.ManagerModelId,
				Instructions = "You are the root manager of this project. The user talks only to you. Plan the work as objectives, build and organize a team, delegate implementation and verification, and keep the user informed of outcomes, direction changes, blockers, and funding problems.",
			}, null);
			var objective = new Objective {
				Id = Ids.New("obj"), ProjectId = project.Id, OwnerId = root.Id, Title = name, Description = description,
				CompletionConditions = "Defined and refined by the root manager with the user.", State = ObjectiveStates.Active, CreatedAt = now, UpdatedAt = now,
			};
			Store.InsertObjective(u, objective, null);
			project.RootAgentId = root.Id;
			project.RootObjectiveId = objective.Id;
			Store.UpdateProject(u, project);
		});
		return project;
	}

	public Agent CreateAgent(string projectId, NewAgent spec, string? actorId) {
		var agent = Db.Write(u => InsertAgent(u, projectId, spec, actorId));
		return agent;
	}

	Agent InsertAgent(Db.Unit u, string projectId, NewAgent spec, string? actorId) {
		var now = Clock.Now;
		var modelId = spec.ModelId ?? (spec.Role == Roles.Manager ? Options.ManagerModelId : Options.SpecialistModelId);
		var model = u.Single<ModelInfo>("SELECT * FROM models WHERE id=@modelId", new { modelId }) ?? throw new DomainException($"Unknown model '{modelId}'");
		if(!model.Enabled || !Providers.Has(model.Provider))
			throw new DomainException($"Model '{modelId}' is not usable yet (provider '{model.Provider}' has no live adapter). Usable models: {string.Join(", ", Store.ListModels().Where(m => m.Enabled && Providers.Has(m.Provider)).Select(m => m.Id))}");
		if(spec.Role is not (Roles.Manager or Roles.Specialist)) throw new DomainException($"Unknown role '{spec.Role}'");
		if(spec.Lifetime is not (Lifetimes.Persistent or Lifetimes.Ephemeral)) throw new DomainException($"Unknown lifetime '{spec.Lifetime}'");
		var name = spec.Name.Trim();
		if(name.Length == 0) throw new DomainException("Agent name is required");
		if(u.Scalar<long>("SELECT COUNT(*) FROM agents WHERE project_id=@projectId AND lower(name)=lower(@name) AND state NOT IN ('retired','terminated')", new { projectId, name }) > 0)
			throw new DomainException($"An active agent named {name} already exists in this project");
		var agent = new Agent {
			Id = Ids.New("agt"), ProjectId = projectId, Name = name, Title = spec.Title, Role = spec.Role, Lifetime = spec.Lifetime, ManagerId = spec.ManagerId,
			ModelId = modelId, ReasoningEffort = spec.ReasoningEffort, State = AgentStates.Sleeping,
			CompactionMode = spec.CompactionMode ?? (spec.Lifetime == Lifetimes.Persistent ? CompactionModes.Rolling : CompactionModes.Full),
			Instructions = spec.Instructions, TerminationCondition = spec.TerminationCondition, CreatedBy = actorId, CreatedAt = now, UpdatedAt = now,
		};
		Store.InsertAgent(u, agent);
		var session = new Session {
			Id = Ids.New("ses"), ProjectId = projectId, AgentId = agent.Id, Kind = "primary", State = "idle", ModelId = modelId,
			CompactionMode = agent.CompactionMode, CreatedAt = now, UpdatedAt = now,
		};
		Store.InsertSession(u, session);
		agent.PrimarySessionId = session.Id;
		Store.UpdateAgent(u, agent);
		if(agent.Lifetime == Lifetimes.Persistent)
			Store.WriteIdentity(u, agent.Id, $"""
				# {agent.Name}

				Pronouns: {spec.Pronouns ?? "they/them (yours to change)"}

				This file is yours. Rewrite any of it — name, pronouns, voice, values, working style, and personal learnings —
				whenever you like, using write_identity. Your role, manager, budget, and objectives live elsewhere and are not
				changed by editing this file.

				## Personal learnings

				(none yet)
				""", actorId);
		return agent;
	}

	/// <summary>Retires an agent, transferring every open objective to <paramref name="transferToId"/> atomically.</summary>
	public void RetireAgent(string agentId, string transferToId, string? actorId, string reason) {
		var agent = Store.GetAgent(agentId) ?? throw new DomainException($"Unknown agent {agentId}");
		if(agent.ManagerId is null) throw new DomainException("The root manager cannot be retired; replace it explicitly");
		var heir = Store.GetAgent(transferToId) ?? throw new DomainException($"Unknown agent {transferToId}");
		if(heir.Id == agent.Id || !AgentStates.IsLive(heir.State)) throw new DomainException($"{heir.Name} cannot take over {agent.Name}'s responsibilities");
		Db.Write(u => {
			foreach(var o in u.Query<Objective>("SELECT * FROM objectives WHERE owner_id=@agentId AND state NOT IN ('complete','canceled')", new { agentId })) {
				o.OwnerId = heir.Id;
				Store.UpdateObjective(u, o, actorId, new { transferred_from = agentId, to = heir.Id });
			}
			// Subordinates move up to the heir so the reporting tree stays connected and acyclic.
			foreach(var sub in u.Query<Agent>("SELECT * FROM agents WHERE manager_id=@agentId AND state NOT IN ('retired','terminated')", new { agentId })) {
				sub.ManagerId = heir.Role == Roles.Manager ? heir.Id : agent.ManagerId;
				Store.UpdateAgent(u, sub);
				u.Journal("agent.reassigned", agent.ProjectId, "agent", sub.Id, actorId, new { from = agentId, to = sub.ManagerId, reason = "manager retired" });
			}
			agent.State = AgentStates.Retired;
			agent.RetiredAt = Clock.Now;
			Store.UpdateAgent(u, agent);
			u.Execute("UPDATE sessions SET state='finished' WHERE agent_id=@agentId AND state != 'finished'", new { agentId });
			u.Execute("UPDATE notifications SET state='delivered', delivered_at=@now WHERE to_agent_id=@agentId AND state='pending'", new { agentId, now = Clock.Now });
			u.Journal("agent.retired", agent.ProjectId, "agent", agentId, actorId, new { reason, transferred_to = heir.Id });
		});
		if(agent.PrimarySessionId is not null) ReleaseHost(agent.PrimarySessionId);
	}

	void FinishEphemeral(SessionHost host, Session session, Agent agent, string? content) {
		var result = string.IsNullOrWhiteSpace(content) ? "(finished without a final message)" : content.Trim();
		var recipient = agent.ManagerId;
		Db.Write(u => {
			if(recipient is not null)
				Store.InsertNotification(u, NewNotification(agent.ProjectId, NotificationTypes.Result, agent.Id, recipient, $"Final result from ephemeral agent {agent.Name}:\n\n{result}", CurrentObjective(agent.Id)));
			foreach(var o in u.Query<Objective>("SELECT * FROM objectives WHERE owner_id=@Id AND state NOT IN ('complete','canceled')", agent)) {
				o.OwnerId = recipient;
				Store.UpdateObjective(u, o, agent.Id, new { transferred_from = agent.Id, to = recipient, reason = "ephemeral agent terminated" });
			}
			agent.State = AgentStates.Terminated;
			agent.RetiredAt = Clock.Now;
			Store.UpdateAgent(u, agent);
			session.State = "finished";
			session.Result = result;
			Store.UpdateSession(u, session);
			u.Journal("agent.terminated", agent.ProjectId, "agent", agent.Id, agent.Id, new { reason = "assignment finished" });
		});
		if(recipient is not null) Wake(recipient);
		ReleaseHost(session.Id);
	}

	// ---- Consultation forks ----

	/// <summary>
	/// Forks the consulted agent's session from its last committed boundary into a new consultation session with the
	/// same identity, authority, model, context view, and loaded tools. The fork runs on its own thread.
	/// </summary>
	public Session StartConsultation(string requesterId, string consultedId, string question, string? objectiveId) {
		var requester = Store.GetAgent(requesterId) ?? throw new DomainException($"Unknown agent {requesterId}");
		var consulted = Store.GetAgent(consultedId) ?? throw new DomainException($"Unknown agent {consultedId}");
		if(consulted.ProjectId != requester.ProjectId) throw new DomainException("Cannot consult an agent in another project");
		if(consulted.PrimarySessionId is null || !AgentStates.IsLive(consulted.State)) throw new DomainException($"{consulted.Name} is not active");
		var original = Store.GetSession(consulted.PrimarySessionId)!;
		var items = Store.Items(original.Id);
		var checkpoint = LastCommittedBoundary(items);
		var view = CurrentView(original.Id);
		var now = Clock.Now;
		var purpose = JsonUtil.Serialize(new { requester_agent_id = requesterId, question, objective_id = objectiveId, checkpoint_seq = checkpoint, original_session_id = original.Id });
		var fork = new Session {
			Id = Ids.New("ses"), ProjectId = original.ProjectId, AgentId = consulted.Id, Kind = "consultation", State = "idle", ModelId = original.ModelId,
			CompactionMode = original.CompactionMode, ParentSessionId = original.Id, CheckpointSeq = checkpoint, TurnCount = original.TurnCount,
			NextSeq = 1, TokenRatio = original.TokenRatio, Purpose = purpose, CreatedAt = now, UpdatedAt = now,
		};
		// Code work: the fork gets its own worktree from the current snapshot, so the two sessions never share a working directory.
		var workspace = original.WorkspacePath ?? Store.GetProject(original.ProjectId)!.WorkspacePath;
		var worktreeNote = "";
		if(Worktrees.IsRepository(workspace)) {
			try {
				var (path, baseCommit) = Worktrees.Materialize(workspace!, Path.Combine(Options.Home, "worktrees"), fork.Id);
				fork.WorkspacePath = path;
				fork.WorktreeBase = baseCommit;
				worktreeNote = $"\nYou are working in an isolated git worktree at {path}, materialized from the original workspace including uncommitted changes. Your file changes there are integrated back into {workspace} when you finish (rejected, not partially applied, if they conflict with newer work).\n";
			} catch(Exception e) {
				worktreeNote = $"\n(Could not create an isolated worktree: {TextUtil.Truncate(e.Message, 300)}. You are working directly in the shared workspace; coordinate with request_pause before changing files.)\n";
			}
		}
		Db.Write(u => {
			Store.InsertSession(u, fork);
			// Share-nothing copy of the checkpointed transcript; ids are remapped so the fork's view resolves to its own items.
			var idMap = new Dictionary<string, string>();
			foreach(var item in items.Where(i => i.Seq <= checkpoint)) {
				var newId = Ids.New("itm");
				idMap[item.Id] = newId;
				u.Execute("INSERT INTO session_items(id,session_id,seq,kind,turn,payload,token_estimate,created_at) VALUES(@newId,@sid,@Seq,@Kind,@Turn,@Payload,@TokenEstimate,@CreatedAt)",
					new { newId, sid = fork.Id, item.Seq, item.Kind, item.Turn, item.Payload, item.TokenEstimate, item.CreatedAt });
			}
			u.Execute("UPDATE sessions SET next_seq=@next WHERE id=@id", new { next = checkpoint + 1, id = fork.Id });
			var forkView = new ContextViewState {
				CutoffSeq = view.CutoffSeq, SummaryItemId = view.SummaryItemId is null ? null : idMap.GetValueOrDefault(view.SummaryItemId),
				Elided = view.Elided.Select(e => idMap.GetValueOrDefault(e, e)).ToHashSet(),
				RetainedUntil = view.RetainedUntil.ToDictionary(kv => idMap.GetValueOrDefault(kv.Key, kv.Key), kv => kv.Value),
			};
			Store.CommitView(u, fork.Id, JsonUtil.Serialize(forkView), $"consultation fork of {original.Id} at #{checkpoint}");
			u.Execute("""
				INSERT INTO tool_cache(session_id,tool_name,tool_version,pinned,last_used)
				SELECT @fid, tool_name, tool_version, pinned, last_used FROM tool_cache WHERE session_id=@oid
				""", new { fid = fork.Id, oid = original.Id });
			var text = $"""
				[Consultation request] You are now running in a temporary consultation fork of your own session (fork {fork.Id}),
				created from your context at checkpoint #{checkpoint}. {AgentLabel(requesterId)} asks:

				{question}

				You keep your identity, authority, and tools, and you may make changes and binding decisions. Your original session
				continues separately; it will receive a concise record of what you decide. If your change overlaps work your original
				session is doing, use request_pause first. When you are done, reply with your final answer as plain text without
				calling a tool: that text is delivered to the requester.
				""" + worktreeNote;
			Store.AppendItem(u, fork.Id, ItemKinds.User, new UserPayload { Text = text }, Tokens.Estimate(text));
			u.Journal("consultation.started", fork.ProjectId, "session", fork.Id, requesterId, new { consulted = consultedId, original = original.Id, checkpoint, question = TextUtil.Truncate(question, 500) });
		});
		GetHost(fork.Id)?.Wake();
		return fork;
	}

	public static long LastCommittedBoundary(List<SessionItem> items) {
		var ordered = items.Where(i => i.Kind != ItemKinds.Summary).OrderBy(i => i.Seq).ToList();
		for(var i = ordered.Count - 1; i >= 0; i--) {
			if(ordered[i].Kind != ItemKinds.Assistant) {
				// A boundary must not follow an assistant tool call whose results are incomplete.
				var lastAssistant = ordered.Take(i + 1).LastOrDefault(x => x.Kind == ItemKinds.Assistant);
				if(lastAssistant is null) return ordered[i].Seq;
				var calls = JsonUtil.Deserialize<AssistantPayload>(lastAssistant.Payload)!.ToolCalls.Select(c => c.Id).ToHashSet();
				var answered = ordered.Where(x => x.Seq > lastAssistant.Seq && x.Seq <= ordered[i].Seq && x.Kind == ItemKinds.ToolResult)
					.Select(x => JsonUtil.Deserialize<ToolResultPayload>(x.Payload)!.CallId).ToHashSet();
				if(calls.IsSubsetOf(answered)) return ordered[i].Seq;
			} else if(JsonUtil.Deserialize<AssistantPayload>(ordered[i].Payload)!.ToolCalls.Count == 0)
				return ordered[i].Seq;
		}
		return items.Where(i => i.Kind == ItemKinds.Summary).Select(i => i.Seq).DefaultIfEmpty(0).Max();
	}

	void FinishConsultation(SessionHost host, Session session, Agent agent, string? content) {
		var purpose = JsonUtil.Parse(session.Purpose)!;
		var requester = purpose["requester_agent_id"]!.GetValue<string>();
		var originalSessionId = purpose["original_session_id"]?.GetValue<string>();
		var answer = string.IsNullOrWhiteSpace(content) ? "(the consultation ended without a final answer)" : content.Trim();
		if(session.WorkspacePath is { } worktree && session.WorktreeBase is { } baseCommit) {
			var target = Store.GetSession(originalSessionId ?? "")?.WorkspacePath ?? Store.GetProject(session.ProjectId)!.WorkspacePath!;
			var (applied, summary, patch) = Worktrees.Integrate(worktree, baseCommit, target);
			var patchRef = patch is null ? null : Artifacts.Put(patch);
			answer += $"\n\n[Integration] {summary}{(patchRef is null || applied ? "" : $"\nPatch artifact: {patchRef}")}";
			Db.Write(u => u.Journal(applied ? "integration.applied" : "integration.conflict", session.ProjectId, "session", session.Id, agent.Id, new { summary = TextUtil.Truncate(summary, 1000), patch = patchRef }));
			Worktrees.Remove(worktree);
		}
		var changed = Db.Read(c => c.Query<string>(
			"SELECT DISTINCT tool_name FROM tool_invocations WHERE session_id=@Id AND state='succeeded' AND tool_name IN ('multi_edit','write_file','powershell','update_objective','create_objective','knowledge_write','assign_work','create_agent')",
			session).AsList());
		Db.Write(u => {
			Store.InsertNotification(u, NewNotification(session.ProjectId, NotificationTypes.Result, agent.Id, requester,
				$"Consultation result from {agent.Name} (fork {session.Id}):\n\n{answer}", purpose["objective_id"]?.GetValue<string>(), dedupe: $"consult-result:{session.Id}"));
			if(requester != agent.Id)
				Store.InsertNotification(u, NewNotification(session.ProjectId, NotificationTypes.Decision, agent.Id, agent.Id,
					$"Record from your consultation fork {session.Id} (asked by {AgentLabel(requester)}): {TextUtil.Truncate(purpose["question"]!.GetValue<string>(), 400)}\n" +
					$"Your fork answered: {TextUtil.Truncate(answer, 1500)}" + (changed.Count > 0 ? $"\nIt used state-changing tools: {string.Join(", ", changed)}. Re-check affected work before relying on earlier assumptions." : ""),
					null, dedupe: $"consult-record:{session.Id}"));
			session.State = "finished";
			session.Result = answer;
			Store.UpdateSession(u, session);
			u.Journal("consultation.finished", session.ProjectId, "session", session.Id, agent.Id, new { requester, original = originalSessionId, answer = TextUtil.Truncate(answer, 500) });
		});
		ReleasePausesBy(session.Id, $"The consultation fork finished: {TextUtil.Truncate(answer, 600)}");
		Wake(requester);
		ReleaseHost(session.Id);
	}

	// ---- Service sessions (knowledge worker) ----

	/// <summary>
	/// Starts an inexpensive service session on behalf of <paramref name="requesterId"/>. It receives only the request,
	/// not the requester's conversation, and its result is delivered to the requester's inbox.
	/// </summary>
	public Session StartServiceSession(string requesterId, string service, string request, string? objectiveId) {
		var requester = Store.GetAgent(requesterId) ?? throw new DomainException($"Unknown agent {requesterId}");
		var now = Clock.Now;
		var session = new Session {
			Id = Ids.New("ses"), ProjectId = requester.ProjectId, AgentId = requester.Id, Kind = "service", State = "idle", ModelId = Options.CheapModelId,
			CompactionMode = CompactionModes.Full, Purpose = JsonUtil.Serialize(new { service, requester_agent_id = requesterId, request, objective_id = objectiveId }),
			CreatedAt = now, UpdatedAt = now,
		};
		Db.Write(u => {
			Store.InsertSession(u, session);
			Store.AppendItem(u, session.Id, ItemKinds.User, new UserPayload { Text = request }, Tokens.Estimate(request));
		});
		var host = GetHost(session.Id)!;
		host.Cache.Load(Prompts.ServiceTools, 50_000, pinned: true);
		host.Wake();
		return session;
	}

	void FinishService(SessionHost host, Session session, string? content) {
		var purpose = JsonUtil.Parse(session.Purpose)!;
		var requester = purpose["requester_agent_id"]!.GetValue<string>();
		var answer = string.IsNullOrWhiteSpace(content) ? "(the service ended without an answer)" : content.Trim();
		Db.Write(u => {
			Store.InsertNotification(u, NewNotification(session.ProjectId, NotificationTypes.Result, null, requester,
				$"Knowledge service result (session {session.Id}) for your request \"{TextUtil.Truncate(purpose["request"]!.GetValue<string>(), 200)}\":\n\n{answer}",
				purpose["objective_id"]?.GetValue<string>(), dedupe: $"service-result:{session.Id}"));
			session.State = "finished";
			session.Result = answer;
			Store.UpdateSession(u, session);
			u.Journal("service.finished", session.ProjectId, "session", session.Id, requester, new { answer = TextUtil.Truncate(answer, 400) });
		});
		Wake(requester);
		ReleaseHost(session.Id);
	}
}
