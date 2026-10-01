using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Ainur.Core.Model;
using Ainur.Releasing;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ainur.Core.Tools;

public sealed class BuildReleaseTool : BuiltinTool {
	public override string Name => "build_release";
	public override string Description => "Build an immutable Ainur release (web UI + published runtime) from a source checkout (default: the project workspace) into the release store. Returns the release id. The active installation is unchanged.";
	public override TimeSpan Timeout => TimeSpan.FromMinutes(20);
	public override IReadOnlyList<string> Tags => ["release", "build", "upgrade", "publish", "self", "deploy"];
	public override JsonObject InputSchema => Schema.Object(("source", Schema.String("Path to an Ainur source checkout (default: workspace)."), false), ("notes", Schema.String("What changed."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var source = ctx.ResolvePath(OptStr(args, "source") ?? ".");
		if(!File.Exists(Path.Combine(source, "src", "Ainur.Server", "Ainur.Server.csproj"))) throw new ToolException($"{source} is not an Ainur source checkout");
		var log = new StringWriter();
		var releases = new Releases(ctx.Runtime.Options.Home);
		string id;
		try {
			id = await releases.BuildAsync(source, log);
		} catch(Exception e) {
			return ToolResult.Error($"Build failed: {e.Message}\n{TextUtil.Preview(log.ToString(), 6000)}");
		}
		var meta = File.ReadAllText(Path.Combine(releases.PathFor(id), "release.json"));
		var revision = JsonNode.Parse(meta)?["revision"]?.GetValue<string>();
		ctx.Runtime.Db.Write(u => {
			u.Execute("INSERT INTO releases(id,path,source_revision,state,notes,created_at) VALUES(@id,@path,@revision,'built',@notes,@now)",
				new { id, path = releases.PathFor(id), revision, notes = OptStr(args, "notes") ?? "", now = Clock.Now });
			u.Journal("release.built", ctx.Project.Id, "release", id, ctx.Agent.Id, new { revision, source });
		});
		return ToolResult.Ok($"Built release {id} at {releases.PathFor(id)} from {source} (revision {revision}). Validate it with validate_release before activation.", description: $"release {id}");
	}
}

public sealed class ValidateReleaseTool : BuiltinTool {
	public override string Name => "validate_release";
	public override string Description => """
		Validate a built release against disposable state: copy the live database to a scratch home, start the candidate on it
		(no agent work auto-starts), check readiness and migrations, run a representative manager workflow in a scratch project
		(one cheap model request), then confirm the currently running release can still read the state the candidate wrote.
		Records evidence and marks the release validated or failed. Live state is never modified.
		""";
	public override TimeSpan Timeout => TimeSpan.FromMinutes(15);
	public override IReadOnlyList<string> Tags => ["release", "validate", "verify", "upgrade", "test"];
	public override JsonObject InputSchema => Schema.Object(
		("release_id", Schema.String("Release id from build_release."), true),
		("skip_model_workflow", Schema.Boolean("Skip the model-backed workflow step (default false)."), false));

	public override async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var rt = ctx.Runtime;
		var id = Str(args, "release_id");
		var releases = new Releases(rt.Options.Home);
		var dir = releases.PathFor(id);
		if(!File.Exists(Path.Combine(dir, "Ainur.Server.dll"))) throw new ToolException($"Release {id} not found at {dir}");
		var scratch = Path.Combine(Path.GetTempPath(), $"ainur-validate-{id}-{Guid.NewGuid():N}"[..60]);
		Directory.CreateDirectory(scratch);
		var evidence = new StringBuilder();
		var ok = true;
		void Check(bool condition, string what) {
			evidence.Append(condition ? "PASS " : "FAIL ").Append(what).Append('\n');
			ok &= condition;
		}
		try {
			// Disposable copy of live state via the SQLite online backup API.
			using(var src = new SqliteConnection($"Data Source={rt.Db.Path}")) {
				src.Open();
				using var dst = new SqliteConnection($"Data Source={Path.Combine(scratch, "ainur.db")}");
				dst.Open();
				src.BackupDatabase(dst);
			}
			var liveSchema = rt.Db.SchemaVersion;
			long candidateCash = 0, candidateEffective = 0;
			await using(var candidate = await Instance.StartAsync(Path.Combine(dir, "Ainur.Server.dll"), scratch, ctx.CancellationToken)) {
				Check(candidate.Ready, $"candidate {id} became ready on port {candidate.Port}");
				if(candidate.Ready) {
					var health = await candidate.Http.GetFromJsonAsync<JsonNode>("api/v1/health", ctx.CancellationToken);
					var schema = health?["schema"]?.GetValue<int>() ?? 0;
					Check(schema >= liveSchema, $"candidate schema {schema} >= live schema {liveSchema}");
					var projects = await candidate.Http.GetFromJsonAsync<JsonArray>("api/v1/projects", ctx.CancellationToken);
					Check(projects?.Count == rt.Store.ListProjects().Count, $"candidate reads all {rt.Store.ListProjects().Count} existing projects");
					var create = await candidate.Http.PostAsJsonAsync("api/v1/projects", new { name = "validation scratch", description = "Disposable validation project", workspace_path = Path.Combine(scratch, "ws"), budget_dollars = 0.05m, manager_model = rt.Options.CheapModelId }, ctx.CancellationToken);
					var project = await create.Content.ReadFromJsonAsync<JsonNode>(ctx.CancellationToken);
					var pid = project?["id"]?.GetValue<string>();
					Check(create.IsSuccessStatusCode && pid is not null, "candidate creates a scratch project with a root manager");
					if(pid is not null && !(OptBool(args, "skip_model_workflow") ?? false)) {
						await candidate.Http.PostAsJsonAsync($"api/v1/projects/{pid}/conversation", new { text = "Validation check: reply with exactly the word READY and nothing else. Do not use tools." }, ctx.CancellationToken);
						var replied = false;
						for(var i = 0; i < 120 && !replied; i++) {
							await Task.Delay(1000, ctx.CancellationToken);
							var conv = await candidate.Http.GetFromJsonAsync<JsonArray>($"api/v1/projects/{pid}/conversation", ctx.CancellationToken);
							replied = conv?.Any(c => c?["author"]?.GetValue<string>() == "manager" && c["body"]!.GetValue<string>().Contains("READY", StringComparison.OrdinalIgnoreCase)) == true;
						}
						Check(replied, "representative manager workflow completed a model turn in the scratch project");
						var costs = await candidate.Http.GetFromJsonAsync<JsonNode>($"api/v1/projects/{pid}/costs", ctx.CancellationToken);
						candidateCash = costs?["summary"]?["cash_known_nanos"]?.GetValue<long>() ?? 0;
						candidateEffective = costs?["summary"]?["effective_nanos"]?.GetValue<long>() ?? 0;
					}
				} else
					evidence.Append(TextUtil.Preview(candidate.Output, 4000)).Append('\n');
			}
			// Backward compatibility: the currently running build must read state the candidate wrote.
			var current = Path.Combine(AppContext.BaseDirectory, "Ainur.Server.dll");
			if(File.Exists(current)) {
				await using var previous = await Instance.StartAsync(current, scratch, ctx.CancellationToken);
				Check(previous.Ready, "current release starts on state written by the candidate");
				if(previous.Ready) {
					var projects = await previous.Http.GetFromJsonAsync<JsonArray>("api/v1/projects", ctx.CancellationToken);
					Check(projects?.Any(p => p?["name"]?.GetValue<string>() == "validation scratch") == true, "current release reads the candidate's scratch project");
				}
			} else
				evidence.Append("SKIP backward-compatibility check: current runtime binary not found\n");
			// The validation's model spend is real; charge it to the requesting work.
			if(candidateEffective > 0)
				rt.Db.Write(u => rt.Store.InsertCost(u, new CostEvent {
					Id = Ids.New("cst"), ProjectId = ctx.Project.Id, AgentId = ctx.Agent.Id, SessionId = ctx.Session.Id, ObjectiveId = rt.CurrentObjective(ctx.Agent.Id),
					Category = "validation", CashNanos = candidateCash, CashBasis = "usage_priced", EffectiveNanos = candidateEffective, Valuation = $"release validation {id}", CreatedAt = Clock.Now,
				}));
		} catch(Exception e) when(e is not OperationCanceledException) {
			Check(false, $"validation error: {e.Message}");
		} finally {
			try { Directory.Delete(scratch, true); } catch { }
		}
		var state = ok ? "validated" : "failed";
		rt.Db.Write(u => {
			u.Execute("UPDATE releases SET state=@state, notes=notes || @evidence WHERE id=@id", new { state, evidence = $"\n[validation {DateTime.UtcNow:u}]\n{evidence}", id });
			u.Journal($"release.{state}", ctx.Project.Id, "release", id, ctx.Agent.Id, new { evidence = evidence.ToString() });
		});
		return new ToolResult { Text = $"Release {id} {state.ToUpperInvariant()}.\n{evidence}", IsError = !ok, Description = $"validate {id}: {state}" };
	}

	/// <summary>A runtime process on a disposable home and a free loopback port.</summary>
	sealed class Instance : IAsyncDisposable {
		public required Process Process { get; init; }
		public required int Port { get; init; }
		public required HttpClient Http { get; init; }
		public bool Ready { get; private set; }
		readonly StringBuilder OutputBuffer = new();
		public string Output { get { lock(OutputBuffer) return OutputBuffer.ToString(); } }

		public static async Task<Instance> StartAsync(string dll, string home, CancellationToken ct) {
			var listener = new TcpListener(IPAddress.Loopback, 0);
			listener.Start();
			var port = ((IPEndPoint) listener.LocalEndpoint).Port;
			listener.Stop();
			var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(dll)! };
			psi.ArgumentList.Add(dll);
			psi.ArgumentList.Add("--home"); psi.ArgumentList.Add(home);
			psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
			psi.Environment["AINUR_VALIDATION"] = "1";
			psi.Environment.Remove("AINUR_SUPERVISED");
			var instance = new Instance {
				Process = Process.Start(psi)!, Port = port,
				Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30), DefaultRequestHeaders = { { "X-Ainur", "1" } } },
			};
			instance.Process.OutputDataReceived += (_, e) => { lock(instance.OutputBuffer) instance.OutputBuffer.AppendLine(e.Data); };
			instance.Process.ErrorDataReceived += (_, e) => { lock(instance.OutputBuffer) instance.OutputBuffer.AppendLine(e.Data); };
			instance.Process.BeginOutputReadLine();
			instance.Process.BeginErrorReadLine();
			for(var i = 0; i < 120 && !instance.Process.HasExited; i++) {
				try {
					if((await instance.Http.GetAsync("api/v1/health", ct)).IsSuccessStatusCode) {
						instance.Ready = true;
						break;
					}
				} catch(HttpRequestException) { }
				await Task.Delay(500, ct);
			}
			return instance;
		}

		public async ValueTask DisposeAsync() {
			try { await Http.PostAsync("api/v1/control/stop", null); } catch { }
			if(!Process.WaitForExit(20_000)) try { Process.Kill(true); } catch { }
			Http.Dispose();
			Process.Dispose();
		}
	}
}

public sealed class ActivateReleaseTool : BuiltinTool {
	public override string Name => "activate_release";
	public override string Description => """
		Managers: autonomously upgrade Ainur to a validated release. The supervisor drains all sessions to safe checkpoints,
		stops this runtime, starts the candidate, checks readiness, and monitors a probation period, rolling back automatically
		on failure. Your session resumes on the new runtime after the restart. Requires running under the supervisor.
		""";
	public override IReadOnlyList<string> Tags => ["release", "activate", "upgrade", "deploy", "self"];
	public override JsonObject InputSchema => Schema.Object(("release_id", Schema.String("Validated release id."), true), ("notes", Schema.String("Why."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var id = Str(args, "release_id");
		var state = ctx.Runtime.Db.Read(c => c.ExecuteScalar<string?>("SELECT state FROM releases WHERE id=@id", new { id }));
		if(state != "validated") throw new ToolException($"Release {id} is {state ?? "unknown"}; only validated releases can be activated");
		if(Environment.GetEnvironmentVariable("AINUR_SUPERVISED") != "1")
			throw new ToolException("This runtime is not running under the supervisor, so it cannot be upgraded autonomously. Start Ainur with `ainur-supervisor run`.");
		var request = new UpgradeRequest { ReleaseId = id, RequestedBy = ctx.Agent.Id, Notes = OptStr(args, "notes") };
		UpgradeRequest.Write(ctx.Runtime.Options.Home, request);
		ctx.Runtime.Db.Write(u => {
			u.Execute("INSERT INTO upgrade_attempts(id,release_id,previous_release_id,state,detail,created_at,updated_at) VALUES(@AttemptId,@ReleaseId,NULL,'requested',@Notes,@now,@now) ON CONFLICT(id) DO NOTHING",
				new { request.AttemptId, request.ReleaseId, Notes = request.Notes ?? "", now = Clock.Now });
			u.Journal("upgrade.requested", ctx.Project.Id, "upgrade_attempt", request.AttemptId, ctx.Agent.Id, new { release = id, request.Notes });
		});
		return Task.FromResult(new ToolResult {
			Text = $"Upgrade {request.AttemptId} to {id} requested. The supervisor will drain and restart the runtime; this session resumes afterwards. End your turn now.",
			EndsTurn = true, Description = $"activate {id}",
		});
	}
}

public sealed class ReleaseStatusTool : BuiltinTool {
	public override string Name => "release_status";
	public override string Description => "Show built releases with validation evidence, the active and previous release, and recent upgrade attempts.";
	public override IReadOnlyList<string> Tags => ["release", "status", "upgrade", "rollback"];
	public override JsonObject InputSchema => Schema.Object();

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var releases = new Releases(ctx.Runtime.Options.Home);
		var state = releases.LoadState();
		var sb = new StringBuilder($"Active: {state.Active ?? "(dev build, unsupervised)"}; previous: {state.Previous ?? "none"}; failed: {string.Join(", ", state.Failed)}\nRunning release: {Environment.GetEnvironmentVariable("AINUR_RELEASE") ?? "dev"}\n\nReleases:\n");
		foreach(var r in ctx.Runtime.Db.Read(c => c.Query<(string Id, string State, string Notes, long CreatedAt)>("SELECT id, state, notes, created_at FROM releases ORDER BY created_at DESC LIMIT 10").ToList()))
			sb.Append($"- {r.Id} [{r.State}] {TextUtil.Truncate(r.Notes.Trim(), 800)}\n");
		sb.Append("\nUpgrade attempts:\n");
		foreach(var a in ctx.Runtime.Db.Read(c => c.Query<(string Id, string ReleaseId, string State, string Detail)>("SELECT id, release_id, state, detail FROM upgrade_attempts ORDER BY updated_at DESC LIMIT 10").ToList()))
			sb.Append($"- {a.Id} → {a.ReleaseId}: {a.State} {a.Detail}\n");
		return Task.FromResult(ToolResult.Ok(sb.ToString(), description: "release_status"));
	}
}
