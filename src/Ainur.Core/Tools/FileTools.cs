using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ainur.Core.Tools;

public sealed record EditSpec(string OldText, string NewText, bool ReplaceAll = false);

/// <summary>
/// Pure multi-edit engine. Every edit is validated against the evolving in-memory text; if any edit fails,
/// nothing is written and every failure is reported, so a partial patch never leaves the file half-applied.
/// </summary>
public static class MultiEdit {
	public sealed record Outcome(string? Text, List<string> Errors, List<string> Applied);

	public static Outcome Apply(string original, IReadOnlyList<EditSpec> edits, string? append) {
		var errors = new List<string>();
		var applied = new List<string>();
		var text = original;
		for(var i = 0; i < edits.Count; i++) {
			var e = edits[i];
			var label = $"edit {i + 1}";
			if(e.OldText.Length == 0) {
				errors.Add($"{label}: old_text is empty; use append to add text at the end of the file");
				continue;
			}
			var count = CountOccurrences(text, e.OldText);
			if(count == 0) {
				errors.Add($"{label}: old_text not found{NearMissHint(text, e.OldText)}. First line of old_text: {Quote(FirstLine(e.OldText))}");
				continue;
			}
			if(count > 1 && !e.ReplaceAll) {
				errors.Add($"{label}: old_text matches {count} locations (lines {string.Join(", ", LinesOf(text, e.OldText).Take(10))}); include more surrounding context or set replace_all");
				continue;
			}
			var line = LinesOf(text, e.OldText).First();
			text = e.ReplaceAll ? text.Replace(e.OldText, e.NewText, StringComparison.Ordinal) : ReplaceFirst(text, e.OldText, e.NewText);
			applied.Add($"{label}: replaced {count} occurrence{(count == 1 ? "" : "s")} at line {line}");
		}
		if(!string.IsNullOrEmpty(append)) {
			if(text.Length > 0 && !text.EndsWith('\n')) text += "\n";
			var appendLine = text.Count(c => c == '\n') + 1;
			text += append;
			applied.Add($"append: added {append.Count(c => c == '\n') + (append.EndsWith('\n') ? 0 : 1)} line(s) at line {appendLine}");
		}
		return new(errors.Count == 0 ? text : null, errors, applied);
	}

	static int CountOccurrences(string text, string needle) {
		var count = 0;
		for(var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
			count++;
		return count;
	}

	static IEnumerable<int> LinesOf(string text, string needle) {
		for(var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
			yield return LineAt(text, i);
	}

	static int LineAt(string text, int index) {
		var line = 1;
		for(var i = 0; i < index; i++) if(text[i] == '\n') line++;
		return line;
	}

	static string ReplaceFirst(string text, string old, string replacement) {
		var i = text.IndexOf(old, StringComparison.Ordinal);
		return text[..i] + replacement + text[(i + old.Length)..];
	}

	static string FirstLine(string s) => s.Split('\n')[0];
	static string Quote(string s) => "\"" + TextUtil.Truncate(s, 120) + "\"";

	/// <summary>Explains likely causes of a failed match: whitespace, line endings, or a matching first line elsewhere.</summary>
	static string NearMissHint(string text, string needle) {
		static string Norm(string s) => Regex.Replace(s.Replace("\r\n", "\n"), @"[ \t]+", " ").Trim();
		if(text.Contains(needle.Replace("\r\n", "\n"), StringComparison.Ordinal) || text.Contains(needle.Replace("\n", "\r\n"), StringComparison.Ordinal))
			return " (it would match with different line endings)";
		var lines = text.Split('\n');
		var needleLines = needle.Split('\n').Select(Norm).Where(l => l.Length > 0).ToArray();
		if(needleLines.Length == 0) return "";
		static string Flat(string s) => Regex.Replace(s, @"\s+", " ").Trim();
		if(Flat(text).Contains(Flat(needle), StringComparison.Ordinal)) {
			for(var i = 0; i < lines.Length; i++)
				if(Norm(lines[i]) == needleLines[0])
					return $" (a whitespace-insensitive match starts at line {i + 1}; check indentation and spacing)";
			return " (a whitespace-insensitive match exists; check indentation and spacing)";
		}
		for(var i = 0; i < lines.Length; i++)
			if(Norm(lines[i]) == needleLines[0])
				return $" (its first line appears at line {i + 1} but later lines differ)";
		return "";
	}
}

public static class FileOps {
	/// <summary>Writes via a temp file and rename so readers never observe a partially written file.</summary>
	public static void AtomicWrite(string path, string content) {
		var dir = Path.GetDirectoryName(path)!;
		Directory.CreateDirectory(dir);
		var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
		try {
			File.WriteAllText(tmp, content, new UTF8Encoding(false));
			if(File.Exists(path) && !OperatingSystem.IsWindows())
				File.SetUnixFileMode(tmp, File.GetUnixFileMode(path));
			File.Move(tmp, path, overwrite: true);
		} finally {
			if(File.Exists(tmp)) File.Delete(tmp);
		}
	}

	public static readonly HashSet<string> SkippedDirectories = [".git", "bin", "obj", "node_modules", "dist", ".ainur", ".vs", ".idea"];

	public static IEnumerable<string> WalkFiles(string root) {
		var stack = new Stack<string>([root]);
		while(stack.TryPop(out var dir)) {
			IEnumerable<string> files, dirs;
			try {
				files = Directory.EnumerateFiles(dir);
				dirs = Directory.EnumerateDirectories(dir);
			} catch(UnauthorizedAccessException) {
				continue;
			}
			foreach(var f in files.Order()) yield return f;
			foreach(var d in dirs.OrderDescending())
				if(!SkippedDirectories.Contains(Path.GetFileName(d)))
					stack.Push(d);
		}
	}

	public static bool LooksBinary(string path) {
		try {
			using var fs = File.OpenRead(path);
			var buf = new byte[Math.Min(8000, fs.Length)];
			var n = fs.Read(buf, 0, buf.Length);
			return buf.AsSpan(0, n).IndexOf((byte) 0) >= 0;
		} catch {
			return true;
		}
	}

	public static Regex GlobToRegex(string glob) {
		var sb = new StringBuilder("^");
		for(var i = 0; i < glob.Length; i++) {
			var c = glob[i];
			if(c == '*' && i + 1 < glob.Length && glob[i + 1] == '*') {
				sb.Append(".*");
				i++;
				if(i + 1 < glob.Length && glob[i + 1] == '/') i++;
			} else if(c == '*') sb.Append("[^/]*");
			else if(c == '?') sb.Append("[^/]");
			else sb.Append(Regex.Escape(c.ToString()));
		}
		return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase);
	}
}

public sealed class MultiEditTool : BuiltinTool {
	public override string Name => "multi_edit";
	public override string Description => """
		Atomically edit one file. Each edit replaces an exact old_text block with new_text, applied in order to the evolving text.
		Every old_text must match exactly once (or set replace_all). Optionally append text to the end. If any edit fails,
		the file is left untouched and every failure is reported. A missing file is created only when there are no edits.
		""";
	public override IReadOnlyList<string> Tags => ["file", "edit", "patch", "replace", "code", "write"];
	public override JsonObject InputSchema => Schema.Object(
		("path", Schema.String("File path, absolute or relative to the project workspace."), true),
		("edits", Schema.Array("Replacements applied in order.", Schema.Object(
			("old_text", Schema.String("Exact text to find, including whitespace."), true),
			("new_text", Schema.String("Replacement text."), true),
			("replace_all", Schema.Boolean("Replace every occurrence instead of requiring a unique match."), false))), false),
		("append", Schema.String("Text to add at the end of the file after the edits."), false),
		("expected_sha256", Schema.String("Optional hash of the current file contents; the edit fails if the file has changed."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var path = ctx.ResolvePath(Str(args, "path"));
		var edits = (args["edits"] as JsonArray ?? []).Select((e, i) => e is JsonObject o
			? new EditSpec(Str(o, "old_text"), o["new_text"]?.GetValue<string>() ?? throw new ToolException($"edit {i + 1}: missing new_text"), OptBool(o, "replace_all") ?? false)
			: throw new ToolException($"edit {i + 1} must be an object")).ToList();
		var append = OptStr(args, "append");
		if(edits.Count == 0 && string.IsNullOrEmpty(append))
			throw new ToolException("Provide at least one edit or append text");
		var exists = File.Exists(path);
		if(!exists && edits.Count > 0)
			throw new ToolException($"{path} does not exist; edits need an existing file (use append alone or write_file to create one)");
		var original = exists ? File.ReadAllText(path) : "";
		var originalHash = Hash.Sha256(original);
		if(OptStr(args, "expected_sha256") is { } expected && !expected.Equals(originalHash, StringComparison.OrdinalIgnoreCase))
			throw new ToolException($"{path} changed: expected sha256 {expected}, found {originalHash}. Nothing was written.");
		var outcome = MultiEdit.Apply(original, edits, append);
		if(outcome.Text is null)
			return Task.FromResult(ToolResult.Error($"multi_edit failed; {path} was NOT modified.\n" + string.Join("\n", outcome.Errors)
				+ (outcome.Applied.Count > 0 ? $"\n({outcome.Applied.Count} other edit(s) would have applied but were discarded.)" : "")));
		// Detect a concurrent writer between our read and the rename.
		if(exists && Hash.Sha256(File.ReadAllText(path)) != originalHash)
			return Task.FromResult(ToolResult.Error($"{path} changed while editing; nothing was written. Re-read and retry."));
		FileOps.AtomicWrite(path, outcome.Text);
		return Task.FromResult(ToolResult.Ok($"Edited {path} ({(exists ? "modified" : "created")}, sha256 {Hash.Sha256(outcome.Text)[..16]}…)\n" + string.Join("\n", outcome.Applied),
			description: $"multi_edit {Path.GetFileName(path)}"));
	}
}

public sealed class ReadFileTool : BuiltinTool {
	public override string Name => "read_file";
	public override string Description => "Read a text file with line numbers. Use offset/limit (1-based lines) for large files.";
	public override IReadOnlyList<string> Tags => ["file", "read", "view", "code"];
	public override JsonObject InputSchema => Schema.Object(
		("path", Schema.String("File path, absolute or relative to the project workspace."), true),
		("offset", Schema.Integer("First line to return (1-based). Default 1."), false),
		("limit", Schema.Integer("Maximum number of lines. Default 2000."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var path = ctx.ResolvePath(Str(args, "path"));
		if(Directory.Exists(path)) throw new ToolException($"{path} is a directory; use list_files");
		if(!File.Exists(path)) throw new ToolException($"{path} does not exist");
		if(FileOps.LooksBinary(path)) throw new ToolException($"{path} looks like a binary file");
		var lines = File.ReadAllLines(path);
		var offset = Math.Max(1, OptInt(args, "offset") ?? 1);
		var limit = Math.Max(1, OptInt(args, "limit") ?? 2000);
		var sb = new StringBuilder();
		foreach(var (line, i) in lines.Skip(offset - 1).Take(limit).Select((l, i) => (l, i)))
			sb.Append($"{offset + i,6}\t{TextUtil.Truncate(line, 2000)}\n");
		var shown = Math.Min(limit, Math.Max(0, lines.Length - offset + 1));
		if(offset + shown - 1 < lines.Length)
			sb.Append($"[showing lines {offset}-{offset + shown - 1} of {lines.Length}]\n");
		if(lines.Length == 0) sb.Append("[empty file]\n");
		return Task.FromResult(ToolResult.Ok(sb.ToString(), description: $"read {Path.GetFileName(path)} lines {offset}-{offset + shown - 1} of {lines.Length}"));
	}
}

public sealed class WriteFileTool : BuiltinTool {
	public override string Name => "write_file";
	public override string Description => "Create or fully overwrite a text file atomically. Prefer multi_edit for changes to existing files.";
	public override IReadOnlyList<string> Tags => ["file", "write", "create", "code"];
	public override JsonObject InputSchema => Schema.Object(
		("path", Schema.String("File path, absolute or relative to the project workspace."), true),
		("content", Schema.String("Complete file contents."), true));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var path = ctx.ResolvePath(Str(args, "path"));
		var content = args["content"]?.GetValue<string>() ?? throw new ToolException("Missing content");
		var existed = File.Exists(path);
		FileOps.AtomicWrite(path, content);
		return Task.FromResult(ToolResult.Ok($"{(existed ? "Overwrote" : "Created")} {path} ({content.Length} chars, {content.Count(c => c == '\n') + 1} lines)", description: $"write {Path.GetFileName(path)}"));
	}
}

public sealed class ListFilesTool : BuiltinTool {
	public override string Name => "list_files";
	public override string Description => "List files under a directory, optionally filtered by a glob such as **/*.cs. Skips .git, bin, obj, node_modules.";
	public override IReadOnlyList<string> Tags => ["file", "list", "glob", "find", "directory"];
	public override JsonObject InputSchema => Schema.Object(
		("path", Schema.String("Directory (default: workspace root)."), false),
		("glob", Schema.String("Glob relative to path, e.g. src/**/*.ts"), false),
		("limit", Schema.Integer("Maximum entries (default 500)."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var root = ctx.ResolvePath(OptStr(args, "path") ?? ".");
		if(!Directory.Exists(root)) throw new ToolException($"{root} is not a directory");
		var glob = OptStr(args, "glob");
		var regex = glob is null ? null : FileOps.GlobToRegex(glob);
		var limit = OptInt(args, "limit") ?? 500;
		var results = FileOps.WalkFiles(root).Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
			.Where(r => regex is null || regex.IsMatch(r)).Take(limit + 1).ToList();
		var text = string.Join("\n", results.Take(limit)) + (results.Count > limit ? $"\n[truncated at {limit} entries]" : "") + (results.Count == 0 ? "[no matching files]" : "");
		return Task.FromResult(ToolResult.Ok(text, results.Take(limit).ToList(), $"list {results.Count} files under {root}"));
	}
}

public sealed class SearchTextTool : BuiltinTool {
	public override string Name => "search_text";
	public override string Description => "Search file contents with a .NET regular expression. Returns path:line: text for matches.";
	public override IReadOnlyList<string> Tags => ["file", "search", "grep", "regex", "find", "code"];
	public override JsonObject InputSchema => Schema.Object(
		("pattern", Schema.String("Regular expression."), true),
		("path", Schema.String("Directory or file to search (default workspace root)."), false),
		("glob", Schema.String("Only search files matching this glob, e.g. **/*.cs"), false),
		("ignore_case", Schema.Boolean("Case-insensitive match."), false),
		("limit", Schema.Integer("Maximum matches (default 200)."), false));

	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var root = ctx.ResolvePath(OptStr(args, "path") ?? ".");
		var regex = new Regex(Str(args, "pattern"), (OptBool(args, "ignore_case") ?? false ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
		var glob = OptStr(args, "glob") is { } g ? FileOps.GlobToRegex(g) : null;
		var limit = OptInt(args, "limit") ?? 200;
		var files = File.Exists(root) ? [root] : FileOps.WalkFiles(root);
		var sb = new StringBuilder();
		var count = 0;
		foreach(var file in files) {
			ctx.CancellationToken.ThrowIfCancellationRequested();
			var rel = File.Exists(root) ? Path.GetFileName(file) : Path.GetRelativePath(root, file).Replace('\\', '/');
			if(glob is not null && !glob.IsMatch(rel)) continue;
			if(new FileInfo(file).Length > 4_000_000 || FileOps.LooksBinary(file)) continue;
			var lineNo = 0;
			foreach(var line in File.ReadLines(file)) {
				lineNo++;
				if(!regex.IsMatch(line)) continue;
				sb.Append($"{rel}:{lineNo}: {TextUtil.Truncate(line.Trim(), 300)}\n");
				if(++count >= limit) {
					sb.Append($"[stopped at {limit} matches]\n");
					return Task.FromResult(ToolResult.Ok(sb.ToString(), description: $"search /{regex}/: {count}+ matches"));
				}
			}
		}
		return Task.FromResult(ToolResult.Ok(count == 0 ? "[no matches]" : sb.ToString(), description: $"search /{regex}/: {count} matches"));
	}
}
