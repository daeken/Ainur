using Ainur.Core;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Runtime;
using Ainur.Core.Tools;
using Dapper;

namespace Ainur.Server;

public sealed record CreateProjectRequest(string Name, string? Description, string? WorkspacePath, decimal? BudgetDollars, string? ManagerModel, string? ManagerName, bool? NoEffectiveLimit = null, decimal? CashCeilingDollars = null);
public sealed record UpdateProjectRequest(decimal? BudgetDollars, decimal? CashCeilingDollars, bool? ClearCashCeiling, string? Description, bool? NoEffectiveLimit = null);
public sealed record SetAgentModelRequest(string? ModelId, string? ReasoningEffort);
public sealed record MessageRequest(string Text);
public sealed record DrainRequest(int? TimeoutSeconds);
public sealed record UpgradeOutcome(string AttemptId, string State, string ReleaseId, string? Detail);

/// <summary>Read-only build/runtime identity for the GET /api/v1/version endpoint.</summary>
public sealed record VersionInfo(string Release, long Generation, int Schema);

public static class Api {
	/// <summary>Release id (AINUR_RELEASE, defaulting to "dev"), runtime generation, and database schema version.</summary>
	public static VersionInfo GetVersionInfo(ServerOptions options, AinurRuntime rt) =>
		new(options.Release ?? "dev", rt.Generation, rt.Db.SchemaVersion);

	public static void Map(WebApplication app) {
		var api = app.MapGroup("/api/v1");

		api.MapGet("/version", (ServerOptions options, AinurRuntime rt) => Results.Json(GetVersionInfo(options, rt)));
		api.MapGet("/control/route-receipt", (HttpContext ctx, ServerOptions options, AinurRuntime rt) => RouteReceipt.Get(ctx, options, rt));

		api.MapGet("/health", async (AinurRuntime rt) => {
			// Readiness requires a working database and responsive session dispatchers, not merely a live process.
			var dbOk = rt.Db.Read(c => c.ExecuteScalar<int>("SELECT 1")) == 1;
			var stuck = new List<string>();
			foreach(var host in rt.LiveHosts) {
				var ping = host.Dispatcher.InvokeAsync(() => Task.FromResult(true));
				if(await Task.WhenAny(ping, Task.Delay(TimeSpan.FromSeconds(3))) != ping && !host.IsRunning) stuck.Add(host.SessionId);
			}
			// A scheduler that cannot advance runnable work must fail health checks; an idle or blocked team must not.
			var stalled = rt.Draining ? [] : rt.Db.Read(c => c.Query<string>("""
				SELECT DISTINCT n.to_agent_id FROM notifications n JOIN agents a ON a.id = n.to_agent_id
				WHERE n.state='pending' AND n.wakes=1 AND n.created_at < @cutoff AND a.state NOT IN ('paused','retired','terminated')
				""", new { cutoff = Clock.Now - 180_000 }).AsList())
				.Where(agentId => rt.Store.GetAgent(agentId)?.PrimarySessionId is { } sid && rt.ActivePause(sid) is null && !rt.LiveHosts.Any(h => h.SessionId == sid && h.IsRunning)).ToList();
			stuck.AddRange(stalled.Select(a => $"stalled:{a}"));
			var ready = dbOk && stuck.Count == 0;
			return Results.Json(new {
				ready, rt.Generation, schema = rt.Db.SchemaVersion, draining = rt.Draining, hosts = rt.LiveHosts.Count, stuck,
				in_flight = rt.Watchdog.Current,
			}, statusCode: ready ? 200 : 503);
		});

		api.MapGet("/models", (AinurRuntime rt) => rt.Store.ListModels().Select(m => new {
			m.Id, m.Provider, m.UpstreamModel, m.DisplayName, m.ContextTokens, m.InputPerMillion, m.CachedInputPerMillion, m.OutputPerMillion,
			m.PriceProvenance, m.Billing, m.Enabled, usable = m.Enabled && rt.Providers.Has(m.Provider), m.Notes,
		}));

		api.MapGet("/quotas", (AinurRuntime rt) => rt.Quotas.Status(rt.Db));

		api.MapGet("/projects", (AinurRuntime rt) => rt.Store.ListProjects().Select(p => ProjectView(rt, p)));

		api.MapPost("/projects", (AinurRuntime rt, CreateProjectRequest req) => {
			try {
				var p = rt.CreateProject(req.Name, req.Description ?? "", req.WorkspacePath, req.BudgetDollars, req.ManagerModel, req.ManagerName ?? "Manwë", req.NoEffectiveLimit, req.CashCeilingDollars);
				return Results.Json(ProjectView(rt, p));
			} catch(DomainException e) {
				return Results.BadRequest(new { error = e.Message });
			}
		});

		api.MapGet("/projects/{id}", (AinurRuntime rt, string id) => rt.Store.GetProject(id) is { } p ? Results.Json(ProjectView(rt, p)) : Results.NotFound());

		api.MapPatch("/projects/{id}", (AinurRuntime rt, string id, UpdateProjectRequest req) => {
			var p = rt.Store.GetProject(id);
			if(p is null) return Results.NotFound();
			try {
				p = rt.UpdateProjectBudget(id, req.BudgetDollars, req.NoEffectiveLimit, req.CashCeilingDollars, req.ClearCashCeiling == true, req.Description);
				// Updated limits do not implicitly resume agents paused by exhaustion.
				return Results.Json(ProjectView(rt, p));
			} catch(DomainException e) { return Results.BadRequest(new { error = e.Message }); }
		});

		api.MapGet("/projects/{id}/agents", (AinurRuntime rt, string id) => {
			var spend = rt.Ledger.ByAgent(id);
			var hosts = rt.LiveHosts.ToDictionary(h => h.SessionId);
			return rt.Store.ListAgents(id).Select(a => new {
				a.Id, a.Name, a.Title, a.Role, a.Lifetime, a.ManagerId, a.ModelId, a.ReasoningEffort, a.State, a.CompactionMode, a.PrimarySessionId, a.CreatedAt, a.RetiredAt,
				status = a.PrimarySessionId is not null && hosts.TryGetValue(a.PrimarySessionId, out var h) ? h.Status : null,
				// Consultations are temporary branches of this agent's work, not new agents.
				consultations = rt.Store.SessionsForAgent(a.Id).Where(s => s.Kind == "consultation" && s.State != "finished")
					.Select(s => new { s.Id, question = JsonUtil.Parse(s.Purpose)?["question"]?.GetValue<string>(), checkpoint = s.CheckpointSeq }),
				pause = a.PrimarySessionId is null ? null : rt.ActivePause(a.PrimarySessionId) is { } pr ? new { pr.Id, pr.Scope, pr.Reason, pr.ReleaseCondition, pr.State, requester = rt.AgentLabel(pr.RequesterAgentId) } : null,
				direct_nanos = spend.GetValueOrDefault(a.Id).Direct, delegated_nanos = spend.GetValueOrDefault(a.Id).Delegated, cash_direct_nanos = spend.GetValueOrDefault(a.Id).CashDirect,
			});
		});

		api.MapGet("/agents/{id}", (AinurRuntime rt, string id) => {
			var a = rt.Store.GetAgent(id);
			if(a is null) return Results.NotFound();
			var spend = rt.Ledger.ByAgent(a.ProjectId).GetValueOrDefault(a.Id);
			return Results.Json(new {
				agent = a, identity = rt.Store.CurrentIdentity(a.Id), identity_revisions = rt.Store.IdentityHistory(a.Id).Count,
				manager = a.ManagerId is null ? null : rt.Store.GetAgent(a.ManagerId),
				reports = rt.Store.Subordinates(a.Id), objectives = rt.Store.ObjectivesOwnedBy(a.Id),
				sessions = rt.Store.SessionsForAgent(a.Id),
				spend = new { direct_nanos = spend.Direct, delegated_nanos = spend.Delegated, cash_direct_nanos = spend.CashDirect },
			});
		});

		api.MapPatch("/agents/{id}", (AinurRuntime rt, string id, SetAgentModelRequest req) => {
			try {
				var change = rt.SetAgentModel(null, id, req.ModelId, req.ReasoningEffort);
				return Results.Json(new {
					agent_id = change.Agent.Id, model_id = change.Agent.ModelId, reasoning_effort = change.Agent.ReasoningEffort,
					previous_model_id = change.PreviousModelId, previous_reasoning_effort = change.PreviousReasoningEffort,
					sessions_retargeted = change.SessionsRetargeted,
					// The model of a turn is read from the session at every step (SessionHost.cs:135), so retargeting the
					// agent's live primary sessions makes the change apply at their next model step; no restart is needed.
				});
			} catch(DomainException e) {
				return Results.BadRequest(new { error = e.Message });
			}
		});

		api.MapPost("/agents/{id}/pause", (AinurRuntime rt, string id) => { rt.PauseAgent(id, null, "paused by the user"); return Results.Ok(); });
		api.MapPost("/agents/{id}/resume", (AinurRuntime rt, string id) => { rt.ResumeAgent(id, null); return Results.Ok(); });

		api.MapGet("/projects/{id}/objectives", (AinurRuntime rt, string id) => {
			var objectives = rt.Store.ListObjectives(id);
			var direct = rt.Store.CostEvents(id).Where(e => e.ObjectiveId is not null).GroupBy(e => e.ObjectiveId!).ToDictionary(g => g.Key, g => g.Sum(e => e.EffectiveNanos));
			var children = objectives.ToLookup(o => o.ParentId);
			long Total(string oid) => direct.GetValueOrDefault(oid) + children[oid].Sum(c => Total(c.Id));
			return new {
				objectives,
				dependencies = rt.Store.ListDependencies(id).Select(d => new { objective_id = d.ObjectiveId, depends_on_id = d.DependsOnId }),
				spend = objectives.ToDictionary(o => o.Id, o => new { direct_nanos = direct.GetValueOrDefault(o.Id), total_nanos = Total(o.Id) }),
			};
		});

		api.MapGet("/projects/{id}/conversation", (AinurRuntime rt, string id) => rt.Store.Conversation(id));
		api.MapPost("/projects/{id}/conversation", (AinurRuntime rt, string id, MessageRequest req) => {
			if(string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Empty message" });
			try {
				return Results.Json(rt.PostUserMessage(id, req.Text.Trim()));
			} catch(DomainException e) {
				return Results.BadRequest(new { error = e.Message });
			}
		});

		api.MapGet("/projects/{id}/events", (AinurRuntime rt, string id, long? after, int? limit) => rt.Store.Events(id, after ?? 0, Math.Clamp(limit ?? 300, 1, 2000)));
		api.MapGet("/projects/{id}/notifications", (AinurRuntime rt, string id) => rt.Store.ListNotifications(id));
		api.MapGet("/projects/{id}/knowledge", (AinurRuntime rt, string id) => rt.Db.Read(c => c.Query<KnowledgeRevision>(
			"SELECT * FROM knowledge_revisions r WHERE project_id=@id AND revision=(SELECT MAX(revision) FROM knowledge_revisions WHERE project_id=r.project_id AND doc_key=r.doc_key) ORDER BY doc_key", new { id }).AsList()));

		api.MapGet("/projects/{id}/costs", (AinurRuntime rt, string id) => {
			var events = rt.Store.CostEvents(id);
			return new {
				summary = rt.Ledger.Summary(id),
				by_agent = rt.Ledger.ByAgent(id).Select(kv => new { agent_id = kv.Key, direct_nanos = kv.Value.Direct, delegated_nanos = kv.Value.Delegated, cash_direct_nanos = kv.Value.CashDirect }),
				by_category = events.GroupBy(e => e.Category).Select(g => new { category = g.Key, effective_nanos = g.Sum(e => e.EffectiveNanos), cash_nanos = g.Sum(e => e.CashNanos ?? 0), unknown_cash = g.Count(e => e.CashNanos is null), count = g.Count() }),
				recent = events.TakeLast(100).Reverse(),
			};
		});

		api.MapGet("/sessions/{id}/items", (AinurRuntime rt, string id, long? after) => rt.Store.Items(id, after ?? 0).Select(i => new {
			i.Id, i.Seq, i.Kind, i.Turn, i.TokenEstimate, i.CreatedAt, payload = JsonUtil.Parse(i.Payload),
		}));

		api.MapGet("/sessions/{id}/context", (AinurRuntime rt, string id) => {
			var s = rt.Store.GetSession(id);
			if(s is null) return Results.NotFound();
			var agent = rt.Store.GetAgent(s.AgentId)!;
			var view = rt.CurrentView(id);
			var policy = rt.PolicyFor(agent, s);
			var model = rt.Store.GetModel(s.ModelId);
			var cache = new ToolCache(rt.Store, rt.Tools, id);
			var tools = cache.Tools();
			var host = rt.LiveHosts.FirstOrDefault(h => h.SessionId == id);
			BuiltContext? built = host is null ? null : rt.BuildContext(host, policy);
			var items = rt.Store.Items(id);
			var compactions = rt.Db.Read(c => c.Query("SELECT id, mode, from_seq, through_seq, source_tokens, summary_tokens, state, error, created_at FROM compactions WHERE session_id=@id ORDER BY created_at DESC LIMIT 20", new { id }).AsList());
			var elided = items.Where(i => i.Kind == ItemKinds.ToolResult && i.Seq > view.CutoffSeq && ContextBuilder.IsElided(i, view, s.TurnCount, policy))
				.Select(i => JsonUtil.Deserialize<ToolResultPayload>(i.Payload)!).Select(p => new { p.InvocationId, p.ToolName, p.Chars, p.Description, @explicit = false }).ToList();
			return Results.Json(new {
				session = s, compaction_mode = s.CompactionMode, view, policy,
				usable_budget = model is null ? 0 : policy.UsableBudget(model),
				estimated_tokens = built?.EstimatedTokens, conversation_tokens = built?.ConversationTokens,
				tool_tokens = tools.Sum(Schema.TokenEstimate), tool_budget = policy.ToolTokenBudget,
				loaded_tools = cache.Entries().Select(e => new { e.Name, e.Version, e.Pinned, e.LastUsed, tokens = rt.Tools.Get(e.Name) is { } t ? Schema.TokenEstimate(t) : 0 }),
				compactions, elided, objects = host?.Objects.List().Select(o => new { o.Handle, o.TypeName, o.Summary }),
				status = host?.Status,
			});
		});

		api.MapGet("/invocations/{id}", (AinurRuntime rt, string id) => {
			var inv = rt.Store.GetInvocation(id);
			if(inv is null) return Results.NotFound();
			return Results.Json(new { invocation = inv, result = inv.ResultArtifact is null ? null : TextUtil.Truncate(rt.Artifacts.GetText(inv.ResultArtifact), 200_000) });
		});

		api.MapGet("/events/stream", async (HttpContext ctx, EventHub hub, string? project) => {
			ctx.Response.Headers.ContentType = "text/event-stream";
			ctx.Response.Headers.CacheControl = "no-cache";
			var reader = hub.Subscribe(ctx.RequestAborted);
			await ctx.Response.WriteAsync(": connected\n\n", ctx.RequestAborted);
			await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
			try {
				await foreach(var msg in reader.ReadAllAsync(ctx.RequestAborted)) {
					if(project is not null && msg.StartsWith("event: journal") && !msg.Contains($"\"project_id\":\"{project}\"") && msg.Contains("\"project_id\":\"prj_")) continue;
					await ctx.Response.WriteAsync(msg, ctx.RequestAborted);
					await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
				}
			} catch(OperationCanceledException) { }
		});

		api.MapPost("/control/drain", async (AinurRuntime rt, DrainRequest? req) => {
			var drained = await rt.DrainAsync(TimeSpan.FromSeconds(req?.TimeoutSeconds ?? 60));
			return Results.Json(new { drained, running = rt.LiveHosts.Where(h => h.IsRunning).Select(h => h.SessionId) });
		});
		api.MapPost("/control/undrain", (AinurRuntime rt) => { rt.Undrain(); return Results.Ok(); });
		api.MapPost("/control/upgrade-outcome", (AinurRuntime rt, UpgradeOutcome outcome) => {
			rt.ReportUpgradeOutcome(outcome.AttemptId, outcome.State, outcome.ReleaseId, outcome.Detail ?? "");
			return Results.Ok();
		});
		api.MapPost("/control/stop", (IHostApplicationLifetime life) => { life.StopApplication(); return Results.Ok(); });
	}

	static object ProjectView(AinurRuntime rt, Project p) => new {
		p.Id, p.Name, p.Description, p.WorkspacePath, p.RootAgentId, p.RootObjectiveId, p.State, p.EffectiveBudgetNanos, p.CashCeilingNanos, p.CreatedAt,
		no_effective_limit = p.EffectiveBudgetNanos == 0,
		effective_limit_nanos = p.EffectiveBudgetNanos == 0 ? (long?) null : p.EffectiveBudgetNanos,
		costs = rt.Ledger.Summary(p.Id),
		agents = rt.Store.ListAgents(p.Id).Count(a => AgentStates.IsLive(a.State)),
	};
}
