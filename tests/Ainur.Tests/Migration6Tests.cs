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
	public void FreshDatabaseGetsMigration6AndDisabledHistoricalApiTwins() {
		var path = TempDb();
		try {
			var db = new Db(path);
			Assert.Equal(14, db.SchemaVersion); // includes real registrationIntent13 + exactReviewPurpose14
			Assert.True(HasFallbackColumn(path));

			var store = new Store(db);
			ModelCatalog.EnsureSeeded(store);
			var astra = store.GetModel("gpt-6-astra");
			Assert.NotNull(astra);
			Assert.Null(astra.FallbackModelId);
			Assert.True(astra.Enabled);
			Assert.Equal("subscription", astra.Billing);
			var twin = store.GetModel("gpt-6-astra-api");
			Assert.NotNull(twin);
			Assert.Equal("api", twin.Billing);
			Assert.False(twin.Enabled);
			Assert.Contains("Historical-only", twin.Notes);
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

			// Reopen through the real Db: migration 6 runs additively.
			var db = new Db(path);
			Assert.Equal(14, db.SchemaVersion); // includes real registrationIntent13 + exactReviewPurpose14
			Assert.True(HasFallbackColumn(path));

			var store = new Store(db);
			var existing = store.GetModel("gpt-6");
			Assert.NotNull(existing);
			Assert.Equal(1, existing.Enabled ? 1 : 0);
			Assert.Null(existing.InputPerMillion); // prices untouched
			Assert.Equal("old note", existing.Notes); // only the new column is added; existing columns untouched
			Assert.Null(existing.FallbackModelId); // no linkage until reseeded

			// Re-run the seed: historical requests and costs remain; prohibited API twins are disabled.
			ModelCatalog.EnsureSeeded(store);
			var reseeded = store.GetModel("gpt-6");
			Assert.NotNull(reseeded);
			Assert.Null(reseeded!.FallbackModelId);
			Assert.True(reseeded.Enabled);
			Assert.False(store.GetModel("gpt-6-api")!.Enabled);

			// A normal restart re-seeds old catalog values in place; cost/request rows are not deleted.
			var staleApi = store.GetModel("gpt-6-api")!;
			staleApi.Enabled = true;
			staleApi.Notes = "Old selectable paid route";
			db.Write(u => store.UpsertModel(u, staleApi));
			var staleSubscription = store.GetModel("gpt-6")!;
			staleSubscription.FallbackModelId = "gpt-6-api";
			staleSubscription.Notes = "Old paid fallback note";
			db.Write(u => store.UpsertModel(u, staleSubscription));
			ModelCatalog.EnsureSeeded(store);
			Assert.False(store.GetModel("gpt-6-api")!.Enabled);
			Assert.Contains("Historical-only", store.GetModel("gpt-6-api")!.Notes);
			Assert.Null(store.GetModel("gpt-6")!.FallbackModelId);
			Assert.DoesNotContain("fallback", store.GetModel("gpt-6")!.Notes, StringComparison.OrdinalIgnoreCase);
			Assert.True(store.GetModel("deepseek-v4.1-flash")!.Enabled); // unrelated provider untouched

			// History untouched.
			Assert.Equal(1, db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM model_requests WHERE id='req-old'")));
			Assert.Equal(1, db.Read(c => c.ExecuteScalar<int>("SELECT COUNT(*) FROM cost_events WHERE id='cost-old'")));
		} finally {
			DbCleanup(path);
		}
	}

	static void DbCleanup(string path) {
		SqliteConnection.ClearAllPools();
		foreach(var f in new[] { path, path + "-wal", path + "-shm" })
			if(File.Exists(f)) File.Delete(f);
	}
}
