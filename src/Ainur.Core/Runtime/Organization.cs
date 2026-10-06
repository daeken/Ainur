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
	public bool CanUseEngineeringModel(ModelInfo? model) => model is { Enabled: true } && Providers.Has(model.Provider) &&
		(model.Billing == "subscription" || Options.AllowApiModelsForEngineering && model.Billing == "api" && model.Provider != "openai");

	/// <summary>The supported API defaults to engineering; internal callers can explicitly opt in to it while legacy direct callers retain single-manager behavior.</summary>
	public Project CreateProject(string name, string description, string? workspacePath, decimal? budgetDollars = null, string? managerModelId = null, string managerName = "Manwë",
		bool? noEffectiveLimit = null, decimal? cashCeilingDollars = null, string organization = "single") {
		using var admission = Maintenance.Admit("project");
		if(organization is not ("engineering" or "single")) throw new DomainException("Organization must be 'engineering' or 'single'");
		if(organization == "engineering" && string.IsNullOrWhiteSpace(workspacePath))
			throw new DomainException("Choose an existing workspace directory for the new project");
		if(organization == "engineering" && !string.IsNullOrWhiteSpace(managerModelId) && !CanUseEngineeringModel(Store.GetModel(managerModelId)))
			throw new DomainException($"Engineering model '{managerModelId}' is unavailable or excluded by the configured billing policy");
		var limits = new Project();
		ProjectBudgets.Apply(limits, budgetDollars ?? (noEffectiveLimit == true ? null : Options.DefaultBudgetDollars), noEffectiveLimit, cashCeilingDollars);
		if(workspacePath is not null) {
			workspacePath = Path.GetFullPath(workspacePath.StartsWith("~/") ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), workspacePath[2..]) : workspacePath);
			if(organization == "engineering" && !Directory.Exists(workspacePath))
				throw new DomainException($"Workspace directory does not exist: {workspacePath}. Create or select it before setting up the project");
			if(organization == "single") Directory.CreateDirectory(workspacePath);
			if(organization == "engineering") {
				// Compare resolved directories, not lexical names: a symlink (including an ancestor)
				// can otherwise make two projects operate on the same repository.
				var canonical = CanonicalWorkspace(workspacePath);
				if(Store.ListProjects().Any(p => p.WorkspacePath is { } existing &&
					(WorkspaceContains(CanonicalWorkspace(existing), canonical) || WorkspaceContains(canonical, CanonicalWorkspace(existing)))))
					throw new DomainException("Workspace overlaps another Ainur project; choose a separate repository directory");
				workspacePath = canonical;
			}
		}
		var now = Clock.Now;
		var project = new Project {
			Id = Ids.New("prj"), Name = name, Description = description, WorkspacePath = workspacePath, State = "active",
			EffectiveBudgetNanos = limits.EffectiveBudgetNanos, CashCeilingNanos = limits.CashCeilingNanos, CreatedAt = now, UpdatedAt = now,
		};
		Db.Write(u => {
			Store.InsertProject(u, project);
			var model = managerModelId ?? (organization == "engineering" ? Options.EngineeringModelId : Options.ManagerModelId);
			if(organization == "engineering" && !CanUseEngineeringModel(u.Single<ModelInfo>("SELECT * FROM models WHERE id=@model", new { model })))
				throw new DomainException($"Engineering model '{model}' is unavailable or excluded by the configured billing policy");
			var root = InsertAgent(u, project.Id, new NewAgent {
				Name = managerName, Title = organization == "engineering" ? "Project lead and user liaison" : "Root manager", Role = Roles.Manager, ModelId = model, ReasoningEffort = organization == "engineering" ? "high" : null,
				Instructions = organization == "engineering"
					? "You are the user's project-facing lead. Agree scope and priorities with the user; delegate technical delivery to your delivery manager. Do not implement specialist work yourself. Require independent review before accepting changes and report verifiable evidence to the user. Work only in this project's workspace."
					: "You are the root manager of this project. The user talks only to you. Plan the work as objectives, build and organize a team, delegate implementation and verification, and keep the user informed of outcomes, direction changes, blockers, and funding problems.",
			}, null);
			var objective = new Objective {
				Id = Ids.New("obj"), ProjectId = project.Id, OwnerId = root.Id, Title = name, Description = description,
				CompletionConditions = "Defined and refined by the root manager with the user.", State = ObjectiveStates.Active, CreatedAt = now, UpdatedAt = now,
			};
			Store.InsertObjective(u, objective, null);
			project.RootAgentId = root.Id;
			project.RootObjectiveId = objective.Id;
			Store.UpdateProject(u, project);
			if(organization == "engineering") {
				var delivery = InsertAgent(u, project.Id, new NewAgent {
					Name = "Delivery manager", Title = "Technical delivery manager", Role = Roles.Manager, ManagerId = root.Id, ModelId = model, ReasoningEffort = "high",
					Instructions = "Own technical delivery for your lead. Break down assigned work and add implementation specialists as needed. Assign a separate reviewer who did not implement each change; verify commits belong to this project's workspace. Report the outcome and evidence to your lead. Do not act on another project's repository.",
				}, root.Id);
				InsertAgent(u, project.Id, new NewAgent {
					Name = "Reviewer", Title = "Independent reviewer", ManagerId = delivery.Id, ModelId = model, ReasoningEffort = "high",
					Instructions = "Independently verify changes assigned by your delivery manager. Do not approve your own implementation. Check focused tests and the resulting commit in this project's workspace; report evidence and limitations to your manager.",
				}, delivery.Id);
				Store.InsertObjective(u, new Objective {
					Id = Ids.New("obj"), ProjectId = project.Id, ParentId = objective.Id, OwnerId = delivery.Id,
					Title = "Technical delivery", Description = "Coordinate implementation and independent review for the project lead's approved work.",
					CompletionConditions = "Assigned changes are implemented, independently reviewed, and evidenced by commits in the selected workspace.",
					State = ObjectiveStates.Planned, DelegatedById = root.Id, CreatedAt = now, UpdatedAt = now,
				}, root.Id);
			}
		});
		return project;
	}

	private static string CanonicalWorkspace(string path) {
		var full = Path.GetFullPath(path);
		var root = Path.GetPathRoot(full)!;
		var current = root;
		foreach(var segment in full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)) {
			current = Path.Combine(current, segment);
			// ResolveLinkTarget(true) follows a directory link chain; resolving each component
			// also catches symlinks in parents, not just the final workspace component.
			if(new DirectoryInfo(current).ResolveLinkTarget(true) is { } resolved)
				current = CanonicalWorkspace(resolved.FullName);
		}
		return current;
	}

	private static bool WorkspaceContains(string parent, string candidate) {
		var relative = Path.GetRelativePath(parent, candidate);
		return relative == "." || (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative));
	}

	public Agent CreateAgent(string projectId, NewAgent spec, string? actorId) {
		using var admission = Maintenance.Admit("agent");
		var agent = Db.Write(u => InsertAgent(u, projectId, spec, actorId));
		return agent;
	}

	/// <summary>
	/// Reasoning-effort values the platform accepts. Policy 2026-10-01: 'high' is the default and 'medium' is the
	/// enforced floor — nothing below medium may be requested at any authoritative agent control (create_agent,
	/// set_agent_model, PATCH /agents/{id}). Mirrors the tool schemas at Tools/OrgTools.cs.
	/// </summary>
	public static readonly string[] AllowedReasoningEfforts = ["medium", "high", "max"];

	/// <summary>The reasoning effort used when an agent is created without an explicit choice.</summary>
	public const string DefaultReasoningEffort = "high";

	/// <summary>Normalizes and validates an explicitly supplied effort; throws rather than silently clamping.</summary>
	public static string ValidateReasoningEffort(string value) {
		var effort = value.Trim().ToLowerInvariant();
		if(!AllowedReasoningEfforts.Contains(effort))
			throw new DomainException($"Reasoning effort '{value}' is not allowed: 'medium' is the platform floor and lower values ('none', 'minimal', 'low') may not be requested. Allowed: {string.Join(", ", AllowedReasoningEfforts)}");
		return effort;
	}

	/// <summary>Resolves a possibly-unspecified effort: unspecified means the platform default ('high').</summary>
	public static string ResolveReasoningEffort(string? value) => value is null ? DefaultReasoningEffort : ValidateReasoningEffort(value);

	/// <summary>
	/// The efforts each provider adapter can actually transmit, derived from the adapters themselves
	/// (Providers/ChatCompletionsProvider.cs DeepSeekProvider.MapEffort; Providers/OpenAiResponsesProvider.cs MapEffort;
	/// zai is constructed with sendEffortLevel: false at Providers/ChatCompletionsProvider.cs:183, so it only
	/// distinguishes thinking disabled/high-ish and does not transmit a level). The model catalog has no per-model
	/// effort capability column, so support is checked at provider granularity; a provider that is not listed here is
	/// assumed to accept the platform vocabulary.
	/// </summary>
	static readonly Dictionary<string, string[]> ProviderEfforts = new(StringComparer.OrdinalIgnoreCase) {
		// deepseek transmits an effort level. DeepSeekProvider.MapEffort collapses 'medium' onto its 'high' wire
		// value — an upgrade, never a downgrade below the floor; making medium distinct is a provider change.
		["deepseek"] = ["medium", "high", "max"],
		// openai passes the value through untouched.
		["openai"] = ["medium", "high", "max"],
		// zai is constructed with sendEffortLevel: false, so it only toggles thinking on/off and cannot express a
		// level at all; every permitted effort maps to thinking-enabled. Disclosed limitation, not a sub-medium downshift.
		["zai"] = ["medium", "high", "max"],
	};

	public sealed record AgentModelChange {
		public required Agent Agent { get; init; }
		public string? PreviousModelId { get; init; }
		public string? PreviousReasoningEffort { get; init; }
		public IReadOnlyList<string> SessionsRetargeted { get; init; } = [];
	}

	/// <summary>
	/// Changes an agent's model and/or reasoning effort. A null <paramref name="actorId"/> is the operator surface
	/// (the REST API); otherwise the actor must be a manager changing itself or an agent in its own subtree.
	///
	/// Semantics for a running agent (Runtime/SessionHost.cs): the model of a turn comes from
	/// <c>sessions.model_id</c> (SessionHost.cs:135, read through the live Session property at SessionHost.cs:133)
	/// and the effort from <c>agents.reasoning_effort</c> (SessionHost.cs:143), both re-read from the database at
	/// every model step. An effort change therefore takes effect at the target's next model step. A model change
	/// takes effect at the next model step as well, because this method retargets the agent's live primary session
	/// rows along with the agent row; no new session and no runtime restart is required. Service sessions keep their
	/// cheap model by design (Organization.cs:368, Kind = "service") and in-flight consultation forks keep the model they forked with.
	/// </summary>
	public AgentModelChange SetAgentModel(string? actorId, string agentId, string? modelId, string? reasoningEffort) {
		if(modelId is null && reasoningEffort is null) throw new DomainException("Nothing to change: provide model and/or reasoning_effort");
		var agent = Store.GetAgent(agentId) ?? throw new DomainException($"Unknown agent '{agentId}'");
		if(actorId is not null) {
			var actor = Store.GetAgent(actorId) ?? throw new DomainException($"Unknown agent '{actorId}'");
			if(actor.Role != Roles.Manager) throw new DomainException($"Only a manager may change an agent's model; {actor.Name} is a {actor.Role}");
			if(actor.Id != agent.Id && !Store.IsInSubtree(actor.Id, agent.Id)) throw new DomainException($"{agent.Name} is not in {actor.Name}'s reporting subtree");
		}
		var desiredModel = agent.ModelId;
		if(modelId is not null) {
			var model = Store.GetModel(modelId) ?? throw new DomainException($"Unknown model '{modelId}'. Known models: {string.Join(", ", Store.ListModels().Select(m => m.Id))}");
			if(!model.Enabled || !Providers.Has(model.Provider))
				throw new DomainException($"Model '{modelId}' is not usable yet (disabled, or provider '{model.Provider}' has no live adapter). Usable models: {string.Join(", ", Store.ListModels().Where(m => m.Enabled && Providers.Has(m.Provider)).Select(m => m.Id))}");
			desiredModel = model.Id;
		}
		// Model-only changes may not carry a legacy sub-floor effort into a fresh provider dispatch. A caller may
		// repair such an agent by supplying medium/high/max in the same change; a legacy null defaults to high.
		var desiredEffort = reasoningEffort is null ? ResolveReasoningEffort(agent.ReasoningEffort) : ValidateReasoningEffort(reasoningEffort);
		var chosen = Store.GetModel(desiredModel) ?? throw new DomainException($"Agent {agent.Name} is on unknown model '{desiredModel}'; set a valid model first");
		if(ProviderEfforts.TryGetValue(chosen.Provider, out var supported) && !supported.Contains(desiredEffort))
			throw new DomainException($"Model '{chosen.Id}' (provider '{chosen.Provider}') does not support reasoning effort '{desiredEffort}'. Supported: {string.Join(", ", supported)}");
		var previousModel = agent.ModelId;
		var previousEffort = agent.ReasoningEffort;
		var retargeted = new List<string>();
		Db.Write(u => {
			agent.ModelId = desiredModel;
			agent.ReasoningEffort = desiredEffort;
			agent.UpdatedAt = Clock.Now;
			Store.UpdateAgent(u, agent);
			if(desiredModel != previousModel)
				foreach(var session in u.Query<Session>("SELECT * FROM sessions WHERE agent_id=@agentId AND kind='primary' AND state!='finished'", new { agentId = agent.Id })) {
					session.ModelId = desiredModel;
					session.UpdatedAt = Clock.Now;
					Store.UpdateSession(u, session);
					retargeted.Add(session.Id);
				}
			u.Journal("agent.updated", agent.ProjectId, "agent", agent.Id, actorId, new {
				model = new { from = previousModel, to = desiredModel },
				reasoning_effort = new { from = previousEffort, to = desiredEffort },
				sessions_retargeted = retargeted.Count,
			});
		});
		return new AgentModelChange { Agent = agent, PreviousModelId = previousModel, PreviousReasoningEffort = previousEffort, SessionsRetargeted = retargeted };
	}

	/// <summary>Atomically updates plain-text display metadata only. Requires the exact old title so a
	/// concurrent edit is not overwritten; never decodes HTML entities or changes identity/sessions.</summary>
	public Agent SetAgentTitle(string? actorId, string agentId, string? title, string? expectedTitle) {
		if(title is null || expectedTitle is null)
			throw new DomainException("Both title and expected_title are required");
		if(string.IsNullOrWhiteSpace(title) || title.Length > 200 || title.Any(char.IsControl))
			throw new DomainException("Title must be 1-200 non-control characters");
		return Db.Write(u => {
			var agent = Store.GetAgent(u, agentId) ?? throw new DomainException($"Unknown agent '{agentId}'");
			if(actorId is not null) {
				var actor = Store.GetAgent(u, actorId) ?? throw new DomainException($"Unknown agent '{actorId}'");
				if(actor.Role != Roles.Manager) throw new DomainException($"Only a manager may change an agent's title; {actor.Name} is a {actor.Role}");
				if(actor.Id != agent.Id && !Store.IsInSubtree(actor.Id, agent.Id)) throw new DomainException($"{agent.Name} is not in {actor.Name}'s reporting subtree");
			}
			if(!string.Equals(agent.Title, expectedTitle, StringComparison.Ordinal))
				throw new DomainException("Agent title has changed; reload it before editing");
			if(string.Equals(agent.Title, title, StringComparison.Ordinal)) return agent;
			agent.Title = title;
			agent.UpdatedAt = Clock.Now;
			Store.UpdateAgent(u, agent);
			u.Journal("agent.updated", agent.ProjectId, "agent", agent.Id, actorId,
				new { title = new { from = expectedTitle, to = title } });
			return agent;
		});
	}

	Agent InsertAgent(Db.Unit u, string projectId, NewAgent spec, string? actorId) {
		var now = Clock.Now;
		var modelId = spec.ModelId ?? (spec.Role == Roles.Manager ? Options.ManagerModelId : Options.SpecialistModelId);
		var model = u.Single<ModelInfo>("SELECT * FROM models WHERE id=@modelId", new { modelId }) ?? throw new DomainException($"Unknown model '{modelId}'");
		if(!model.Enabled || !Providers.Has(model.Provider))
			throw new DomainException($"Model '{modelId}' is not usable yet (provider '{model.Provider}' has no live adapter). Usable models: {string.Join(", ", Store.ListModels().Where(m => m.Enabled && Providers.Has(m.Provider)).Select(m => m.Id))}");
		// Policy 2026-10-01: unspecified means 'high'; anything below the 'medium' floor is rejected here, before insert.
		var effort = ResolveReasoningEffort(spec.ReasoningEffort);
		if(ProviderEfforts.TryGetValue(model.Provider, out var supported) && !supported.Contains(effort))
			throw new DomainException($"Model '{model.Id}' (provider '{model.Provider}') does not support reasoning effort '{effort}'. Supported: {string.Join(", ", supported)}");
		if(spec.Role is not (Roles.Manager or Roles.Specialist)) throw new DomainException($"Unknown role '{spec.Role}'");
		if(spec.Lifetime is not (Lifetimes.Persistent or Lifetimes.Ephemeral)) throw new DomainException($"Unknown lifetime '{spec.Lifetime}'");
		var name = spec.Name.Trim();
		if(name.Length == 0) throw new DomainException("Agent name is required");
		if(u.Scalar<long>("SELECT COUNT(*) FROM agents WHERE project_id=@projectId AND lower(name)=lower(@name) AND state NOT IN ('retired','terminated')", new { projectId, name }) > 0)
			throw new DomainException($"An active agent named {name} already exists in this project");
		var agent = new Agent {
			Id = Ids.New("agt"), ProjectId = projectId, Name = name, Title = spec.Title, Role = spec.Role, Lifetime = spec.Lifetime, ManagerId = spec.ManagerId,
			ModelId = modelId, ReasoningEffort = effort, State = AgentStates.Sleeping,
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
		using var admission = Maintenance.Admit("consultation");
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
		using var admission = Maintenance.Admit("service_session");
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
		var host = GetHost(session.Id);
		if(host is not null) {
			host.Cache.Load(Prompts.ServiceTools, 50_000, pinned: true);
			host.Wake();
		}
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
