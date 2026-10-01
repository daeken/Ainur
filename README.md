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

- **Persistence and recovery.** SQLite in WAL mode with current-state tables and an append-only journal committed in the same transactions; content-addressed artifacts for tool results and raw provider exchanges. On restart, interrupted tool invocations become explicit "outcome unknown" results (never blindly retried), in-flight model requests keep conservative estimated charges, and sessions resume.
- **Organization.** Projects (Arda) with one root manager, an acyclic reporting tree, persistent and ephemeral agents, agent-owned identity files with revision history, and an objective hierarchy with owners, dependencies (cycle-checked), completion conditions, evidence, and manager acceptance of delegated work.
- **Execution.** A dedicated thread and serial dispatcher per live session; typed communication (assignment, result, decision, escalation) that wakes recipients; consultation forks that copy the consulted agent's full context into a temporary session with the same authority; an inexpensive knowledge-service session for knowledge questions.
- **Provider.** A streaming DeepSeek Chat Completions adapter that preserves reasoning content and tool-call identifiers. Other catalog models are recorded but not yet usable.
- **Accounting.** Every request is quoted, reserved against the project's effective budget and optional cash ceiling, and settled into exactly one cost event with separate cash and effective-dollar amounts. Subscription valuation follows the spec's formula.
- **Context management.** Rolling and full compaction with provenance, automatic elision of tool results after N turns, explicit elision and retention, bounded previews for oversized results, and `read_result` to retrieve any excerpt of the raw evidence.
- **Tools.** Atomic `multi_edit` (every block must match or nothing is written), file tools, PowerShell as the only shell (every tool is a PowerShell function returning live objects), organization/objective/identity/knowledge/cost tools, a cheap-model tool finder with a token-budgeted LRU tool cache, agent-authored PowerShell tools with immutable versions, and release tools (`build_release`, `validate_release`, `activate_release`).
- **Web UI.** Project list, manager conversation with live streaming, org chart, objective tree with evidence, costs (cash vs effective), activity journal, knowledge, and per-agent identity, context view (compaction, elided results, loaded tools), and transcripts.

## Known gaps

See the implementation sequence in the spec. Not yet built: adapters for OpenAI, Anthropic, xAI, and Z.ai; MCP; subscription quota windows and cross-project quota reservations; write-scope claims and structured pause requests between forks and original sessions; isolated git worktrees for consultation forks; and autonomous supervisor replacement. There is no permission model by design: agents have full access to the machine.
