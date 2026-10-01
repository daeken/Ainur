using Dapper;
using Microsoft.Data.Sqlite;

namespace Ainur.Core.Persistence;

/// <summary>
/// SQLite in WAL mode. Writes are serialized through one gate so current-state rows and journal
/// events commit together; reads use pooled connections and see committed snapshots.
/// </summary>
public sealed class Db {
	readonly string ConnectionString;
	readonly SemaphoreSlim WriteGate = new(1, 1);
	public readonly string Path;

	public event Action<IReadOnlyList<JournalEvent>>? Committed;

	static Db() {
		DefaultTypeMap.MatchNamesWithUnderscores = true;
	}

	public Db(string path) {
		Path = path;
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
		ConnectionString = new SqliteConnectionStringBuilder {
			DataSource = path,
			Mode = SqliteOpenMode.ReadWriteCreate,
			Cache = SqliteCacheMode.Private,
			Pooling = true,
			DefaultTimeout = 30,
		}.ToString();
		using var conn = Open();
		conn.Execute("PRAGMA journal_mode=WAL;");
		Migrate(conn);
	}

	public SqliteConnection Open() {
		var conn = new SqliteConnection(ConnectionString);
		conn.Open();
		conn.Execute("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;");
		return conn;
	}

	public int SchemaVersion {
		get {
			using var conn = Open();
			return conn.ExecuteScalar<int>("PRAGMA user_version;");
		}
	}

	static void Migrate(SqliteConnection conn) {
		var current = conn.ExecuteScalar<int>("PRAGMA user_version;");
		var latest = Migrations.All.Max(m => m.Version);
		if(current > latest)
			// A newer release wrote this database. Additive migrations keep it readable; continue without migrating.
			return;
		foreach(var (version, _, sql) in Migrations.All.Where(m => m.Version > current).OrderBy(m => m.Version)) {
			using var tx = conn.BeginTransaction();
			conn.Execute(sql, transaction: tx);
			conn.Execute($"PRAGMA user_version = {version};", transaction: tx);
			tx.Commit();
		}
	}

	public T Read<T>(Func<SqliteConnection, T> fn) {
		using var conn = Open();
		return fn(conn);
	}

	/// <summary>Runs <paramref name="fn"/> in a write transaction. Journal events appended through the unit commit atomically with it.</summary>
	public T Write<T>(Func<Unit, T> fn) {
		WriteGate.Wait();
		List<JournalEvent> events;
		T result;
		try {
			using var conn = Open();
			using var tx = conn.BeginTransaction(deferred: false);
			var unit = new Unit(conn, tx);
			result = fn(unit);
			tx.Commit();
			events = unit.Events;
		} finally {
			WriteGate.Release();
		}
		if(events.Count > 0)
			Committed?.Invoke(events);
		return result;
	}

	public void Write(Action<Unit> fn) => Write(u => { fn(u); return 0; });

	public sealed class Unit(SqliteConnection conn, SqliteTransaction tx) {
		public readonly SqliteConnection Conn = conn;
		public readonly SqliteTransaction Tx = tx;
		internal readonly List<JournalEvent> Events = [];

		public int Execute(string sql, object? param = null) => Conn.Execute(sql, param, Tx);
		public T? Single<T>(string sql, object? param = null) => Conn.QuerySingleOrDefault<T>(sql, param, Tx);
		public T Scalar<T>(string sql, object? param = null) => Conn.ExecuteScalar<T>(sql, param, Tx)!;
		public List<T> Query<T>(string sql, object? param = null) => Conn.Query<T>(sql, param, Tx).AsList();

		/// <summary>Appends to the activity journal inside this transaction.</summary>
		public void Journal(string kind, string? projectId, string? entityType = null, string? entityId = null, string? agentId = null, object? payload = null) {
			var json = payload is null ? "{}" : Json.Serialize(payload);
			var now = Clock.Now;
			var id = Conn.ExecuteScalar<long>(
				"INSERT INTO events(project_id, kind, entity_type, entity_id, agent_id, payload, created_at) VALUES(@projectId, @kind, @entityType, @entityId, @agentId, @json, @now) RETURNING id",
				new { projectId, kind, entityType, entityId, agentId, json, now }, Tx);
			Events.Add(new JournalEvent { Id = id, ProjectId = projectId, Kind = kind, EntityType = entityType, EntityId = entityId, AgentId = agentId, Payload = json, CreatedAt = now });
		}
	}
}

public sealed class JournalEvent {
	public long Id { get; set; }
	public string? ProjectId { get; set; }
	public string Kind { get; set; } = "";
	public string? EntityType { get; set; }
	public string? EntityId { get; set; }
	public string? AgentId { get; set; }
	public string Payload { get; set; } = "{}";
	public long CreatedAt { get; set; }
}
