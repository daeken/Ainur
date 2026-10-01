using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Ainur.Core.Runtime;

namespace Ainur.Core.Tools;

public sealed class McpServerConfig {
	public string Command { get; set; } = "";
	public List<string> Args { get; set; } = [];
	public Dictionary<string, string> Env { get; set; } = [];
	public string? Cwd { get; set; }
	public bool Enabled { get; set; } = true;
}

/// <summary>Minimal Model Context Protocol client over stdio (newline-delimited JSON-RPC 2.0).</summary>
public sealed class McpClient : IAsyncDisposable {
	readonly Process Process;
	readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> Pending = new();
	readonly SemaphoreSlim WriteGate = new(1, 1);
	long NextId;
	public string Name { get; }
	public string? ServerInfo { get; private set; }
	public readonly List<string> Stderr = [];

	McpClient(string name, Process process) {
		Name = name;
		Process = process;
		_ = Task.Run(ReadLoop);
		_ = Task.Run(async () => {
			while(await process.StandardError.ReadLineAsync() is { } line)
				lock(Stderr) { Stderr.Add(line); if(Stderr.Count > 200) Stderr.RemoveAt(0); }
		});
	}

	public static async Task<McpClient> StartAsync(string name, McpServerConfig config, CancellationToken ct) {
		var psi = new ProcessStartInfo(config.Command) {
			RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
			WorkingDirectory = config.Cwd ?? Environment.CurrentDirectory,
		};
		foreach(var a in config.Args) psi.ArgumentList.Add(a);
		foreach(var (k, v) in config.Env) psi.Environment[k] = v;
		var client = new McpClient(name, Process.Start(psi) ?? throw new InvalidOperationException($"Could not start MCP server {name}"));
		var init = await client.RequestAsync("initialize", new JsonObject {
			["protocolVersion"] = "2025-06-18",
			["capabilities"] = new JsonObject(),
			["clientInfo"] = new JsonObject { ["name"] = "ainur", ["version"] = BuiltinTool.BuildVersion },
		}, ct);
		client.ServerInfo = init?["serverInfo"]?.ToJsonString();
		await client.NotifyAsync("notifications/initialized", null);
		return client;
	}

	async Task ReadLoop() {
		try {
			while(await Process.StandardOutput.ReadLineAsync() is { } line) {
				if(string.IsNullOrWhiteSpace(line)) continue;
				JsonNode? msg;
				try { msg = JsonNode.Parse(line); } catch { continue; }
				if(msg?["id"] is JsonValue idv && idv.TryGetValue<long>(out var id) && Pending.TryRemove(id, out var tcs)) {
					if(msg["error"] is { } err) tcs.TrySetException(new ToolException($"MCP {Name} error: {err["message"]?.GetValue<string>() ?? err.ToJsonString()}"));
					else tcs.TrySetResult(msg["result"]);
				}
			}
		} finally {
			foreach(var tcs in Pending.Values) tcs.TrySetException(new ToolException($"MCP server {Name} exited"));
		}
	}

	public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken ct) {
		var id = Interlocked.Increment(ref NextId);
		var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Pending[id] = tcs;
		await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() });
		using var reg = ct.Register(() => {
			if(Pending.TryRemove(id, out var t)) t.TrySetCanceled();
			_ = NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "canceled by Ainur" });
		});
		return await tcs.Task;
	}

	public Task NotifyAsync(string method, JsonObject? parameters) =>
		SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters ?? new JsonObject() });

	async Task SendAsync(JsonObject message) {
		await WriteGate.WaitAsync();
		try {
			await Process.StandardInput.WriteLineAsync(message.ToJsonString());
			await Process.StandardInput.FlushAsync();
		} finally {
			WriteGate.Release();
		}
	}

	public async Task<List<JsonObject>> ListToolsAsync(CancellationToken ct) {
		var tools = new List<JsonObject>();
		string? cursor = null;
		do {
			var result = await RequestAsync("tools/list", cursor is null ? null : new JsonObject { ["cursor"] = cursor }, ct);
			foreach(var t in result?["tools"] as JsonArray ?? []) if(t is JsonObject o) tools.Add(o);
			cursor = result?["nextCursor"]?.GetValue<string>();
		} while(cursor is not null);
		return tools;
	}

	public bool Exited => Process.HasExited;

	public async ValueTask DisposeAsync() {
		try { Process.StandardInput.Close(); } catch { }
		if(!Process.WaitForExit(2000)) try { Process.Kill(true); } catch { }
		Process.Dispose();
		await Task.CompletedTask;
	}
}

/// <summary>A remote MCP tool adapted into the shared invocation lifecycle with a versioned contract.</summary>
public sealed class McpTool(McpClient client, string remoteName, string description, JsonObject schema) : ITool {
	public string Name { get; } = Sanitize($"{client.Name}__{remoteName}");
	public string Description => $"[MCP {client.Name}] {description}";
	public JsonObject InputSchema => schema;
	public string Version { get; } = $"{Sanitize($"{client.Name}__{remoteName}")}@mcp-{Hash.Sha256(description + schema.ToJsonString())[..12]}";
	public TimeSpan Timeout => TimeSpan.FromMinutes(5);
	public IReadOnlyList<string> Tags => ["mcp", client.Name];

	static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? char.ToLowerInvariant(c) : '_').ToArray());

	public async Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		if(client.Exited) throw new ToolException($"MCP server {client.Name} is not running");
		var result = await client.RequestAsync("tools/call", new JsonObject { ["name"] = remoteName, ["arguments"] = args.DeepClone() }, ctx.CancellationToken);
		var text = string.Join("\n", (result?["content"] as JsonArray ?? []).Select(c => c?["type"]?.GetValue<string>() switch {
			"text" => c["text"]?.GetValue<string>(),
			"resource" => c["resource"]?["text"]?.GetValue<string>() ?? c["resource"]?.ToJsonString(),
			var other => $"[{other} content]",
		}));
		if(result?["structuredContent"] is { } structured && text.Length == 0) text = structured.ToJsonString();
		var isError = result?["isError"]?.GetValue<bool>() ?? false;
		return new ToolResult { Text = text.Length == 0 ? "[empty result]" : text, IsError = isError, Value = result?["structuredContent"], Description = $"mcp {Name}" };
	}
}

public static class McpLoader {
	public static string ConfigPath(AinurRuntime rt) => Path.Combine(rt.Options.Home, "mcp.json");

	/// <summary>Starts explicitly configured MCP servers and registers their tools. Failures are journaled, not fatal.</summary>
	public static async Task<List<McpClient>> LoadAsync(AinurRuntime rt, CancellationToken ct) {
		var clients = new List<McpClient>();
		var path = ConfigPath(rt);
		if(!File.Exists(path)) return clients;
		var config = JsonUtil.Deserialize<Dictionary<string, Dictionary<string, McpServerConfig>>>(File.ReadAllText(path))?.GetValueOrDefault("servers") ?? [];
		foreach(var (name, server) in config.Where(kv => kv.Value.Enabled)) {
			try {
				using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
				timeout.CancelAfter(TimeSpan.FromSeconds(30));
				var client = await McpClient.StartAsync(name, server, timeout.Token);
				var tools = await client.ListToolsAsync(timeout.Token);
				foreach(var t in tools)
					rt.Tools.Register(new McpTool(client, t["name"]!.GetValue<string>(), t["description"]?.GetValue<string>() ?? "",
						t["inputSchema"] as JsonObject is { } s ? (JsonObject) s.DeepClone() : new JsonObject { ["type"] = "object" }), kind: "mcp", source: name);
				clients.Add(client);
				rt.Db.Write(u => u.Journal("mcp.connected", null, "mcp_server", name, payload: new { tools = tools.Count, server = client.ServerInfo }));
			} catch(Exception e) {
				rt.Db.Write(u => u.Journal("mcp.failed", null, "mcp_server", name, payload: new { error = e.Message }));
			}
		}
		return clients;
	}
}
