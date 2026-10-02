using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core.Accounting;
using Ainur.Core.Browser;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Ainur.Core.Tools;

namespace Ainur.Core.Runtime;

/// <summary>
/// Owns one live session: a dedicated dispatcher thread, its live-object registry, PowerShell runspace, and tool
/// cache. Runs the loop of delivering inbox items, preparing context, calling the model, and executing tools inline.
/// At most one model or tool step advances the session at a time.
/// </summary>
public sealed class SessionHost : IDisposable {
	public readonly AinurRuntime Runtime;
	public readonly string SessionId;
	public readonly string AgentId;
	public readonly Dispatcher Dispatcher;
	public readonly ObjectRegistry Objects;
	public readonly ToolCache Cache;
	PowerShellHost? Ps;
	readonly CancellationTokenSource StopCts = new();
	TaskCompletionSource? WakeSignal;
	bool WakeRequested;
	bool Running;
	public string? CurrentInvocationId { get; private set; }
	public string Status { get; private set; } = "starting";
	/// <summary>True when the loop is at a safe boundary (not inside a model or tool step).</summary>
	public volatile bool AtBoundary = true;
	public event Action<string, StreamDelta>? Delta;

	public SessionHost(AinurRuntime runtime, Session session) {
		Runtime = runtime;
		SessionId = session.Id;
		AgentId = session.AgentId;
		Objects = new ObjectRegistry(session.Id, runtime.Generation);
		Cache = new ToolCache(runtime.Store, runtime.Tools, session.Id);
		Dispatcher = new Dispatcher(session.Id);
		Dispatcher.Post(() => _ = LoopAsync());
	}

	public Session Session => Runtime.Store.GetSession(SessionId)!;
	public Agent Agent => Runtime.Store.GetAgent(AgentId)!;
	public Project Project => Runtime.Store.GetProject(Session.ProjectId)!;
	public string Workspace => Session.WorkspacePath ?? Project.WorkspacePath ?? Environment.CurrentDirectory;
	public PowerShellHost PowerShell => Ps ??= new PowerShellHost(this);

	public void Wake() => Dispatcher.Post(() => {
		WakeRequested = true;
		WakeSignal?.TrySetResult();
	});

	public List<ITool> AvailableTools() => Runtime.Tools.Available(Session.ProjectId).Where(t => Runtime.ToolAllowed(Agent, Session, t.Name)).ToList();

	async Task LoopAsync() {
		while(!StopCts.IsCancellationRequested) {
			WakeRequested = false;
			bool work;
			try {
				lock(Runtime.AdmissionGate) {
					work = !Runtime.IsPaused(SessionId) && !Runtime.Draining && HasWork();
					if(work) Running = true;
				}
			} catch(Exception e) {
				Console.Error.WriteLine($"[session {SessionId}] {e}");
				work = false;
			}
			if(!work) {
				// Idle is a safe boundary: record quiescence for any pause request targeting this session.
				if(Runtime.ActivePause(SessionId) is { State: "requested" }) Runtime.AcknowledgePause(SessionId);
				SetStatus(Runtime.ActivePause(SessionId) is not null ? "paused" : "idle");
				if(!WakeRequested) {
					WakeSignal = new TaskCompletionSource();
					await WakeSignal.Task;
					WakeSignal = null;
				}
				continue;
			}
			try {
				await RunAsync();
			} catch(BudgetExhaustedException e) {
				Runtime.HandleBudgetExhausted(this, e.Message);
			} catch(OperationCanceledException) when(StopCts.IsCancellationRequested) {
				break;
			} catch(Exception e) {
				Runtime.HandleSessionFailure(this, e);
				await Task.Delay(TimeSpan.FromSeconds(5));
			} finally {
				lock(Runtime.AdmissionGate) {
					Running = false;
					AtBoundary = true;
				}
			}
		}
		SetStatus("stopped");
	}

	public bool IsRunning => Running;

	void SetStatus(string status) {
		if(Status == status) return;
		Status = status;
		var state = status switch { "idle" or "stopped" or "paused" => "idle", _ => "running" };
		Runtime.Store.Db.Write(u => {
			u.Execute("UPDATE sessions SET state=CASE WHEN state='finished' THEN state ELSE @state END WHERE id=@SessionId", new { state, SessionId });
			if(Session.Kind == "primary") {
				var agent = Runtime.Store.GetAgent(u, AgentId)!;
				if(AgentStates.IsLive(agent.State) && agent.State != AgentStates.Paused)
					Runtime.Store.SetAgentState(u, AgentId, status is "idle" or "paused" ? AgentStates.Sleeping : AgentStates.Working);
			}
			u.Journal("session.status", Session.ProjectId, "session", SessionId, AgentId, new { status });
		});
	}

	/// <summary>Work exists when the inbox has waking items or the transcript ends with something the model has not answered.</summary>
	bool HasWork() {
		var session = Session;
		if(session.State == "finished") return false;
		var agent = Agent;
		if(!AgentStates.IsLive(agent.State) || agent.State == AgentStates.Paused) return false;
		if(session.Kind == "primary" && Runtime.Store.PendingNotifications(AgentId).Any(n => n.Wakes)) return true;
		var last = Runtime.Store.Db.Read(c => Dapper.SqlMapper.QueryFirstOrDefault<SessionItem>(c,
			"SELECT * FROM session_items WHERE session_id=@SessionId AND kind != 'summary' ORDER BY seq DESC LIMIT 1", new { SessionId }));
		return last is not null && last.Kind is ItemKinds.User or ItemKinds.Notice or ItemKinds.ToolResult;
	}

	async Task RunAsync() {
		SetStatus("working");
		DeliverInbox();
		var policy = Runtime.PolicyFor(Agent, Session);
		for(var step = 0; step < Runtime.Options.MaxStepsPerWake; step++) {
			if(StopCts.IsCancellationRequested || Runtime.Draining || Runtime.IsPaused(SessionId)) return;
			AtBoundary = false;
			var session = Session;
			var agent = Agent;
			var model = Runtime.Store.GetModel(session.ModelId) ?? throw new InvalidOperationException($"Unknown model {session.ModelId}");
			var tools = PrepareTools(agent, session, policy);
			var built = await PrepareContextAsync(session, agent, model, tools, policy);
			SetStatus("thinking");
			var result = await Runtime.Gateway.CallAsync(new ModelCall {
				ProjectId = session.ProjectId, AgentId = agent.Id, SessionId = session.Id, ObjectiveId = Runtime.CurrentObjective(agent.Id),
				SponsorAgentId = Runtime.SponsorFor(session), Purpose = "turn", Category = session.Kind == "consultation" ? "consultation" : session.Kind == "service" ? "knowledge" : "direct",
				Model = model, Messages = built.Messages, Tools = tools.Select(ToolRegistry.Spec).ToList(),
				EnableWebSearch = model.Provider == "openai" && Runtime.Options.EnableOpenAiWebSearch,
				MaxOutputTokens = Math.Min(model.MaxOutputTokens ?? 32_000, Runtime.Options.MaxOutputTokens), ReasoningEffort = agent.ReasoningEffort ?? AinurRuntime.DefaultReasoningEffort,
				EstimatedInputTokens = built.EstimatedTokens + tools.Sum(Schema.TokenEstimate), ContextRevision = built.Revision,
				OnDelta = d => Delta?.Invoke(SessionId, d),
				ToolBindings = tools.ToDictionary(t => t.Name, t => t.Version),
			}, StopCts.Token);
			var response = result.Response;
			var assistant = new AssistantPayload {
				Content = response.Content, Reasoning = response.Reasoning, ToolCalls = response.ToolCalls, FinishReason = response.FinishReason,
				ModelRequestId = result.Record.Id, ModelId = model.Id,
			};
			Runtime.Store.Db.Write(u => {
				Runtime.Store.AppendItem(u, SessionId, ItemKinds.Assistant, assistant, Tokens.Estimate(response.Content) + response.ToolCalls.Sum(c => Tokens.Estimate(c.Arguments) + 8));
				var s = Runtime.Store.GetSession(u, SessionId)!;
				s.TurnCount++;
				// Calibrate the token estimator against reported usage (smoothed).
				if(response.Usage.Reported && built.EstimatedTokens > 0) {
					var estimateWithTools = built.EstimatedTokens / s.TokenRatio + tools.Sum(Schema.TokenEstimate);
					var observed = response.Usage.InputTokens / Math.Max(1.0, estimateWithTools);
					s.TokenRatio = Math.Clamp(s.TokenRatio * 0.5 + observed * 0.5, 0.3, 3.0);
				}
				Runtime.Store.UpdateSession(u, s);
			});
			AtBoundary = true;

			if(response.ToolCalls.Count == 0) {
				Runtime.OnTurnFinished(this, response.Content);
				return;
			}

			var endTurn = false;
			var bindings = tools.ToDictionary(t => t.Name, t => t.Version);
			foreach(var call in response.ToolCalls) {
				AtBoundary = false;
				var r = await ExecuteCallAsync(call, bindings.GetValueOrDefault(call.Name));
				endTurn |= r.EndsTurn;
				AtBoundary = true;
				if(StopCts.IsCancellationRequested) return;
			}
			if(Session.State == "finished") return;
			DeliverInbox();
			if(endTurn) return;
		}
		Runtime.Store.Db.Write(u => Runtime.Store.AppendItem(u, SessionId, ItemKinds.Notice,
			new NoticePayload { Text = $"Step limit of {Runtime.Options.MaxStepsPerWake} reached for this wake; continuing." }, 20));
		Wake();
	}

	List<ITool> PrepareTools(Agent agent, Session session, ContextPolicy policy) {
		var tools = Cache.Tools().Where(t => Runtime.ToolAllowed(agent, session, t.Name)).ToList();
		if(tools.Count == 0) {
			Runtime.InitializeToolCache(this, agent, policy);
			tools = Cache.Tools().Where(t => Runtime.ToolAllowed(agent, session, t.Name)).ToList();
		}
		return tools;
	}

	async Task<BuiltContext> PrepareContextAsync(Session session, Agent agent, ModelInfo model, List<ITool> tools, ContextPolicy policy) {
		var budget = policy.UsableBudget(model);
		var toolTokens = tools.Sum(Schema.TokenEstimate);
		for(var pass = 0; ; pass++) {
			var built = Runtime.BuildContext(this, policy);
			var total = built.EstimatedTokens + toolTokens;
			if(total <= budget * policy.TriggerFraction) return built;
			if(pass >= policy.MaxRollingPasses + 1) {
				if(total <= budget) return built;
				throw new InvalidOperationException($"Context of ~{total} tokens exceeds the usable budget of {budget} even after compaction; refusing to dispatch an oversized request.");
			}
			var mode = pass >= policy.MaxRollingPasses ? CompactionModes.Full : session.CompactionMode;
			SetStatus("compacting");
			var view = Runtime.CurrentView(SessionId);
			try {
				await Runtime.Compactor.CompactAsync(session, agent, view, mode, policy, StopCts.Token);
			} catch(InvalidOperationException) when(pass > 0) {
				// Nothing left to compact; fall through to the hard budget check on the next pass.
			}
		}
	}

	public void DeliverInbox() {
		var session = Session;
		if(session.Kind != "primary") return;
		var pending = Runtime.Store.PendingNotifications(AgentId);
		if(pending.Count == 0) return;
		// Preserve each image-bearing user's message and provenance, rather than merging its pixels
		// into a mixed agent-notification inbox. Resolve only server-bound conversation references.
		var deliveries = pending.Select(n => (Notification: n, Images: Runtime.ConversationImagesForNotification(SessionId, n.Id))).ToList();
		Runtime.Store.Db.Write(u => {
			var textBatch = new List<Notification>();
			void FlushTextBatch() {
				if(textBatch.Count == 0) return;
				var text = Runtime.FormatInbox(textBatch);
				Runtime.Store.AppendItem(u, SessionId, ItemKinds.User, new UserPayload { Text = text, NotificationIds = textBatch.Select(n => n.Id).ToList() }, Tokens.Estimate(text));
				textBatch.Clear();
			}
			foreach(var (notification, images) in deliveries) {
				if(images.Count == 0) { textBatch.Add(notification); continue; }
				FlushTextBatch();
				Runtime.Store.AppendItem(u, SessionId, ItemKinds.User, new UserPayload { Text = notification.Body, NotificationIds = [notification.Id], Images = images }, Tokens.Estimate(notification.Body) + images.Count * 2048);
			}
			FlushTextBatch();
			Runtime.Store.MarkDelivered(u, pending.Select(n => n.Id));
		});
	}

	async Task<ToolResult> ExecuteCallAsync(ToolCall call, string? pinnedVersion) {
		var (result, invocation, tool) = await InvokeAsync(call.Name, call.Arguments, call.Id, null, StopCts.Token, pinnedVersion);
		RecordResult(call, invocation, tool, result);
		return result;
	}

	public void RecordResult(ToolCall call, ToolInvocation invocation, ITool? tool, ToolResult result) {
		string? handle = null;
		if(ObjectRegistry.ShouldRegister(result.Value))
			handle = Objects.Put(result.Value!, tool?.Version, result.Description).Handle;
		var payload = new ToolResultPayload {
			CallId = call.Id, ToolName = call.Name, ToolVersion = tool?.Version ?? "unknown", InvocationId = invocation.Id,
			IsError = result.IsError, Text = result.Text, Artifact = invocation.ResultArtifact, Chars = result.Text.Length,
			Description = result.Description, ValueHandle = handle, Images = result.Images.ToList(),
		};
		Runtime.Store.Db.Write(u => Runtime.Store.AppendItem(u, SessionId, ItemKinds.ToolResult, payload, Tokens.Estimate(TextUtil.Truncate(result.Text, 24_000)) + 10));
	}

	/// <summary>Invokes a tool from another tool or a PowerShell pipeline; recorded as a nested invocation.</summary>
	public async Task<ToolResult> InvokeNestedAsync(string name, JsonObject args, string? parentInvocationId, CancellationToken ct) {
		var (result, _, _) = await InvokeAsync(name, args.ToJsonString(), $"nested_{Guid.NewGuid():N}"[..20], parentInvocationId, ct);
		return result;
	}

	async Task<(ToolResult, ToolInvocation, ITool?)> InvokeAsync(string name, string arguments, string callId, string? parentId, CancellationToken ct, string? pinnedVersion = null) {
		var session = Session;
		var agent = Agent;
		// Calls resolve against the exact version declared in the request, never silently rebinding to a newer one.
		var tool = pinnedVersion is not null ? Runtime.Tools.GetVersion(pinnedVersion) : Runtime.Tools.Get(name);
		if(pinnedVersion is not null && tool is null) {
			var conflict = ToolResult.Error($"Tool version conflict: this call was issued against {pinnedVersion}, which is no longer available (current: {Runtime.Tools.Get(name)?.Version ?? "none"}). Re-check the current schema and call again if appropriate.");
			var failed = new ToolInvocation {
				Id = Ids.New("inv"), ProjectId = session.ProjectId, SessionId = SessionId, AgentId = AgentId, CallId = callId, ToolName = name, ToolVersion = pinnedVersion,
				Arguments = arguments, State = InvocationStates.Failed, Error = conflict.Text, ParentInvocationId = parentId, CreatedAt = Clock.Now, FinishedAt = Clock.Now,
			};
			Runtime.Store.Db.Write(u => Runtime.Store.InsertInvocation(u, failed));
			return (conflict, failed, null);
		}
		var invocation = new ToolInvocation {
			Id = Ids.New("inv"), ProjectId = session.ProjectId, SessionId = SessionId, AgentId = AgentId, CallId = callId, ToolName = name,
			ToolVersion = tool?.Version ?? "unknown", Arguments = arguments, State = InvocationStates.Queued, ParentInvocationId = parentId, CreatedAt = Clock.Now,
		};
		ToolResult result;
		if(tool is null || !Runtime.ToolAllowed(agent, session, name)) {
			result = ToolResult.Error(tool is null
				? $"Unknown tool '{name}'. Use find_tools to search the registry."
				: $"Tool '{name}' is not available to {agent.Name} in this session.");
			invocation.State = InvocationStates.Failed;
			invocation.Error = result.Text;
			invocation.FinishedAt = Clock.Now;
			invocation.ResultArtifact = Runtime.Artifacts.Put(result.Text);
			invocation.ResultChars = result.Text.Length;
			Runtime.Store.Db.Write(u => Runtime.Store.InsertInvocation(u, invocation));
			return (result, invocation, tool);
		}
		if(Runtime.Tools.IsQuarantined(tool.Version)) {
			result = ToolResult.Error($"Tool version {tool.Version} is quarantined after a forced restart; repair or replace it.");
			invocation.State = InvocationStates.Failed;
			invocation.Error = result.Text;
			invocation.FinishedAt = Clock.Now;
			Runtime.Store.Db.Write(u => Runtime.Store.InsertInvocation(u, invocation));
			return (result, invocation, tool);
		}

		JsonObject args;
		try {
			args = Schema.ParseArguments(arguments);
		} catch(ToolException e) {
			result = ToolResult.Error(e.Message);
			invocation.State = InvocationStates.Failed;
			invocation.Error = result.Text;
			invocation.FinishedAt = Clock.Now;
			Runtime.Store.Db.Write(u => Runtime.Store.InsertInvocation(u, invocation));
			return (result, invocation, tool);
		}
		var timeout = tool.TimeoutFor(args);
		invocation.State = InvocationStates.Running;
		invocation.StartedAt = Clock.Now;
		invocation.DeadlineAt = invocation.StartedAt + (long) timeout.TotalMilliseconds;
		// Record intent and report the deadline before entering inline tool code.
		Runtime.Store.Db.Write(u => Runtime.Store.InsertInvocation(u, invocation));
		Runtime.Watchdog.Enter(new(invocation.Id, SessionId, name, tool.Version, invocation.DeadlineAt.Value, invocation.StartedAt.Value));
		var previousInvocation = CurrentInvocationId;
		CurrentInvocationId = invocation.Id;
		using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, StopCts.Token);
		cts.CancelAfter(timeout);
		try {
			var ctx = new ToolContext {
				Runtime = Runtime, Agent = agent, Session = session, Project = Runtime.Store.GetProject(session.ProjectId)!, InvocationId = invocation.Id,
				CancellationToken = cts.Token, Host = this,
			};
			SetStatus($"tool:{name}");
			result = await tool.InvokeAsync(ctx, args);
			invocation.State = result.IsError ? InvocationStates.Failed : InvocationStates.Succeeded;
		} catch(ToolException e) {
			result = ToolResult.Error(e.Message);
			invocation.State = InvocationStates.Failed;
		} catch(OperationCanceledException) when(!StopCts.IsCancellationRequested) {
			result = ToolResult.Error(ct.IsCancellationRequested ? "Tool invocation was canceled." : $"Tool exceeded its deadline of {timeout.TotalSeconds:0}s and was canceled.");
			invocation.State = InvocationStates.Canceled;
		} catch(DomainException e) {
			result = ToolResult.Error(e.Message);
			invocation.State = InvocationStates.Failed;
		} catch(Exception e) when(e is not OperationCanceledException) {
			result = ToolResult.Error($"{e.GetType().Name}: {e.Message}");
			invocation.State = InvocationStates.Failed;
		} finally {
			Runtime.Watchdog.Exit(invocation.Id);
			CurrentInvocationId = previousInvocation;
		}
		invocation.FinishedAt = Clock.Now;
		invocation.ResultArtifact = Runtime.Artifacts.Put(result.Text);
		invocation.ResultChars = result.Text.Length;
		invocation.Error = result.IsError ? TextUtil.Truncate(result.Text, 2000) : null;
		Runtime.Store.Db.Write(u => Runtime.Store.UpdateInvocation(u, invocation));
		if(parentId is null) Cache.Touch(name);
		return (result, invocation, tool);
	}

	public void Stop() {
		StopCts.Cancel();
		Dispatcher.Post(() => WakeSignal?.TrySetResult());
	}

	public void Dispose() {
		Stop();
		// A session owns only its own browser. Do not dispose the manager: sibling sessions in the
		// same runtime may still be operating, and Runtime.Dispose closes the whole manager.
		if(BrowserManager.TryFor(Runtime, out var browsers)) {
			try { browsers!.CloseSessionAsync(SessionId).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult(); }
			catch(Exception e) { Console.Error.WriteLine($"Browser session shutdown incomplete: {e.Message}"); }
		}
		Dispatcher.Post(() => Ps?.Dispose());
		Dispatcher.Dispose();
	}
}
