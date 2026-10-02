using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

using Ainur.Releasing;

namespace Ainur.Supervisor;

public sealed class SupervisorOptions {
	public string Home { get; set; } = Environment.GetEnvironmentVariable("AINUR_HOME") is { Length: > 0 } h ? h : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ainur");
	public int Port { get; set; } = 5180;
	public string? Source { get; set; }
	public TimeSpan ReadyTimeout { get; set; } = TimeSpan.FromSeconds(90);
	public TimeSpan Probation { get; set; } = TimeSpan.FromSeconds(60);
	public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(120);
	public TimeSpan DeadlineGrace { get; set; } = TimeSpan.FromSeconds(30);

	public static SupervisorOptions Parse(string[] args) {
		var o = new SupervisorOptions();
		for(var i = 0; i < args.Length - 1; i++)
			switch(args[i]) {
				case "--home": o.Home = Path.GetFullPath(args[++i]); break;
				case "--port": o.Port = int.Parse(args[++i]); break;
				case "--source": o.Source = Path.GetFullPath(args[++i]); break;
				case "--probation-seconds": o.Probation = TimeSpan.FromSeconds(int.Parse(args[++i])); break;
				case "--ready-seconds": o.ReadyTimeout = TimeSpan.FromSeconds(int.Parse(args[++i])); break;
			}
		return o;
	}
}

/// <summary>
/// Mechanical release and recovery logic: selects the active release, holds one runtime at a time, checks readiness,
/// supervises one runtime lifetime, and performs explicitly requested drain → activate → probation upgrades.
/// Unexpected child exit, unresponsive/stuck work and failed readiness require operator recovery;
/// only an explicitly requested upgrade may gracefully stop its failed candidate and start a rollback.
/// </summary>
public sealed class Supervisor(SupervisorOptions opts, Releases releases) : IDisposable {
	readonly HttpClient Http = new() { BaseAddress = new Uri($"http://127.0.0.1:{opts.Port}/"), Timeout = TimeSpan.FromSeconds(10) };
	readonly CancellationTokenSource Cts = new();
	Process? Runtime;
	string? RunningRelease;
	bool InterventionRequired;

	// A single-use durable claim survives crashes and launchd KeepAlive restarts.
	// No automatic deletion: even an empty/partial file blocks new child spawns.
	// Operator may clear only after independently proving all prior parents/children
	// exited and the runtime home, route, and ledger are preserved.
	internal static bool TryClaimSpawnLease(string home) {
		var path = Path.Combine(home, "supervisor", "spawn-lease.json");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		try {
			using var lease = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
			var data = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
				owner_pid = Environment.ProcessId, created_utc = DateTime.UtcNow.ToString("O"),
				policy = "single use: never automatically clear after crash, timeout or shutdown",
			}));
			lease.Write(data);
			lease.Flush(flushToDisk: true);
			return true;
		} catch(IOException) when(File.Exists(path)) { return false; }
	}

	/// <summary>Ends the supervision loop; RunAsync requests graceful child stop, never forces it.</summary>
	public void RequestShutdown() {
		Log("Shutdown requested");
		Cts.Cancel();
	}

	void Log(string message) {
		var line = $"{DateTime.UtcNow:O} {message}";
		Console.WriteLine($"[supervisor] {line}");
		Directory.CreateDirectory(Path.Combine(opts.Home, "supervisor"));
		File.AppendAllText(Path.Combine(opts.Home, "supervisor", "supervisor.log"), line + "\n");
	}

	public async Task<int> RunAsync() {
		Http.DefaultRequestHeaders.Add("X-Ainur", "1");
		if(!TryClaimSpawnLease(opts.Home)) {
			Log("Existing single-use spawn lease: child start blocked; independent operator recovery required");
			try { await Task.Delay(Timeout.InfiniteTimeSpan, Cts.Token); } catch(OperationCanceledException) { }
			return 3;
		}
		var state = releases.LoadState();
		if(state.Active is null && Directory.Exists(releases.Root) && Directory.GetDirectories(releases.Root).Select(Path.GetFileName).Where(d => !state.Failed.Contains(d!)).OrderDescending().FirstOrDefault() is { } newest) {
			Log($"No active release recorded; selecting newest built release {newest}");
			state.Active = newest;
			releases.SaveState(state);
		}
		if(state.Active is null) {
			if(opts.Source is null) {
				Log("No active release and no --source to build one from.");
				return 2;
			}
			Log($"Building initial release from {opts.Source}");
			var id = await releases.BuildAsync(opts.Source, Console.Out);
			state.Active = id;
			releases.SaveState(state);
		}
		while(!Cts.IsCancellationRequested) {
			// Preserve the sole child after an unsafe stop/readiness/health/deadline outcome.
			// No upgrade, rollback or replacement is permitted until independently recovered.
			if(InterventionRequired) {
				try { await Task.Delay(2000, Cts.Token); } catch(OperationCanceledException) { }
				continue;
			}
			state = releases.LoadState();
			if(Runtime is { HasExited: true }) {
				RequireIntervention($"Runtime {RunningRelease} exited with code {Runtime.ExitCode}; no automatic restart or rollback");
				continue;
			}
			if(Runtime is null) {
				if(Cts.IsCancellationRequested) break;
				StartRuntime(state.Active!);
				if(!await WaitReadyAsync(opts.ReadyTimeout)) {
					Log($"Runtime {state.Active} did not become ready; requesting graceful stop only");
					var stopped = await StopRuntimeAsync(TimeSpan.FromSeconds(5));
					RequireIntervention(stopped
						? "runtime failed readiness and exited voluntarily; no blind retry"
						: "runtime failed readiness and did not exit after graceful stop; no replacement");
					continue;
				}
				Log($"Runtime {state.Active} ready");
			}

			await WatchDeadlinesAsync();
			if(InterventionRequired) continue;
			await HealthCheckAsync();
			if(InterventionRequired) continue;
			if(UpgradeRequest.TryTake(opts.Home) is { } req)
				await UpgradeAsync(req);
			try { await Task.Delay(2000, Cts.Token); } catch(OperationCanceledException) { }
		}
		if(!await StopRuntimeAsync(TimeSpan.FromSeconds(30))) {
			// Do not release ownership of a live child: an automatic service restart could
			// otherwise launch a second runtime against the same home and ledger.
			RequireIntervention("shutdown incomplete; retaining supervisor ownership until child exits voluntarily");
			var child = Runtime!;
			await Task.Run(() => child.WaitForExit());
			Log($"Child PID {child.Id} exited voluntarily after shutdown timeout");
		}
		return 0;
	}

	int HealthFailures;
	async Task HealthCheckAsync() {
		if(Runtime is null || Runtime.HasExited) return;
		if(await IsReadyAsync()) {
			HealthFailures = 0;
			return;
		}
		if(++HealthFailures >= 5)
			RequireIntervention("runtime failed 5 consecutive health checks; child remains alive, no restart");
	}

	/// <summary>Detect an overdue invocation, but preserve the child for controlled recovery.</summary>
	async Task WatchDeadlinesAsync() {
		var path = Path.Combine(opts.Home, "runtime", "inflight.json");
		if(Runtime is null || Runtime.HasExited || !File.Exists(path)) return;
		try {
			var doc = JsonNode.Parse(await File.ReadAllTextAsync(path)) as JsonObject
				?? throw new JsonException("inflight registry is not an object");
			var pid = doc["pid"]?.GetValue<int>()
				?? throw new JsonException("inflight registry has no PID");
			if(pid != Runtime.Id) {
				RequireIntervention("inflight registry owner PID differs from child; child preserved");
				return;
			}
			var entries = doc["in_flight"] as JsonArray
				?? throw new JsonException("inflight registry has no invocation array");
			var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
			foreach(var value in entries) {
				var entry = value as JsonObject ?? throw new JsonException("invalid inflight invocation");
				var deadline = entry["deadline_at"]?.GetValue<long>()
					?? throw new JsonException("inflight invocation has no deadline");
				if(now <= deadline + (long) opts.DeadlineGrace.TotalMilliseconds) continue;
				var invocation = entry["invocation_id"]?.GetValue<string>();
				RequireIntervention($"Invocation {invocation} ({entry["tool"]}) exceeded its deadline by {(now - deadline) / 1000}s; child preserved");
				return;
			}
		} catch(Exception e) {
			RequireIntervention($"cannot safely parse inflight registry ({e.GetType().Name}); child preserved");
		}
	}

	void RequireIntervention(string reason) {
		if(InterventionRequired) return;
		InterventionRequired = true;
		Log($"INTERVENTION REQUIRED: {reason}");
	}

	void StartRuntime(string releaseId) {
		if(InterventionRequired || Runtime is { HasExited: false })
			throw new InvalidOperationException("Cannot start another runtime while a child is alive or intervention is required");
		var dir = releases.PathFor(releaseId);
		var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = dir };
		psi.ArgumentList.Add(Path.Combine(dir, "Ainur.Server.dll"));
		psi.ArgumentList.Add("--home"); psi.ArgumentList.Add(opts.Home);
		psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(opts.Port.ToString());
		psi.ArgumentList.Add("--release"); psi.ArgumentList.Add(releaseId);
		// Pin the paid-route exclusion on EVERY spawn (initial, crash restart, candidate, rollback).
		// Never trust the loaded launchd parent's possibly stale environment.
		psi.Environment["AINUR_OPENAI_ROUTE"] = "subscription";
		psi.Environment["AINUR_SUPERVISED"] = "1";
		Runtime = Process.Start(psi)!;
		RunningRelease = releaseId;
		Log($"Started runtime {releaseId} (pid {Runtime.Id})");
	}

	async Task<bool> IsReadyAsync() {
		try {
			var r = await Http.GetAsync("api/v1/health");
			return r.IsSuccessStatusCode;
		} catch {
			return false;
		}
	}

	async Task<bool> WaitReadyAsync(TimeSpan timeout) {
		var deadline = DateTime.UtcNow + timeout;
		while(DateTime.UtcNow < deadline && !Cts.IsCancellationRequested) {
			if(Runtime is null || Runtime.HasExited) return false;
			if(await IsReadyAsync()) return true;
			await Task.Delay(500);
		}
		return false;
	}

	// Kept pure for offline stuck-child/failed-stop tests. The real child is never killed on timeout.
	internal static async Task<bool> GracefulStopAsync(TimeSpan timeout, Func<Task> requestStop, Func<TimeSpan, Task<bool>> waitExit) {
		try { await requestStop(); } catch { /* An unreachable stop endpoint does not authorize a kill. */ }
		return await waitExit(timeout);
	}

	async Task<bool> StopRuntimeAsync(TimeSpan timeout) {
		if(Runtime is null || Runtime.HasExited) return true;
		var child = Runtime;
		var exited = await GracefulStopAsync(timeout,
			async () => { await Http.PostAsync("api/v1/control/stop", null); },
			limit => Task.Run(() => child.WaitForExit(limit)));
		if(!exited) Log($"Child PID {child.Id} did not exit within {timeout}; leaving it alive");
		return exited;
	}

	async Task UpgradeAsync(UpgradeRequest req) {
		var state = releases.LoadState();
		var previous = state.Active!;
		var attempt = req.AttemptId!;
		if(state.Failed.Contains(req.ReleaseId)) {
			Log($"Refusing to activate {req.ReleaseId}: it previously failed");
			RecordAttempt(attempt, req.ReleaseId, previous, "aborted", "release previously failed; activation suppressed");
			return;
		}
		if(!Directory.Exists(releases.PathFor(req.ReleaseId))) {
			RecordAttempt(attempt, req.ReleaseId, previous, "aborted", "release directory missing");
			return;
		}
		Log($"Upgrade {attempt}: {previous} → {req.ReleaseId}");
		RecordAttempt(attempt, req.ReleaseId, previous, "draining", "");
		var drained = false;
		try {
			var r = await new HttpClient { Timeout = opts.DrainTimeout + TimeSpan.FromSeconds(10) }.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{opts.Port}/api/v1/control/drain") {
				Content = JsonContent.Create(new { timeout_seconds = (int) opts.DrainTimeout.TotalSeconds }), Headers = { { "X-Ainur", "1" } },
			});
			drained = (await r.Content.ReadFromJsonAsync<JsonNode>())?["drained"]?.GetValue<bool>() == true;
		} catch(Exception e) {
			Log($"Drain request failed: {e.Message}");
		}
		if(!drained) {
			// The drain deadline expired: abort and restore service rather than leaving the organization stopped.
			Log("Drain did not complete; aborting upgrade");
			try { await Http.PostAsync("api/v1/control/undrain", null); } catch { }
			RecordAttempt(attempt, req.ReleaseId, previous, "aborted", "drain deadline expired");
			await ReportOutcomeAsync(attempt, "aborted", req.ReleaseId, "drain deadline expired; the previous runtime kept serving");
			return;
		}
		RecordAttempt(attempt, req.ReleaseId, previous, "activating", "");
		if(!await StopRuntimeAsync(TimeSpan.FromSeconds(60))) {
			RequireIntervention("drained runtime did not exit gracefully; candidate NOT started");
			RecordAttempt(attempt, req.ReleaseId, previous, "aborted", "previous runtime did not exit; preserve drain and child; operator recovery required");
			return;
		}
		StartRuntime(req.ReleaseId);
		var ok = await WaitReadyAsync(opts.ReadyTimeout);
		if(ok) {
			RecordAttempt(attempt, req.ReleaseId, previous, "probation", "");
			var until = DateTime.UtcNow + opts.Probation;
			while(ok && DateTime.UtcNow < until) {
				await Task.Delay(1000);
				ok = Runtime is { HasExited: false } && await IsReadyAsync();
			}
		}
		if(ok) {
			state.Previous = previous;
			state.Active = req.ReleaseId;
			releases.SaveState(state);
			RecordAttempt(attempt, req.ReleaseId, previous, "succeeded", "");
			Log($"Upgrade {attempt} succeeded; active release {req.ReleaseId}");
			await ReportOutcomeAsync(attempt, "succeeded", req.ReleaseId, "");
		} else {
			Log($"Upgrade {attempt} failed readiness or probation; requesting graceful candidate stop before rollback to {previous}");
			if(!await StopRuntimeAsync(TimeSpan.FromSeconds(60))) {
				RequireIntervention("candidate failed, did not exit gracefully; rollback NOT started");
				RecordAttempt(attempt, req.ReleaseId, previous, "aborted", "candidate still alive; no rollback or replacement");
				return;
			}
			state.Failed.Add(req.ReleaseId);
			releases.SaveState(state);
			StartRuntime(previous);
			if(!await WaitReadyAsync(opts.ReadyTimeout)) {
				RequireIntervention("rollback runtime failed readiness; child preserved without replacement");
				RecordAttempt(attempt, req.ReleaseId, previous, "aborted", "rollback not ready; intervention required");
				return;
			}
			RecordAttempt(attempt, req.ReleaseId, previous, "rolled_back", "candidate failed readiness or probation");
			await ReportOutcomeAsync(attempt, "rolled_back", req.ReleaseId, "candidate failed readiness or probation");
		}
	}

	async Task ReportOutcomeAsync(string attempt, string state, string release, string detail) {
		try {
			await Http.PostAsync("api/v1/control/upgrade-outcome", JsonContent.Create(new { attempt_id = attempt, state, release_id = release, detail }));
		} catch(Exception e) {
			Log($"Could not report upgrade outcome: {e.Message}");
		}
	}

	void RecordAttempt(string id, string release, string? previous, string state, string detail) {
		try {
			using var conn = new SqliteConnection($"Data Source={Path.Combine(opts.Home, "ainur.db")};Default Timeout=30");
			conn.Open();
			using var cmd = conn.CreateCommand();
			cmd.CommandText = """
				INSERT INTO upgrade_attempts(id,release_id,previous_release_id,state,detail,created_at,updated_at) VALUES($id,$release,$previous,$state,$detail,$now,$now)
				ON CONFLICT(id) DO UPDATE SET state=$state, detail=$detail, updated_at=$now;
				INSERT INTO events(project_id,kind,entity_type,entity_id,payload,created_at) VALUES(NULL,'upgrade.' || $state,'upgrade_attempt',$id,json_object('release',$release,'previous',$previous,'detail',$detail),$now);
				""";
			cmd.Parameters.AddWithValue("$id", id);
			cmd.Parameters.AddWithValue("$release", release);
			cmd.Parameters.AddWithValue("$previous", (object?) previous ?? DBNull.Value);
			cmd.Parameters.AddWithValue("$state", state);
			cmd.Parameters.AddWithValue("$detail", detail);
			cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
			cmd.ExecuteNonQuery();
		} catch(Exception e) {
			Log($"Could not record upgrade attempt: {e.Message}");
		}
		File.AppendAllText(Path.Combine(opts.Home, "supervisor", "upgrades.log"), $"{DateTime.UtcNow:O} {id} {release} {state} {detail}\n");
	}

	public void Dispose() {
		Http.Dispose();
		Cts.Dispose();
	}
}
