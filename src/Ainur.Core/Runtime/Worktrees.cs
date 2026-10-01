using System.Diagnostics;
using System.Text;
using Ainur.Core.Model;
using Dapper;

namespace Ainur.Core.Runtime;

/// <summary>
/// Isolated git worktrees for consultation forks, materialized from the original's repository snapshot including
/// uncommitted changes, and a serialized integration path that applies a fork's changes back with a 3-way merge.
/// </summary>
public static class Worktrees {
	static readonly Lock IntegrationGate = new();

	public sealed record Result(int ExitCode, string Output);

	public static Result Git(string cwd, params string[] args) {
		var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
		foreach(var a in args) psi.ArgumentList.Add(a);
		psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
		using var p = Process.Start(psi)!;
		var stdout = p.StandardOutput.ReadToEndAsync();
		var stderr = p.StandardError.ReadToEndAsync();
		p.WaitForExit();
		return new(p.ExitCode, stdout.Result + stderr.Result);
	}

	public static bool IsRepository(string? path) => path is not null && Directory.Exists(path) && Git(path, "rev-parse", "--is-inside-work-tree").Output.Trim() == "true";

	/// <summary>Creates a detached worktree whose contents equal the workspace right now (committed + uncommitted + untracked).</summary>
	public static (string Path, string BaseCommit) Materialize(string workspace, string root, string name) {
		var top = Git(workspace, "rev-parse", "--show-toplevel").Output.Trim();
		var stash = Git(top, "stash", "create", "ainur fork snapshot").Output.Trim();
		var head = Git(top, "rev-parse", "HEAD").Output.Trim();
		var commit = stash.Length == 40 ? stash : head;
		var path = Path.Combine(root, name);
		Directory.CreateDirectory(root);
		var add = Git(top, "worktree", "add", "--detach", path, commit);
		if(add.ExitCode != 0) throw new InvalidOperationException($"git worktree add failed: {add.Output}");
		// Untracked (not ignored) files are part of the snapshot too.
		foreach(var rel in Git(top, "ls-files", "--others", "--exclude-standard", "-z").Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)) {
			var dest = Path.Combine(path, rel);
			Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
			File.Copy(Path.Combine(top, rel), dest, overwrite: true);
		}
		// Record the full snapshot as the base so later diffs show only the fork's own changes.
		Git(path, "add", "-A");
		var baseCommit = Git(path, "-c", "user.name=Ainur", "-c", "user.email=ainur@localhost", "commit", "--allow-empty", "-q", "-m", "ainur: fork snapshot").ExitCode == 0
			? Git(path, "rev-parse", "HEAD").Output.Trim() : commit;
		return (path, baseCommit);
	}

	/// <summary>Applies the fork's changes to the original workspace through a serialized 3-way integration.</summary>
	public static (bool Applied, string Summary, string? Patch) Integrate(string worktree, string baseCommit, string workspace) {
		lock(IntegrationGate) {
			Git(worktree, "add", "-A");
			var patch = Git(worktree, "diff", "--cached", "--binary", baseCommit).Output;
			if(string.IsNullOrWhiteSpace(patch)) return (true, "The fork made no file changes.", null);
			var stat = Git(worktree, "diff", "--cached", "--stat", baseCommit).Output.Trim();
			var top = Git(workspace, "rev-parse", "--show-toplevel").Output.Trim();
			var tmp = Path.GetTempFileName();
			File.WriteAllText(tmp, patch);
			try {
				// git apply is all-or-nothing: check first so a conflict never leaves the workspace half-patched.
				var check = Git(top, "apply", "--check", "--whitespace=nowarn", tmp);
				if(check.ExitCode == 0 && Git(top, "apply", "--whitespace=nowarn", tmp).ExitCode == 0)
					return (true, $"Integrated the fork's changes into the workspace:\n{stat}", patch);
				return (false, $"The fork's changes conflict with newer work and were NOT applied; the workspace is unchanged and reconciliation is required.\n{stat}\n{TextUtil.Truncate(check.Output, 1500)}", patch);
			} finally {
				File.Delete(tmp);
			}
		}
	}

	public static void Remove(string worktree) {
		try {
			Git(worktree, "worktree", "remove", "--force", worktree);
			if(Directory.Exists(worktree)) Directory.Delete(worktree, true);
		} catch { }
	}
}
