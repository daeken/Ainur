using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Ainur.Core.Providers;
using Dapper;

namespace Ainur.Core.Tools;

/// <summary>
/// One registry for built-in, agent-authored, and (later) MCP tools. Versions are immutable: a new definition
/// with the same name becomes the current version while existing references keep resolving their pinned version.
/// </summary>
public sealed class ToolRegistry(Store store) {
	readonly Lock Gate = new();
	readonly Dictionary<string, ITool> Current = new(StringComparer.Ordinal);
	readonly Dictionary<string, ITool> Versions = new(StringComparer.Ordinal);
	readonly Dictionary<string, string?> Scope = new(StringComparer.Ordinal); // name -> project id (null = global)
	public event Action<string>? Changed;

	public static readonly string[] CoreTools = ["find_tools", "load_tools", "read_result", "elide_results", "send_message", "disposition_work", "powershell"];
	public static readonly string[] ManagerDefaults = ["team", "create_agent", "assign_work", "objectives", "create_objective", "update_objective", "retire_agent", "reassign_agent", "reply_to_user", "consult", "read_identity", "write_identity", "costs", "knowledge_search", "knowledge_write"];
	public static readonly string[] SpecialistDefaults = ["read_file", "multi_edit", "write_file", "list_files", "search_text", "objectives", "update_objective", "create_objective", "read_identity", "write_identity", "knowledge_search", "knowledge_write"];

	public void Register(ITool tool, string? projectId = null, string kind = "builtin", string? source = null, string? createdBy = null) {
		lock(Gate) {
			Current[tool.Name] = tool;
			Versions[tool.Version] = tool;
			Scope[tool.Name] = projectId;
		}
		store.Db.Write(u => u.Execute("""
			INSERT INTO tool_versions(id,name,kind,project_id,description,input_schema,source,created_by,state,created_at)
			VALUES(@Version,@Name,@kind,@projectId,@Description,@schema,@source,@createdBy,'active',@now)
			ON CONFLICT(id) DO NOTHING
			""", new { tool.Version, tool.Name, kind, projectId, tool.Description, schema = tool.InputSchema.ToJsonString(), source, createdBy, now = Clock.Now }));
		Changed?.Invoke(tool.Name);
	}

	public ITool? Get(string name) { lock(Gate) return Current.GetValueOrDefault(name); }
	public ITool? GetVersion(string version) { lock(Gate) return Versions.GetValueOrDefault(version); }

	public bool IsQuarantined(string version) => store.Db.Read(c => c.ExecuteScalar<string>("SELECT state FROM tool_versions WHERE id=@version", new { version })) == "quarantined";

	public void Quarantine(string version, string reason) => store.Db.Write(u => {
		u.Execute("UPDATE tool_versions SET state='quarantined' WHERE id=@version", new { version });
		u.Journal("tool.quarantined", null, "tool_version", version, payload: new { reason });
	});

	/// <summary>All tools an agent in <paramref name="projectId"/> may use.</summary>
	public List<ITool> Available(string projectId) {
		lock(Gate) return Current.Values.Where(t => Scope.GetValueOrDefault(t.Name) is null || Scope[t.Name] == projectId).OrderBy(t => t.Name).ToList();
	}

	public List<string> DefaultsFor(Agent agent) =>
		CoreTools.Concat(agent.Role == Roles.Manager ? ManagerDefaults : SpecialistDefaults).Distinct().Where(n => Get(n) is not null).ToList();

	/// <summary>Keyword relevance over names, tags, and descriptions. The cheap-model finder ranks on top of this.</summary>
	public List<(ITool Tool, double Score)> Search(string projectId, string query, int limit = 10) {
		var terms = query.ToLowerInvariant().Split([' ', ',', '.', '_', '-', '/', ':', ';', '\n'], StringSplitOptions.RemoveEmptyEntries).Where(t => t.Length > 1).Distinct().ToList();
		return Available(projectId).Select(t => {
			var name = t.Name.ToLowerInvariant();
			var desc = t.Description.ToLowerInvariant();
			var score = 0.0;
			foreach(var term in terms) {
				if(name == term) score += 6;
				else if(name.Contains(term)) score += 3;
				if(t.Tags.Any(tag => tag.Equals(term, StringComparison.OrdinalIgnoreCase))) score += 2;
				if(desc.Contains(term)) score += 1;
			}
			return (t, score);
		}).Where(x => x.score > 0).OrderByDescending(x => x.score).ThenBy(x => x.t.Name).Take(limit).ToList();
	}

	public static ToolSpec Spec(ITool tool) => new(tool.Name, tool.Description.Trim(), tool.InputSchema);
}

/// <summary>
/// Per-session LRU cache of tool declarations, bounded by rendered tool tokens. Core tools are pinned.
/// Eviction only removes future declarations; it never erases invocation history.
/// </summary>
public sealed class ToolCache(Store store, ToolRegistry registry, string sessionId) {
	public sealed record Entry(string Name, string Version, bool Pinned, long LastUsed);

	public List<Entry> Entries() => store.Db.Read(c => c.Query<(string Name, string Version, long Pinned, long LastUsed)>(
		"SELECT tool_name, tool_version, pinned, last_used FROM tool_cache WHERE session_id=@sessionId", new { sessionId })
		.Select(r => new Entry(r.Name, r.Version, r.Pinned != 0, r.LastUsed)).ToList());

	public List<ITool> Tools() => Entries().Select(e => registry.Get(e.Name)).Where(t => t is not null).Select(t => t!).OrderBy(t => t.Name).ToList();

	public int RenderedTokens() => Tools().Sum(Schema.TokenEstimate);

	static long Tick = 0;
	static long NextTick() => Math.Max(Clock.Now * 1000, Interlocked.Increment(ref Tick));

	/// <summary>Loads tools (refreshing recency) then evicts least-recently-used unpinned entries over budget.</summary>
	public (List<string> Loaded, List<string> Evicted, List<string> Missing) Load(IEnumerable<string> names, int budgetTokens, bool pinned = false) {
		var loaded = new List<string>();
		var missing = new List<string>();
		store.Db.Write(u => {
			foreach(var name in names.Distinct()) {
				if(registry.Get(name) is not { } tool) {
					missing.Add(name);
					continue;
				}
				u.Execute("""
					INSERT INTO tool_cache(session_id,tool_name,tool_version,pinned,last_used) VALUES(@sessionId,@name,@version,@pinned,@now)
					ON CONFLICT(session_id,tool_name) DO UPDATE SET tool_version=@version, last_used=@now, pinned=MAX(pinned,@pinned)
					""", new { sessionId, name, version = tool.Version, pinned = pinned ? 1 : 0, now = NextTick() });
				loaded.Add(name);
			}
		});
		var evicted = Evict(budgetTokens, protect: loaded);
		return (loaded, evicted, missing);
	}

	public void Touch(string name) => store.Db.Write(u =>
		u.Execute("UPDATE tool_cache SET last_used=@now WHERE session_id=@sessionId AND tool_name=@name", new { sessionId, name, now = NextTick() }));

	public List<string> Evict(int budgetTokens, IReadOnlyCollection<string>? protect = null) {
		var evicted = new List<string>();
		var entries = Entries().OrderBy(e => e.LastUsed).ToList();
		var tokens = entries.Sum(e => registry.Get(e.Name) is { } t ? Schema.TokenEstimate(t) : 0);
		foreach(var e in entries) {
			if(tokens <= budgetTokens) break;
			if(e.Pinned || protect?.Contains(e.Name) == true) continue;
			tokens -= registry.Get(e.Name) is { } t ? Schema.TokenEstimate(t) : 0;
			evicted.Add(e.Name);
		}
		if(evicted.Count > 0)
			store.Db.Write(u => {
				foreach(var name in evicted) u.Execute("DELETE FROM tool_cache WHERE session_id=@sessionId AND tool_name=@name", new { sessionId, name });
			});
		return evicted;
	}

	public void CopyFrom(string otherSessionId) => store.Db.Write(u => u.Execute("""
		INSERT OR REPLACE INTO tool_cache(session_id,tool_name,tool_version,pinned,last_used)
		SELECT @sessionId, tool_name, tool_version, pinned, last_used FROM tool_cache WHERE session_id=@otherSessionId
		""", new { sessionId, otherSessionId }));
}
