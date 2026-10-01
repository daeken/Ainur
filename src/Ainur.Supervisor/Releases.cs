using System.Diagnostics;
using System.Text.Json;

namespace Ainur.Supervisor;

public sealed class ReleaseState {
	public string? Active { get; set; }
	public string? Previous { get; set; }
	public List<string> Failed { get; set; } = [];
}

/// <summary>Immutable release directories under the home, outside any worktree, plus the active/previous selection.</summary>
public sealed class Releases(string home) {
	static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true };
	public string Root => Path.Combine(home, "releases");
	public string StatePath => Path.Combine(home, "supervisor", "releases.json");
	public string PathFor(string id) => Path.Combine(Root, id);

	public ReleaseState LoadState() =>
		File.Exists(StatePath) ? JsonSerializer.Deserialize<ReleaseState>(File.ReadAllText(StatePath), Options) ?? new() : new();

	public void SaveState(ReleaseState state) {
		Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
		var tmp = StatePath + ".tmp";
		File.WriteAllText(tmp, JsonSerializer.Serialize(state, Options));
		File.Move(tmp, StatePath, true);
	}

	/// <summary>Builds the web UI and publishes the runtime into a new immutable release directory.</summary>
	public async Task<string> BuildAsync(string source, TextWriter log) {
		var rev = (await Run("git", ["rev-parse", "--short=10", "HEAD"], source, log, required: false)).Trim();
		var dirty = (await Run("git", ["status", "--porcelain"], source, log, required: false)).Trim().Length > 0;
		var id = $"r{DateTime.UtcNow:yyyyMMddHHmmss}-{(rev.Length > 0 ? rev : "nogit")}{(dirty ? "-dirty" : "")}";
		var output = PathFor(id);
		var web = Path.Combine(source, "web");
		if(File.Exists(Path.Combine(web, "package.json"))) {
			if(!Directory.Exists(Path.Combine(web, "node_modules")))
				await Run("npm", ["ci", "--no-audit", "--no-fund"], web, log);
			await Run("npm", ["run", "build"], web, log);
		}
		await Run("dotnet", ["publish", Path.Combine(source, "src", "Ainur.Server", "Ainur.Server.csproj"), "-c", "Release", "-o", output, "--nologo", "-v", "quiet"], source, log);
		File.WriteAllText(Path.Combine(output, "release.json"), JsonSerializer.Serialize(new { id, source, revision = rev, dirty, built_at = DateTime.UtcNow }, Options));
		return id;
	}

	static async Task<string> Run(string file, string[] args, string cwd, TextWriter log, bool required = true) {
		var psi = new ProcessStartInfo(file) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach(var a in args) psi.ArgumentList.Add(a);
		using var p = Process.Start(psi)!;
		var stdout = p.StandardOutput.ReadToEndAsync();
		var stderr = p.StandardError.ReadToEndAsync();
		await p.WaitForExitAsync();
		if(p.ExitCode != 0 && required) {
			await log.WriteLineAsync(await stdout);
			await log.WriteLineAsync(await stderr);
			throw new InvalidOperationException($"{file} {string.Join(' ', args)} failed with exit code {p.ExitCode}");
		}
		return await stdout;
	}
}
