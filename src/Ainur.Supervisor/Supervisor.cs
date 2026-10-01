using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

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

public sealed class UpgradeRequest {
	public string ReleaseId { get; set; } = "";
	public string? AttemptId { get; set; }
	public string? RequestedBy { get; set; }
	public string? Notes { get; set; }

	static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
	public static string PathFor(string home) => Path.Combine(home, "runtime", "upgrade-request.json");

	public static void Write(string home, UpgradeRequest r) {
		r.AttemptId ??= $"upg_{Guid.CreateVersion7():N}";
		Directory.CreateDirectory(Path.Combine(home, "runtime"));
		var tmp = PathFor(home) + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(r, Options));
		File.Move(tmp, PathFor(home), true);
	}

	public static UpgradeRequest? TryTake(string home) {
		var path = PathFor(home);
		if(!File.Exists(path)) return null;
		try {
			var r = JsonSerializer.Deserialize<UpgradeRequest>(File.ReadAllText(path), Options);
			File.Delete(path);
			return r;
		} catch {
			return null;
		}
	}
}

/// <summary>
/// Mechanical release and recovery logic: selects the active release, holds one runtime at a time, checks readiness,
/// restarts crashed or unresponsive runtimes, kills runtimes stuck past an inline tool deadline, and performs
/// drain → activate → probation upgrades with automatic rollback.
/// </summary>
public sealed class Supervisor(SupervisorOptions opts, Releases releases) : IDisposable {
	readonly HttpClient Http = new() { BaseAddress = new Uri($"http://127.0.0.1:{opts.Port}/"), Timeout = TimeSpan.FromSeconds(10) };
	readonly CancellationTokenSource Cts = new();
	Process? Runtime;
	string? RunningRelease;

	public void Shutdown() {
		if(Cts.IsCancellationRequested) return;
		Cts.Cancel();
		StopRuntimeAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
	}

	void Log(string message) {
		var line = $"{DateTime.UtcNow:O} {message}";
		Console.WriteLine($"[supervisor] {line}");
		Directory.CreateDirectory(Path.Combine(opts.Home, "supervisor"));
		File.AppendAllText(Path.Combine(opts.Home, "supervisor", "supervisor.log"), line + "\n");
	}

	public async Task<int> RunAsync() {
		Http.DefaultRequestHeaders.Add("X-Ainur", "1");
		var state = releases.LoadState();
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
		var crashTimes = new List<DateTime>();
		while(!Cts.IsCancellationRequested) {
			state = releases.LoadState();
			if(Runtime is null || Runtime.HasExited) {
				if(Runtime is { HasExited: true }) {
					Log($"Runtime {RunningRelease} exited with code {Runtime.ExitCode}");
					crashTimes.Add(DateTime.UtcNow);
					crashTimes.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(5));
					if(crashTimes.Count >= 4 && state.Previous is not null && state.Active != state.Previous) {
						Log($"Release {state.Active} crashed repeatedly; rolling back to {state.Previous}");
						state.Failed.Add(state.Active!);
						(state.Active, state.Previous) = (state.Previous, null);
						releases.SaveState(state);
						crashTimes.Clear();
					}
					await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(crashTimes.Count, 5))), Cts.Token).ContinueWith(_ => { });
				}
				if(Cts.IsCancellationRequested) break;
				StartRuntime(state.Active!);
				if(!await WaitReadyAsync(opts.ReadyTimeout)) {
					Log($"Runtime {state.Active} did not become ready");
					await StopRuntimeAsync(TimeSpan.FromSeconds(5));
					continue;
				}
				Log($"Runtime {state.Active} ready");
			}

			await WatchDeadlinesAsync();
			if(UpgradeRequest.TryTake(opts.Home) is { } req)
				await UpgradeAsync(req);
			await HealthCheckAsync();
			try { await Task.Delay(2000, Cts.Token); } catch(OperationCanceledException) { }
		}
		await StopRuntimeAsync(TimeSpan.FromSeconds(30));
		return 0;
	}

	int HealthFailures;
	async Task HealthCheckAsync() {
		if(Runtime is null || Runtime.HasExited) return;
		if(await IsReadyAsync()) {
			HealthFailures = 0;
			return;
		}
		if(++HealthFailures >= 5) {
			Log("Runtime failed 5 consecutive health checks; restarting");
			WriteRestartReason(null, "health checks failed");
			KillRuntime();
			HealthFailures = 0;
		}
	}

	/// <summary>Last-resort recovery for inline tools that ignore cancellation: terminate the whole runtime.</summary>
	async Task WatchDeadlinesAsync() {
		var path = Path.Combine(opts.Home, "runtime", "inflight.json");
		if(Runtime is null || Runtime.HasExited || !File.Exists(path)) return;
		JsonNode? doc;
		try { doc = JsonNode.Parse(await File.ReadAllTextAsync(path)); } catch { return; }
		if(doc?["pid"]?.GetValue<int>() != Runtime.Id) return;
		var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		foreach(var entry in doc["in_flight"] as JsonArray ?? []) {
			var deadline = entry!["deadline_at"]!.GetValue<long>();
			if(now <= deadline + (long) opts.DeadlineGrace.TotalMilliseconds) continue;
			var invocation = entry["invocation_id"]!.GetValue<string>();
			Log($"Invocation {invocation} ({entry["tool"]}) exceeded its deadline by {(now - deadline) / 1000}s; terminating runtime");
			WriteRestartReason(invocation, $"tool {entry["tool"]} exceeded its deadline and ignored cancellation");
			KillRuntime();
			return;
		}
	}

	void WriteRestartReason(string? invocationId, string reason) {
		var path = Path.Combine(opts.Home, "runtime", "restart-reason.json");
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, JsonSerializer.Serialize(new { invocation_id = invocationId, reason, at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }));
	}

	void StartRuntime(string releaseId) {
		var dir = releases.PathFor(releaseId);
		var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = dir };
		psi.ArgumentList.Add(Path.Combine(dir, "Ainur.Server.dll"));
		psi.ArgumentList.Add("--home"); psi.ArgumentList.Add(opts.Home);
		psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(opts.Port.ToString());
		psi.ArgumentList.Add("--release"); psi.ArgumentList.Add(releaseId);
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

	async Task StopRuntimeAsync(TimeSpan timeout) {
		if(Runtime is null || Runtime.HasExited) return;
		try { await Http.PostAsync("api/v1/control/stop", null); } catch { }
		var exited = await Task.Run(() => Runtime.WaitForExit(timeout));
		if(!exited) KillRuntime();
	}

	void KillRuntime() {
		try { Runtime?.Kill(entireProcessTree: true); Runtime?.WaitForExit(10_000); } catch { }
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
			return;
		}
		RecordAttempt(attempt, req.ReleaseId, previous, "activating", "");
		await StopRuntimeAsync(TimeSpan.FromSeconds(60));
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
		} else {
			Log($"Upgrade {attempt} failed readiness or probation; rolling back to {previous}");
			KillRuntime();
			state.Failed.Add(req.ReleaseId);
			releases.SaveState(state);
			StartRuntime(previous);
			await WaitReadyAsync(opts.ReadyTimeout);
			RecordAttempt(attempt, req.ReleaseId, previous, "rolled_back", "candidate failed readiness or probation");
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
