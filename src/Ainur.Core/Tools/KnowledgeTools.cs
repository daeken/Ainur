using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ainur.Core.Context;
using Ainur.Core.Model;
using Ainur.Core.Persistence;
using Dapper;

namespace Ainur.Core.Tools;

public static class Knowledge {
	public static readonly string[] Kinds = ["requirement", "decision", "proposal", "observation", "reference", "superseded"];

	public static string FtsQuery(string query) {
		var terms = Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}_]{2,}").Select(m => m.Value).Distinct().Take(16).ToList();
		return terms.Count == 0 ? "\"\"" : string.Join(" OR ", terms.Select(t => $"\"{t}\""));
	}

	public static KnowledgeRevision? Latest(Store store, string projectId, string key) => store.Db.Read(c => c.QuerySingleOrDefault<KnowledgeRevision>(
		"SELECT * FROM knowledge_revisions WHERE project_id=@projectId AND doc_key=@key ORDER BY revision DESC LIMIT 1", new { projectId, key }));

	public static List<(KnowledgeRevision Rev, string Snippet)> Search(Store store, string projectId, string query, int limit) => store.Db.Read(c => c.Query<KnowledgeRevision, string, (KnowledgeRevision, string)>("""
		SELECT r.*, snippet(knowledge_fts, 4, '[', ']', '…', 24) AS snippet
		FROM knowledge_fts f JOIN knowledge_revisions r ON r.id = f.revision_id
		WHERE knowledge_fts MATCH @q AND f.project_id = @projectId
		ORDER BY bm25(knowledge_fts) LIMIT @limit
		""", (r, s) => (r, s), new { q = FtsQuery(query), projectId, limit }, splitOn: "snippet").AsList());

	public static KnowledgeRevision Write(Store store, string projectId, string key, string title, string kind, string content, string? authorId, string provenance, string? objectiveId, int? expectedRevision) {
		if(!Kinds.Contains(kind)) throw new ToolException($"kind must be one of {string.Join(", ", Kinds)}");
		return store.Db.Write(u => {
			var current = u.Single<KnowledgeRevision>("SELECT * FROM knowledge_revisions WHERE project_id=@projectId AND doc_key=@key ORDER BY revision DESC LIMIT 1", new { projectId, key });
			if(current is not null && expectedRevision != current.Revision)
				throw new ToolException($"Revision conflict on '{key}': current revision is {current.Revision}{(expectedRevision is null ? " (pass expected_revision to update an existing document)" : $", you based your edit on {expectedRevision}")}. Re-read and reconcile.");
			var rev = new KnowledgeRevision {
				Id = Ids.New("kr"), ProjectId = projectId, DocKey = key, Revision = (current?.Revision ?? 0) + 1, Kind = kind, Title = title, Content = content,
				AuthorAgentId = authorId, Provenance = provenance, ObjectiveId = objectiveId, ReplacesRevisionId = current?.Id, CreatedAt = Clock.Now,
			};
			u.Execute("""
				INSERT INTO knowledge_revisions(id,project_id,doc_key,revision,kind,title,content,author_agent_id,provenance,objective_id,replaces_revision_id,created_at)
				VALUES(@Id,@ProjectId,@DocKey,@Revision,@Kind,@Title,@Content,@AuthorAgentId,@Provenance,@ObjectiveId,@ReplacesRevisionId,@CreatedAt)
				""", rev);
			u.Execute("DELETE FROM knowledge_fts WHERE project_id=@projectId AND doc_key=@key", new { projectId, key });
			u.Execute("INSERT INTO knowledge_fts(revision_id, project_id, doc_key, title, content) VALUES(@Id, @ProjectId, @DocKey, @Title, @Content)", rev);
			u.Journal("knowledge.revised", projectId, "knowledge", key, authorId, new { rev.Revision, rev.Kind, rev.Title, replaces = current?.Id });
			return rev;
		});
	}
}

public sealed class KnowledgeSearchTool : BuiltinTool {
	public override string Name => "knowledge_search";
	public override string Description => "Full-text search of the project's versioned knowledge store. Returns document keys, revision ids, kinds, and matching passages.";
	public override IReadOnlyList<string> Tags => ["knowledge", "search", "docs", "decisions", "requirements", "memory"];
	public override JsonObject InputSchema => Schema.Object(("query", Schema.String("Search terms."), true), ("limit", Schema.Integer("Max results (default 10)."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var hits = Knowledge.Search(ctx.Runtime.Store, ctx.Project.Id, Str(args, "query"), OptInt(args, "limit") ?? 10);
		if(hits.Count == 0) return Task.FromResult(ToolResult.Ok("[no matching knowledge documents]", description: "knowledge_search: none"));
		var sb = new StringBuilder();
		foreach(var (r, snippet) in hits)
			sb.Append($"- {r.DocKey} r{r.Revision} [{r.Kind}] \"{r.Title}\" (revision id {r.Id}): {snippet.ReplaceLineEndings(" ")}\n");
		return Task.FromResult(ToolResult.Ok(sb.ToString(), description: $"knowledge_search: {hits.Count} hits"));
	}
}

public sealed class KnowledgeReadTool : BuiltinTool {
	public override string Name => "knowledge_read";
	public override string Description => "Read a knowledge document by key (latest revision or a specific one), with its provenance and revision history.";
	public override IReadOnlyList<string> Tags => ["knowledge", "read", "docs"];
	public override JsonObject InputSchema => Schema.Object(("key", Schema.String("Document key."), true), ("revision", Schema.Integer("Revision number."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var key = Str(args, "key");
		var revs = ctx.Runtime.Store.Db.Read(c => c.Query<KnowledgeRevision>("SELECT * FROM knowledge_revisions WHERE project_id=@p AND doc_key=@key ORDER BY revision", new { p = ctx.Project.Id, key }).AsList());
		if(revs.Count == 0) throw new ToolException($"No knowledge document '{key}'");
		var r = OptInt(args, "revision") is { } n ? revs.FirstOrDefault(x => x.Revision == n) ?? throw new ToolException($"No revision {n}") : revs[^1];
		var author = r.AuthorAgentId is null ? "unknown" : ctx.Runtime.AgentLabel(r.AuthorAgentId);
		return Task.FromResult(ToolResult.Ok($"{r.DocKey} revision {r.Revision} of {revs.Count} [{r.Kind}] \"{r.Title}\" (id {r.Id})\nAuthor: {author}; provenance: {r.Provenance}\n\n{r.Content}", description: $"knowledge {key} r{r.Revision}"));
	}
}

public sealed class KnowledgeWriteTool : BuiltinTool {
	public override string Name => "knowledge_write";
	public override string Description => """
		Create or revise a shared knowledge document. Kinds: requirement, decision, proposal, observation, reference, superseded.
		Updating an existing document requires expected_revision (conflicts are rejected). Changing an accepted decision creates a
		traceable replacement under your own authority. Include provenance (sources, commits, file:line).
		""";
	public override IReadOnlyList<string> Tags => ["knowledge", "write", "document", "decision", "record"];
	public override JsonObject InputSchema => Schema.Object(
		("key", Schema.String("Stable document key, e.g. decisions/storage-engine."), true),
		("title", Schema.String("Title."), true),
		("kind", Schema.String("Entry kind.", Knowledge.Kinds), true),
		("content", Schema.String("Complete document content (markdown)."), true),
		("provenance", Schema.String("Sources and evidence."), false),
		("expected_revision", Schema.Integer("Current revision when updating."), false),
		("objective_id", Schema.String("Related objective."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var rev = Knowledge.Write(ctx.Runtime.Store, ctx.Project.Id, Str(args, "key"), Str(args, "title"), Str(args, "kind"), Str(args, "content"),
			ctx.Agent.Id, OptStr(args, "provenance") ?? "", OptStr(args, "objective_id"), OptInt(args, "expected_revision"));
		return Task.FromResult(ToolResult.Ok($"Saved {rev.DocKey} revision {rev.Revision} (id {rev.Id}).", description: $"knowledge_write {rev.DocKey} r{rev.Revision}"));
	}
}

/// <summary>Starts an inexpensive service session to research or edit knowledge; the answer arrives as a result message.</summary>
public sealed class AskKnowledgeTool : BuiltinTool {
	public override string Name => "ask_knowledge";
	public override string Description => """
		Ask the knowledge service: an inexpensive model searches project knowledge (and can read workspace files) to answer a
		question with exact references, or performs a requested knowledge edit. Runs in the background; the answer arrives in
		your inbox as a result message. Costs are charged to your work.
		""";
	public override IReadOnlyList<string> Tags => ["knowledge", "ask", "research", "question", "service"];
	public override JsonObject InputSchema => Schema.Object(
		("request", Schema.String("The question or intended edit, with any context the worker needs."), true),
		("objective_id", Schema.String("Related objective."), false));
	public override Task<ToolResult> InvokeAsync(ToolContext ctx, JsonObject args) {
		var s = ctx.Runtime.StartServiceSession(ctx.Agent.Id, "knowledge", Str(args, "request"), OptStr(args, "objective_id"));
		return Task.FromResult(ToolResult.Ok($"Knowledge service started (session {s.Id}); the answer will arrive in your inbox.", description: "ask_knowledge"));
	}
}
