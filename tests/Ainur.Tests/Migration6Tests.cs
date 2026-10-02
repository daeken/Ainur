using Ainur.Core.Accounting;
using Ainur.Core.Persistence;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Ainur.Tests;

public class Migration6Tests {
	static string TempDb() => Path.Combine(Path.GetTempPath(), "ainur-mig-" + Guid.NewGuid().ToString("N") + ".db");

	static bool HasFallbackColumn(string path) {
		using var conn = new SqliteConnection($"Data Source={path}");
		conn.Open();
		using var cmd = conn.CreateCommand();
		cmd.CommandText = "PRAGMA table_info(models);";
		using var r = cmd.ExecuteReader();
		while(r.Read())
			if(r.GetString(1) == "fallback_model_id") return true;
		return false;
	}

	[Fact]
	public void FreshDatabaseGetsMigration6AndSeededFallbackLinkage() {
		var path = TempDb();
		try {
			var db = new Db(path);
			Assert.Equal(7, db.SchemaVersion);
			Assert.True(HasFallbackColumn(path));

			var store = new Store(db);
			ModelCatalog.EnsureSeeded(store);
			var astra = store.GetModel("gpt-6-astra");
			Assert.NotNull(astra);
			Assert.Equal("gpt-6-astra-api", astra.FallbackModelId);
			Assert.True(astra.Enabled);
			Assert.Equal("subscription", astra.Billing);
			var twin = store.GetModel("gpt-6-astra-api");
			Assert.NotNull(twin);
			Assert.Equal("api", twin.Billing);
			// Honest pricing: no invented per-token numbers.
			Assert.Null(twin.InputPerMillion);
		} finally {
			DbCleanup(path);
		}
	}

	[Fact]
	public void UpgradingExistingPopulatedDatabasePreservesRowsAndAddsColumn() {
		var path = TempDb();
		try {
			// Simulate a release-5 database: apply migrations 1..5 by hand, insert historical rows.
			using(var conn = new SqliteConnection($"Data Source={path}")) {
				conn.Open();
				foreach(var (version, _, sql) in Migrations.All.Where(m => m.Version <= 5).OrderBy(m => m.Version)) {
					using var tx = conn.BeginTransaction();
					using var cmd = conn.CreateCommand();
					cmd.Transaction = tx;
					cmd.CommandText = sql;
					cmd.ExecuteNonQuery();
					using var v = conn.CreateCommand();
					v.Transaction = tx;
					v.CommandText = $"PRAGMA user_version = {version};";
					v.ExecuteNonQuery();
					tx.Commit();
				}
				// A pre-existing subscription row with its recorded (null) prices and enabled flag, plus history.
				using var ins = conn.CreateCommand();
				ins.CommandText = "INSERT INTO models(id,provider,upstream_model,display_name,price_provenance,billing,premium,enabled,notes) VALUES('gpt-6','openai','gpt-6','GPT 6','none','subscription','1',1,'old note')";
				ins.ExecuteNonQuery();
				using var req = conn.CreateCommand();
				req.CommandText = "INSERT INTO model_requests(id,project_id,purpose,model_id,provider,upstream_model,state,quote,started_at) VALUES('req-old','p1','test','gpt-6','openai','gpt-6','succeeded','{}',0)";
				req.ExecuteNonQuery();
				using var cost = conn.CreateCommand();
				cost.CommandText = "INSERT INTO cost_events(id,project_id,model_request_id,category,effective_nanos,cash_basis,created_at) VALUES('cost-old','p1','req-old','direct',1000,'subscription',0)";
				cost.ExecuteNonQuery();
			}

			// Reopen through the real Db: migrations 6 (fallback) and 7 (images) run additively.
			var db = new Db(path);
			Assert.Equal(7, db.SchemaVersion);
			Assert.True(HasFallbackColumn(path));

			var store = new Store(db);
			var existing = store.GetModel("gpt-6");
			Assert.NotNull(existing);
			Assert.Equal(1, existing.Enabled ? 1 : 0);
			Assert.Null(existing.InputPerMillion); // prices untouched
			Assert.Equal("old note", existing.Notes); // only the new column is added; existing columns untouched
			Assert.Null(existing.FallbackModelId); // no linkage until reseeded

			// Re-run the seed: historical rows keep their values, fallback linkage is populated, twins appear.
			ModelCatalog.EnsureSeeded(store);
			var reseeded = store.GetModel("gpt-6");
			Assert.NotNull(reseeded);
			Assert.Equal("gpt-6-api", reseeded!.FallbackModelId);
			Assert.True(reseeded.Enabled);
			Assert.NotNull(store.GetModel("gpt-6-api"));

			// History untouched.
			Assert.Equal(1, db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM model_requests WHERE id='req-old'")));
			Assert.Equal(1, db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM cost_events WHERE id='cost-old'")));
		} finally {
			DbCleanup(path);
		}
	}

	[Fact]
	public void Schema6To7PreservesFallbackPricesHistoryAndLegacyConversationProjection() {
		var path = TempDb();
		try {
			using(var conn = new SqliteConnection($"Data Source={path}")) {
				conn.Open();
				foreach(var migration in Migrations.All.Where(m => m.Version <= 6)) conn.Execute(migration.Sql);
				conn.Execute("PRAGMA user_version=6;");
				conn.Execute("INSERT INTO models(id,provider,upstream_model,display_name,price_provenance,billing,premium,enabled,notes,fallback_model_id) VALUES('custom','openai','custom','Custom','none','subscription','1',1,'preserve me','custom-api')");
				conn.Execute("INSERT INTO conversation(id,project_id,author,body,created_at) VALUES('legacy','p','user','old text',1)");
				conn.Execute("INSERT INTO model_requests(id,project_id,purpose,model_id,provider,upstream_model,state,quote,started_at) VALUES('old','p','test','custom','openai','custom','succeeded','{}',1)");
				conn.Execute("INSERT INTO cost_events(id,project_id,model_request_id,category,effective_nanos,cash_basis,created_at) VALUES('cost','p','old','direct',123,'subscription',1)");
			}
			var db = new Db(path); Assert.Equal(7, db.SchemaVersion);
			var store = new Store(db);
			var model = store.GetModel("custom")!;
			Assert.Equal("custom-api", model.FallbackModelId); Assert.Null(model.InputPerMillion); Assert.True(model.Enabled);
			Assert.Equal("preserve me", model.Notes);
			var legacy = Assert.Single(store.Conversation("p"));
			Assert.Equal("old text", legacy.Body); Assert.Empty(legacy.Attachments);
			Assert.Equal(123, db.Read(c => c.ExecuteScalar<long>("SELECT effective_nanos FROM cost_events WHERE id='cost'")));
			Assert.Equal("succeeded", db.Read(c => c.ExecuteScalar<string>("SELECT state FROM model_requests WHERE id='old'")));
			Assert.Equal(0, db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM conversation_images")));
			Assert.Equal(0, db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM conversation_receipts")));
			// The prior release's legacy SQL remains usable with the additive tables present.
			db.Write(u => u.Execute("INSERT INTO conversation(id,project_id,author,body,created_at) VALUES('legacy-new','p','user','old-writer text',2)"));
			Assert.Equal(2, new Store(new Db(path)).Conversation("p").Count);
			Assert.Equal(7, new Db(path).SchemaVersion); // Reopening does not replay migration 7.
		} finally { DbCleanup(path); }
	}

	static void DbCleanup(string path) {
		SqliteConnection.ClearAllPools();
		foreach(var f in new[] { path, path + "-wal", path + "-shm" })
			if(File.Exists(f)) File.Delete(f);
	}
}
