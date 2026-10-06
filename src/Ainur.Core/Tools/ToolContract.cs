using System.Text.Json;
using System.Text.Json.Nodes;
using Ainur.Core.Model;

namespace Ainur.Core.Tools;

/// <summary>Immutable content-addressed image produced by a tool; bytes remain in ArtifactStore, never in transcript text.</summary>
public sealed record ToolImage(string Artifact, string MimeType, int Width, int Height);

public sealed class ToolResult {
	public string Text { get; init; } = "";
	public IReadOnlyList<ToolImage> Images { get; init; } = [];
	public bool IsError { get; init; }
	/// <summary>Optional live .NET value. PowerShell pipelines receive it directly; the model sees a handle and summary.</summary>
	public object? Value { get; init; }
	/// <summary>Short description used in elision records.</summary>
	public string? Description { get; init; }
	/// <summary>Ends the agent's turn after this result is recorded.</summary>
	public bool EndsTurn { get; init; }

	public static ToolResult Ok(string text, object? value = null, string? description = null) => new() { Text = text, Value = value, Description = description };
	public static ToolResult Error(string text) => new() { Text = text, IsError = true };
}

/// <summary>Thrown by tools for expected failures that should be reported to the model rather than crash the turn.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>Everything an inline tool may use while it runs. Tools are trusted in-process code.</summary>
public sealed class ToolContext {
	public required Runtime.AinurRuntime Runtime { get; init; }
	public required Agent Agent { get; init; }
	public required Session Session { get; init; }
	public required Project Project { get; init; }
	public required string InvocationId { get; init; }
	public required CancellationToken CancellationToken { get; init; }
	public required Runtime.SessionHost Host { get; init; }

	public string Workspace => Session.WorkspacePath ?? Project.WorkspacePath ?? Environment.CurrentDirectory;

	public string ResolvePath(string path) {
		if(path.StartsWith("~/")) path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
		return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Workspace, path));
	}

	/// <summary>Invokes another tool inline as a nested invocation recorded under this one.</summary>
	public Task<ToolResult> InvokeToolAsync(string name, JsonObject args) => Host.InvokeNestedAsync(name, args, InvocationId, CancellationToken);
}

public interface ITool {
	string Name { get; }
	string Description { get; }
	JsonObject InputSchema { get; }
	/// <summary>Content-derived version identifier. Built-ins use the assembly version and name.</summary>
	string Version { get; }
	TimeSpan Timeout { get; }
	/// <summary>The deadline for one call, which may depend on its arguments (e.g. a script's own timeout).</summary>
	TimeSpan TimeoutFor(JsonObject args) => Timeout;
	/// <summary>Search tags used by the tool finder and goal-based initial selection.</summary>
	IReadOnlyList<string> Tags { get; }
	Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args);
}

public abstract class BuiltinTool : ITool {
	public abstract string Name { get; }
	public abstract string Description { get; }
	public abstract JsonObject InputSchema { get; }
	public virtual string Version => $"{Name}@builtin-{BuildVersion}";
	public virtual TimeSpan Timeout => TimeSpan.FromMinutes(2);
	public virtual TimeSpan TimeoutFor(JsonObject args) => Timeout;
	public virtual IReadOnlyList<string> Tags => [];
	public abstract Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args);

	public static readonly string BuildVersion = typeof(BuiltinTool).Assembly.GetName().Version?.ToString() ?? "0";

	protected static string Str(JsonObject args, string name) =>
		args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ToolException($"Missing required string argument '{name}'");
	protected static string? OptStr(JsonObject args, string name) =>
		args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : args[name] is JsonValue o ? o.ToJsonString() : null;
	protected static int? OptInt(JsonObject args, string name) => args[name] switch {
		JsonValue v when v.TryGetValue<int>(out var i) => i,
		JsonValue v when v.TryGetValue<long>(out var l) => (int) l,
		JsonValue v when v.TryGetValue<double>(out var d) => (int) d,
		JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, out var p) => p,
		_ => null,
	};
	protected static bool? OptBool(JsonObject args, string name) => args[name] switch {
		JsonValue v when v.TryGetValue<bool>(out var b) => b,
		JsonValue v when v.TryGetValue<string>(out var s) && bool.TryParse(s, out var p) => p,
		_ => null,
	};
	protected static List<string> StrList(JsonObject args, string name) => args[name] switch {
		JsonArray a => a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : x?.ToJsonString() ?? "").ToList(),
		JsonValue v when v.TryGetValue<string>(out var s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
		_ => [],
	};
}

/// <summary>Compact JSON Schema construction for tool inputs.</summary>
public static class Schema {
	public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] props) {
		var properties = new JsonObject();
		var required = new JsonArray();
		foreach(var (name, schema, req) in props) {
			properties[name] = schema;
			if(req) required.Add(name);
		}
		var obj = new JsonObject { ["type"] = "object", ["properties"] = properties };
		if(required.Count > 0) obj["required"] = required;
		obj["additionalProperties"] = false;
		return obj;
	}
	public static JsonObject String(string description, params string[] enumValues) {
		var s = new JsonObject { ["type"] = "string", ["description"] = description };
		if(enumValues.Length > 0) s["enum"] = new JsonArray(enumValues.Select(v => (JsonNode) v).ToArray());
		return s;
	}
	public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };
	public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };
	public static JsonObject Boolean(string description) => new() { ["type"] = "boolean", ["description"] = description };
	public static JsonObject Array(string description, JsonObject items) => new() { ["type"] = "array", ["description"] = description, ["items"] = items };

	public static int TokenEstimate(ITool tool) => Tokens.Estimate(tool.Name) + Tokens.Estimate(tool.Description) + Tokens.Estimate(tool.InputSchema.ToJsonString()) + 8;

	/// <summary>Parses model-supplied argument text, tolerating an empty string.</summary>
	public static JsonObject ParseArguments(string text) {
		if(string.IsNullOrWhiteSpace(text)) return [];
		try {
			return JsonNode.Parse(text) as JsonObject ?? throw new ToolException("Tool arguments must be a JSON object");
		} catch(JsonException e) {
			throw new ToolException($"Tool arguments are not valid JSON: {e.Message}");
		}
	}
}
