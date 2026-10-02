namespace Ainur.Core.Persistence;

/// <summary>
/// Ordered, additive schema migrations. Never edit a shipped migration; append a new one.
/// Releases must remain able to read state written by the next release during the compatibility window.
/// </summary>
public static class Migrations {
	public static readonly IReadOnlyList<(int Version, string Name, string Sql)> All = [
		(1, "foundation", """
			CREATE TABLE projects (
				id TEXT PRIMARY KEY,
				name TEXT NOT NULL,
				description TEXT NOT NULL DEFAULT '',
				workspace_path TEXT,
				root_agent_id TEXT,
				root_objective_id TEXT,
				state TEXT NOT NULL DEFAULT 'active',
				effective_budget_nanos INTEGER NOT NULL DEFAULT 0,
				cash_ceiling_nanos INTEGER,
				created_at INTEGER NOT NULL,
				updated_at INTEGER NOT NULL
			);

			CREATE TABLE agents (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL REFERENCES projects(id),
				name TEXT NOT NULL,
				title TEXT NOT NULL DEFAULT '',
				role TEXT NOT NULL,              -- manager | specialist
				lifetime TEXT NOT NULL,          -- persistent | ephemeral
				manager_id TEXT REFERENCES agents(id),
				model_id TEXT NOT NULL,
				reasoning_effort TEXT,
				state TEXT NOT NULL,             -- active | sleeping | working | paused | retired | terminated
				compaction_mode TEXT NOT NULL,   -- rolling | full
				instructions TEXT NOT NULL DEFAULT '',
				termination_condition TEXT,
				primary_session_id TEXT,
				created_by TEXT,
				created_at INTEGER NOT NULL,
				updated_at INTEGER NOT NULL,
				retired_at INTEGER
			);
			CREATE INDEX agents_project ON agents(project_id);
			CREATE INDEX agents_manager ON agents(manager_id);

			CREATE TABLE identity_revisions (
				id TEXT PRIMARY KEY,
				agent_id TEXT NOT NULL REFERENCES agents(id),
				revision INTEGER NOT NULL,
				content TEXT NOT NULL,
				author_agent_id TEXT,
				created_at INTEGER NOT NULL,
				UNIQUE(agent_id, revision)
			);

			CREATE TABLE objectives (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL REFERENCES projects(id),
				parent_id TEXT REFERENCES objectives(id),
				owner_id TEXT REFERENCES agents(id),
				delegated_by_id TEXT REFERENCES agents(id),
				title TEXT NOT NULL,
				description TEXT NOT NULL DEFAULT '',
				completion_conditions TEXT NOT NULL DEFAULT '',
				state TEXT NOT NULL,             -- planned | ready | active | blocked | verifying | complete | canceled
				required INTEGER NOT NULL DEFAULT 1,
				evidence TEXT NOT NULL DEFAULT '[]',
				sort_order INTEGER NOT NULL DEFAULT 0,
				created_at INTEGER NOT NULL,
				updated_at INTEGER NOT NULL
			);
			CREATE INDEX objectives_project ON objectives(project_id);
			CREATE INDEX objectives_parent ON objectives(parent_id);
			CREATE INDEX objectives_owner ON objectives(owner_id);

			CREATE TABLE objective_dependencies (
				objective_id TEXT NOT NULL REFERENCES objectives(id),
				depends_on_id TEXT NOT NULL REFERENCES objectives(id),
				PRIMARY KEY(objective_id, depends_on_id)
			);

			CREATE TABLE sessions (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL REFERENCES projects(id),
				agent_id TEXT NOT NULL REFERENCES agents(id),
				kind TEXT NOT NULL,              -- primary | consultation | service
				state TEXT NOT NULL,             -- idle | running | paused | finished
				model_id TEXT NOT NULL,
				compaction_mode TEXT NOT NULL,
				parent_session_id TEXT,
				checkpoint_seq INTEGER,
				turn_count INTEGER NOT NULL DEFAULT 0,
				next_seq INTEGER NOT NULL DEFAULT 1,
				context_revision INTEGER NOT NULL DEFAULT 0,
				token_ratio REAL NOT NULL DEFAULT 1.0,
				purpose TEXT,
				result TEXT,
				created_at INTEGER NOT NULL,
				updated_at INTEGER NOT NULL
			);
			CREATE INDEX sessions_agent ON sessions(agent_id);

			-- Immutable transcript. Context views select from it; nothing here is deleted by compaction or elision.
			CREATE TABLE session_items (
				id TEXT PRIMARY KEY,
				session_id TEXT NOT NULL REFERENCES sessions(id),
				seq INTEGER NOT NULL,
				kind TEXT NOT NULL,              -- user | assistant | tool_result | summary | notice
				turn INTEGER NOT NULL,           -- session turn count when the item was recorded
				payload TEXT NOT NULL,
				token_estimate INTEGER NOT NULL,
				created_at INTEGER NOT NULL,
				UNIQUE(session_id, seq)
			);

			-- Each change to what the next request sees commits a new revision.
			CREATE TABLE context_views (
				session_id TEXT NOT NULL REFERENCES sessions(id),
				revision INTEGER NOT NULL,
				state TEXT NOT NULL,             -- serialized ContextViewState
				reason TEXT NOT NULL,
				created_at INTEGER NOT NULL,
				PRIMARY KEY(session_id, revision)
			);

			CREATE TABLE compactions (
				id TEXT PRIMARY KEY,
				session_id TEXT NOT NULL REFERENCES sessions(id),
				mode TEXT NOT NULL,
				from_seq INTEGER NOT NULL,
				through_seq INTEGER NOT NULL,
				summary_item_id TEXT,
				model_request_id TEXT,
				source_tokens INTEGER NOT NULL,
				summary_tokens INTEGER,
				state TEXT NOT NULL,             -- running | succeeded | failed
				error TEXT,
				policy TEXT NOT NULL,
				created_at INTEGER NOT NULL,
				finished_at INTEGER
			);

			CREATE TABLE tool_invocations (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				session_id TEXT NOT NULL,
				agent_id TEXT NOT NULL,
				call_id TEXT NOT NULL,
				tool_name TEXT NOT NULL,
				tool_version TEXT NOT NULL,
				arguments TEXT NOT NULL,
				state TEXT NOT NULL,             -- queued | running | succeeded | failed | canceled | unknown
				result_artifact TEXT,
				result_chars INTEGER,
				error TEXT,
				parent_invocation_id TEXT,
				deadline_at INTEGER,
				started_at INTEGER,
				finished_at INTEGER,
				created_at INTEGER NOT NULL
			);
			CREATE INDEX tool_invocations_session ON tool_invocations(session_id);

			CREATE TABLE model_requests (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				session_id TEXT,
				agent_id TEXT,
				objective_id TEXT,
				purpose TEXT NOT NULL,           -- turn | compaction | knowledge | tool_finder | other
				model_id TEXT NOT NULL,
				provider TEXT NOT NULL,
				upstream_model TEXT NOT NULL,
				state TEXT NOT NULL,             -- dispatched | succeeded | failed | unknown
				quote TEXT NOT NULL,
				usage TEXT,
				request_artifact TEXT,
				response_artifact TEXT,
				error TEXT,
				context_revision INTEGER,
				started_at INTEGER NOT NULL,
				finished_at INTEGER
			);
			CREATE INDEX model_requests_project ON model_requests(project_id);

			CREATE TABLE cost_events (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				objective_id TEXT,
				agent_id TEXT,
				sponsor_agent_id TEXT,
				session_id TEXT,
				model_request_id TEXT,
				category TEXT NOT NULL,          -- direct | consultation | knowledge | compaction | tool_finder
				cash_nanos INTEGER,              -- null when unknown
				cash_basis TEXT NOT NULL,        -- usage_priced | estimated | invoice | unknown
				effective_nanos INTEGER NOT NULL,
				valuation TEXT,
				created_at INTEGER NOT NULL
			);
			CREATE INDEX cost_events_project ON cost_events(project_id);
			CREATE INDEX cost_events_agent ON cost_events(agent_id);

			CREATE TABLE reservations (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				model_request_id TEXT,
				effective_nanos INTEGER NOT NULL,
				cash_nanos INTEGER NOT NULL,
				state TEXT NOT NULL,             -- held | settled | released
				created_at INTEGER NOT NULL,
				settled_at INTEGER
			);

			CREATE TABLE notifications (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				type TEXT NOT NULL,              -- assignment | consultation | result | decision | pause | resume | escalation | user_message | system
				from_agent_id TEXT,
				to_agent_id TEXT NOT NULL,
				objective_id TEXT,
				body TEXT NOT NULL,
				wakes INTEGER NOT NULL,
				state TEXT NOT NULL,             -- pending | delivered
				causal_parent_id TEXT,
				dedupe_key TEXT UNIQUE,
				created_at INTEGER NOT NULL,
				delivered_at INTEGER
			);
			CREATE INDEX notifications_pending ON notifications(to_agent_id, state);

			CREATE TABLE conversation (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				author TEXT NOT NULL,            -- user | manager
				agent_id TEXT,
				body TEXT NOT NULL,
				created_at INTEGER NOT NULL
			);
			CREATE INDEX conversation_project ON conversation(project_id, created_at);

			CREATE TABLE models (
				id TEXT PRIMARY KEY,
				provider TEXT NOT NULL,
				upstream_model TEXT NOT NULL,
				display_name TEXT NOT NULL,
				context_tokens INTEGER,
				max_output_tokens INTEGER,
				input_per_million TEXT,
				cached_input_per_million TEXT,
				output_per_million TEXT,
				price_provenance TEXT NOT NULL,
				billing TEXT NOT NULL,           -- api | subscription
				premium TEXT NOT NULL DEFAULT '1',
				enabled INTEGER NOT NULL,
				notes TEXT NOT NULL DEFAULT ''
			);

			CREATE TABLE knowledge_revisions (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				doc_key TEXT NOT NULL,
				revision INTEGER NOT NULL,
				kind TEXT NOT NULL,              -- requirement | decision | proposal | observation | reference | superseded
				title TEXT NOT NULL,
				content TEXT NOT NULL,
				author_agent_id TEXT,
				provenance TEXT NOT NULL DEFAULT '',
				objective_id TEXT,
				replaces_revision_id TEXT,
				created_at INTEGER NOT NULL,
				UNIQUE(project_id, doc_key, revision)
			);
			CREATE VIRTUAL TABLE knowledge_fts USING fts5(revision_id UNINDEXED, project_id UNINDEXED, doc_key, title, content);

			CREATE TABLE tool_versions (
				id TEXT PRIMARY KEY,             -- name@hash
				name TEXT NOT NULL,
				kind TEXT NOT NULL,              -- builtin | powershell | assembly | mcp
				project_id TEXT,
				description TEXT NOT NULL,
				input_schema TEXT NOT NULL,
				source TEXT,
				created_by TEXT,
				state TEXT NOT NULL DEFAULT 'active', -- active | quarantined | superseded
				created_at INTEGER NOT NULL
			);

			CREATE TABLE tool_cache (
				session_id TEXT NOT NULL,
				tool_name TEXT NOT NULL,
				tool_version TEXT NOT NULL,
				pinned INTEGER NOT NULL,
				last_used INTEGER NOT NULL,
				PRIMARY KEY(session_id, tool_name)
			);

			CREATE TABLE runtime_generations (
				generation INTEGER PRIMARY KEY,
				release TEXT,
				started_at INTEGER NOT NULL,
				stopped_at INTEGER,
				stop_reason TEXT
			);

			CREATE TABLE releases (
				id TEXT PRIMARY KEY,
				path TEXT NOT NULL,
				source_revision TEXT,
				state TEXT NOT NULL,             -- built | validated | active | failed | retired
				notes TEXT NOT NULL DEFAULT '',
				created_at INTEGER NOT NULL
			);

			CREATE TABLE upgrade_attempts (
				id TEXT PRIMARY KEY,
				release_id TEXT NOT NULL,
				previous_release_id TEXT,
				state TEXT NOT NULL,             -- requested | draining | activating | probation | succeeded | rolled_back | aborted
				detail TEXT NOT NULL DEFAULT '',
				created_at INTEGER NOT NULL,
				updated_at INTEGER NOT NULL
			);

			CREATE TABLE events (
				id INTEGER PRIMARY KEY AUTOINCREMENT,
				project_id TEXT,
				kind TEXT NOT NULL,
				entity_type TEXT,
				entity_id TEXT,
				agent_id TEXT,
				payload TEXT NOT NULL,
				created_at INTEGER NOT NULL
			);
			CREATE INDEX events_project ON events(project_id, id);
			"""),
		(2, "pause_requests", """
			-- Structured pause requests (e.g. from a consultation fork to its original session), enforced at step boundaries.
			CREATE TABLE pause_requests (
				id TEXT PRIMARY KEY,
				project_id TEXT NOT NULL,
				requester_session_id TEXT NOT NULL,
				requester_agent_id TEXT NOT NULL,
				target_session_id TEXT NOT NULL,
				scope TEXT NOT NULL,
				reason TEXT NOT NULL,
				release_condition TEXT NOT NULL,
				generation INTEGER NOT NULL,
				state TEXT NOT NULL,             -- requested | acknowledged | released | expired
				created_at INTEGER NOT NULL,
				acknowledged_at INTEGER,
				released_at INTEGER,
				expires_at INTEGER NOT NULL
			);
			CREATE INDEX pause_requests_target ON pause_requests(target_session_id, state);
			"""),
		(3, "session_worktrees", """
			-- Consultation forks of code work run in isolated git worktrees materialized from the original's snapshot.
			ALTER TABLE sessions ADD COLUMN workspace_path TEXT;
			ALTER TABLE sessions ADD COLUMN worktree_base TEXT;
			"""),
		(4, "request_tool_bindings", """
			-- Exact tool name → version bindings declared to the model for each request; returned calls resolve against them.
			ALTER TABLE model_requests ADD COLUMN tool_bindings TEXT;
			"""),
		(5, "quota_usage", """
			-- Account-wide subscription quota consumption per window cycle, shared across projects.
			CREATE TABLE quota_usage (
				id TEXT PRIMARY KEY,
				window_id TEXT NOT NULL,
				cycle_start INTEGER NOT NULL,
				model_request_id TEXT NOT NULL,
				units INTEGER NOT NULL,
				scarcity TEXT NOT NULL DEFAULT '1', -- frozen at quote time
				state TEXT NOT NULL,             -- held | committed | released
				created_at INTEGER NOT NULL
			);
			CREATE INDEX quota_usage_window ON quota_usage(window_id, cycle_start, state);
			CREATE INDEX quota_usage_request ON quota_usage(model_request_id);
			"""),
		(6, "model_fallback", """
			-- Model-level fallback: when a request fails without billing, the gateway retries this model (its own row + quote).
			ALTER TABLE models ADD COLUMN fallback_model_id TEXT;
			"""),
		(7, "conversation_images", """
			CREATE TABLE conversation_images (
				id TEXT PRIMARY KEY, project_id TEXT NOT NULL REFERENCES projects(id),
				session_id TEXT NOT NULL REFERENCES sessions(id), artifact TEXT NOT NULL,
				mime_type TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
				bytes INTEGER NOT NULL, created_at INTEGER NOT NULL, expires_at INTEGER NOT NULL,
				conversation_id TEXT REFERENCES conversation(id), attachment_order INTEGER, deleted_at INTEGER
			);
			CREATE INDEX conversation_images_scope ON conversation_images(project_id, conversation_id);
			CREATE TABLE conversation_receipts (
				project_id TEXT NOT NULL REFERENCES projects(id), client_message_id TEXT NOT NULL,
				fingerprint TEXT NOT NULL, conversation_id TEXT NOT NULL REFERENCES conversation(id),
				PRIMARY KEY(project_id,client_message_id)
			);
			"""),
	];
}
