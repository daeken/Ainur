# Ainur

Ainur is an agent orchestrator for autonomous teams working on substantial projects over long periods. Each project has an organization of managers and specialists, persistent agent identities, owned objectives, and visible dollar costs. The user works through the team's manager.

The runtime is a local C# and .NET 10 service with a React and TypeScript web UI that can develop, verify, and autonomously upgrade itself. Agents have dedicated execution threads and run tools inline, including an embedded PowerShell session that can call every other tool and pass live .NET objects.

The [project and runtime specification](docs/spec.md) records the product decisions and architecture; the [model catalog](docs/model-catalog.md) records the seeded models and prices.

## Running it

Prerequisites: the .NET 10 SDK and Node 20+. Model credentials come from host-managed sources and are never stored by Ainur: for DeepSeek, `DEEPSEEK_API_KEY` or the macOS keychain item `ai.deepseek.api` / `FlatlineProxy` (the same item FlatlineProxy uses).

Development (no supervisor, state in `.ainur/dev`):

```bash
(cd web && npm ci && npm run build)
dotnet run --project src/Ainur.Server -- --home .ainur/dev --port 5181
```

Then open http://127.0.0.1:5181, create a project with a workspace directory, and talk to its root manager.

Supervised, upgradeable install (state in `~/.ainur`, served on port 5180):

```bash
dotnet run --project src/Ainur.Supervisor -- run --source "$PWD"
```

The supervisor builds an immutable release into `~/.ainur/releases/`, runs exactly one runtime against the shared state, restarts it if it crashes or stops answering health checks, terminates it if an inline tool runs past its deadline, and performs upgrades requested by the team (drain → activate → probation → automatic rollback). `ainur-supervisor status` shows the active and previous releases.

Tests:

```bash
dotnet test tests/Ainur.Tests --filter "Category!=Live"   # deterministic, scripted provider
dotnet test tests/Ainur.Tests --filter "Category=Live"    # real DeepSeek requests (cents)
```

## What exists today

- **Persistence and recovery.** SQLite in WAL mode with current-state tables and an append-only journal committed in the same transactions; additive migrations; content-addressed artifacts for tool results and raw provider exchanges. On restart, interrupted tool invocations become explicit "outcome unknown" results (never blindly retried), in-flight model requests keep conservative estimated charges, and sessions resume.
- **Organization.** Projects (Arda) with one root manager, an acyclic reporting tree, persistent and ephemeral agents, agent-owned identity files with revision history, and an objective hierarchy with owners, dependencies (cycle-checked), completion conditions, evidence, per-objective spending, and manager acceptance of delegated work.
- **Execution.** A dedicated thread and serial dispatcher per live session; typed communication (assignment, result, decision, escalation) that wakes recipients; consultation forks that copy the consulted agent's full context into a temporary session with the same authority, run code work in an isolated git worktree (snapshot including uncommitted changes), and integrate back all-or-nothing; structured pause requests acknowledged at safe boundaries, released with a summary, and revoked on expiry; an inexpensive knowledge-service session for knowledge questions.
- **Providers.** A streaming OpenAI-compatible Chat Completions adapter that preserves reasoning content and tool-call identifiers, configured for DeepSeek (paid API) and Z.ai GLM (Coding Plan subscription). Both are live-verified, including delegation across the two. Other catalog models are recorded but have no adapter yet.
- **Accounting.** Every request is quoted, reserved against the project's effective budget and optional cash ceiling, and settled into exactly one cost event with separate cash and effective-dollar amounts. Subscription use carries no marginal cash but always a positive effective charge; account-wide quota windows (configured in `~/.ainur/quotas.json`) are reserved per window at admission, shared across projects, and valued as the maximum over overlapping windows.
- **Context management.** Rolling and full compaction with provenance, automatic elision of tool results after N turns, explicit elision and retention, bounded previews for oversized results, `read_result` and `read_history` to retrieve raw evidence, and system prompts kept stable between turns so provider prefix caching applies.
- **Tools.** Atomic `multi_edit` (every block must match or nothing is written), file tools, PowerShell as the only shell (every tool is a PowerShell function returning live .NET objects; session-owned object handles that are rejected after a restart), organization/objective/identity/knowledge/cost tools, MCP servers (configured in `~/.ainur/mcp.json`), a cheap-model tool finder with a token-budgeted LRU tool cache, agent-authored PowerShell tools with immutable versions, per-request tool version pinning, and release tools (`build_release`, `validate_release`, `activate_release`).
- **Supervisor.** Release selection, one runtime per home (OS file lock), crash restart with backoff, health checks that detect stalled schedulers, a deadline watchdog that terminates the runtime when an inline tool ignores cancellation (the invocation is reported, not retried), drain → activate → probation upgrades, automatic rollback with suppression of failed releases, and upgrade outcomes delivered to the requesting agent so its work resumes.
- **Web UI.** Project list, manager conversation with live streaming, org chart, objective tree with evidence and spending, costs (cash vs effective, quota windows, model catalog), activity journal, knowledge, runtime status, and per-agent identity, context view (compaction, elided results, loaded tools), and transcripts.

## Verified end to end

- A team (DeepSeek V4 Pro manager, V4 Flash implementer and verifier) built, independently verified, committed, released, validated, and activated two changes to Ainur itself (the `/api/v1/version` endpoint and its display in the UI) under the supervisor, then resumed after each restart and confirmed the change on the running instance. Both commits are in this history.
- A deliberately broken release was rolled back automatically and refused on re-activation; an uncooperative inline tool was terminated by the watchdog and reported as an unknown outcome without a restart loop.

## Known gaps

Not yet built: adapters for OpenAI, Anthropic, and xAI (and therefore live checks of all five providers); quota telemetry from providers (windows are configured, not observed); write-scope claims beyond pause requests; autonomous supervisor replacement. There is no permission model by design: agents have full access to the machine.
