# Ainur

Ainur is a local agent-development platform for autonomous teams working on substantial projects over long periods. Each project (an **Arda**) has a manager, specialists, persistent identities, owned objectives, durable context and visible costs. You work through the team's manager; the team can write code, create its own tools, delegate work and propose upgrades to Ainur itself.

The runtime is C# on **.NET 10**, with a React/TypeScript web UI, SQLite storage and embedded PowerShell. **Use the supervisor for long-running work and release upgrades.** Direct server execution is useful for development, but does not provide crash/deadline recovery or supervised release switching.

> **Trusted-machine software, not a sandbox.** Agents execute tools with your OS account's authority. The HTTP service is loopback-only; do not expose it as a public or multi-tenant service. Use a dedicated account or machine when appropriate, protect its credentials, and review spending and proposed changes.

## Source status and release boundaries

This README describes the checked-in source, not whatever process happens to be running. Publishing or checking out a commit does **not** deploy it. Check the running `/api/v1/version`, `/api/v1/health`, route policy and supervisor release state before assuming a fix is active. An on-disk DLL hash is not in-memory attestation for a process that predates a rebuild; contain unverifiable runtimes before admitting work.

For reproducibility, use a clean worktree pinned to an independently reviewed **full commit SHA**, not a moving branch name. Do not combine unreviewed worktree changes into a release. Release builds, schema upgrades, deployment and live provider/browser verification remain separate operator gates. The feature contracts below describe source scope and limitations; they are not proof of installation or successful live execution.

## What exists today

- **Organization and durable work.** Root managers and reporting trees; owned objectives with dependencies, evidence and delegated-work acceptance; versioned identities and knowledge; persistent and temporary agents; consultation forks with isolated git worktrees.
- **Execution and tools.** Dedicated serial session execution, typed inter-agent notifications, PowerShell as the tool shell, live .NET object handles, atomic file edits, MCP integration, tool discovery and agent-authored versioned PowerShell tools. Release tools can build, validate and request activation. There is no tool permission sandbox.
- **Persistence and context.** SQLite WAL with transactional journals, content-addressed artifacts, rolling/full compaction, tool-result elision and raw-history retrieval. Interrupted tools recover as **outcome unknown**, not blind retries. In-flight model requests retain conservative accounting. Object handles do not survive process restarts.
- **Providers and reasoning.** OpenAI **Responses** supports ChatGPT/Codex subscription authentication and a separately configured paid API route. DeepSeek and Z.ai use streaming Chat Completions. Provider adapters preserve tool-call identifiers and supported reasoning content/effort; effort names and capabilities are model-specific, not a universal guarantee that every model accepts `max`. The catalog contains GPT-6 Astra/Sol, DeepSeek and GLM routes as well as entries without adapters. Anthropic and xAI adapters are not implemented. Seeded availability/prices are not a live provider guarantee; inspect the actual catalog, since existing databases retain operator edits. Native OpenAI `web_search` serialization is opt-in per request, not automatically enabled for every agent turn.
- **Accounting.** Effective-dollar valuation is distinct from cash. Projects may have an explicit unlimited effective budget while retaining a separate cash ceiling. Quotes, reservations and settlement remain recorded; subscription valuation is not an invoice. Unknown cash pricing is not zero and cannot safely be treated as free. Quota windows are configured, not fetched from provider telemetry. See [budget controls](docs/budget-controls.md) and the routing warning below.
- **Browser and images.** CDP browser tools, sessions and screenshot artifacts exist in source; read the [browser contract](docs/browser.md) and [image contract](docs/conversation-images.md) for limitations. A screenshot artifact or UI preview alone does not mean the model saw pixels. Durable authorized user/tool image inputs are supported on compatible OpenAI Responses vision routes; unsupported routes reject rather than silently drop pixels. Uploads are **PNG only**, at most **2 MiB**, **2048 pixels per edge**, **3,000,000 pixels**, and **two attachments per message**; JPEG/WebP, animation and arbitrary remote-image URLs are not supported by that upload contract. Live visual-understanding verification remains a separate gate.
- **Web UI.** Manager conversation and streaming, organization, objectives/evidence, accounting, journal, knowledge, runtime status and agent identity/context/transcripts. Conversation uploads are available in source; do not infer that they are present in the running server.
- **Supervisor.** Immutable runtime releases, an OS scheduler lock per home, crash backoff, readiness checks, deadline watchdog, drain/activate/probation and rollback attempts. These are recovery mechanisms, not a promise that every cancellation or failed drain recovers automatically; see the operator caveats below.

## Credentials and spending safety

Configure credentials in the environment of the **supervisor process** (its runtime children inherit them), or through the supported host credential store. Do not put secrets into source, prompts, artifacts or repository files.

| Provider | Host-managed source |
| --- | --- |
| OpenAI subscription | Codex OAuth tokens in `~/.codex/auth.json`; refresh uses atomic write-back. Treat this file as a credential, not an Ainur configuration example. |
| OpenAI API | `OPENAI_API_KEY`, or macOS keychain service `ai.openai.api` |
| DeepSeek | `DEEPSEEK_API_KEY`, or keychain service `ai.deepseek.api`, account `FlatlineProxy` |
| Z.ai | `ZAI_API_KEY`, or keychain service `ai.z.api`, account `FlatlineProxy` |

The resolver can also use credential-source names declared in a local FlatlineProxy configuration. Secrets are not written to Ainur's database; OAuth refresh can update the external Codex credential file. See [OpenAI design and route configuration](docs/openai-backend.md), noting that older route descriptions there precede the accepted minimal fix.

**Subscription-only ordinary startup in this source:** `Program.cs` refuses inherited OpenAI `api`/`auto` route settings before opening a home and pins `AINUR_OPENAI_ROUTE=subscription`. `OpenAiProvider.CreateDefault()` independently pins the subscription transport; no API-key fallback is permitted on that route. OpenAI historical `-api` catalog twins are disabled on startup reseed, but existing operator-modified rows must still be audited. Cash ceilings and gateway admission remain in force; they do not make an unknown-price paid route free or prove the behavior of a previously launched process. Do not treat a static receipt policy label or an on-disk DLL hash as proof of an old process's loaded provider. Keep unsafe or unverifiable runtimes offline until a strict, immutable replacement is verified.

The runtime's manager/specialist/cheap-service defaults are configured in [`RuntimeOptions`](src/Ainur.Core/Runtime/AinurRuntime.cs); new engineering projects explicitly select an enabled subscription team model (default GPT-6.1 Sol/high). Selecting a project manager in the UI does not override other runtime background roles. Choose available models and deliberate budgets before admitting work. Creating an engineering project alone does not wake its agents; sending a message or assignment can dispatch work and incur charges on non-subscription providers.

## Create a separate engineering project (new-project default)

From the UI choose **New project**. Enter a name and select an **existing directory** for its workspace (for example, a fresh disposable Git repository). Select **Engineering** for the default lead → technical delivery manager → independent reviewer reporting tree. The delivery manager owns a planned child objective beneath the lead's root objective; add implementation specialists and send assignments only when ready. Everyone starts asleep, on the explicitly selected subscription model (default GPT-6.1 Sol/high). An unavailable default blocks creation rather than silently switching providers. **Single manager** remains an explicit customization option for manual organization. Existing Ardas are never migrated by this setup.

The workspace is where project agents' file and Git tools operate; the separate Ainur `--home` holds state and artifacts for all projects in that instance. Selecting a directory does not clone/import a repository, grant access to arbitrary other paths, or provision credentials. Give each project its own non-overlapping workspace (engineering setup rejects paths shared with or nested under another project's workspace) and verify its Git remote/commit before assigning changes. Agents run with the host account's file permissions, not a filesystem sandbox; keep unrelated checkouts out of the workspace and use a disposable repository for trials. Selecting a workspace and creating a project alone does not wake agents or dispatch work; sending a user message or assignment does.

## First run: supervisor recommended

For AWS-backed models, see [Bedrock and Mantle setup](docs/bedrock.md). The optional AWS provider uses SigV4 and refreshable AWS shared-profile credentials, including `credential_process`. Set `AINUR_BEDROCK_ENABLED=1` to register it and `AINUR_ENGINEERING_ALLOW_API=1` to permit API-billed engineering teams. The engineering default and manager/specialist/background model defaults are configurable independently. The subscription-only onboarding descriptions above apply to the default configuration; the direct OpenAI route remains subscription-only.

Prerequisites: Git, the **.NET 10 SDK**, and **Node.js 20.19+ or 22.12+** with npm (matching the checked-in Vite toolchain). Chrome/Chromium and CDP setup are needed only for browser work. Commands below are **PowerShell**, run from the repository root; no globally installed `ainur-supervisor` command is assumed.

For a new home and deliberately selected source:

```powershell
# Inspect/pin your reviewed source before building.
git rev-parse HEAD
git status --short
dotnet build

$AinurHome = Join-Path $HOME '.ainur'
$AinurSource = (Get-Location).Path

# Explicitly request the subscription route; ordinary startup also enforces it.
$env:AINUR_OPENAI_ROUTE = 'subscription'

# First run builds the web UI and an immutable runtime release when none exists.
dotnet run --project src/Ainur.Supervisor -- run --source $AinurSource --home $AinurHome --port 5180
```

Open **http://127.0.0.1:5180**. Configure credentials/models and spending controls before creating a project with its workspace directory and talking to its root manager. There is no separate `init` subcommand. The foreground supervisor remains running; use your OS service manager if you want it to survive logout, with the same explicit home, port and environment.

`--home` is the durable state boundary: it holds `ainur.db`, artifacts, releases and runtime/supervisor metadata. The default is `AINUR_HOME` or `~/.ainur`; specify it explicitly when managing more than one install. `--source` is used for an initial build, **not** an instruction to replace an existing active release. If no active release is recorded but release directories exist, the supervisor selects the newest non-failed one; the example is for a fresh home, not a recovery shortcut. Never launch a second scheduler against an existing live home.

From another terminal, inspect state without requesting an upgrade:

```powershell
$AinurHome = Join-Path $HOME '.ainur'
dotnet run --project src/Ainur.Supervisor -- status --home $AinurHome
Invoke-RestMethod http://127.0.0.1:5180/api/v1/version
Invoke-RestMethod http://127.0.0.1:5180/api/v1/health
```

## Pin, validate, activate and recover

1. **Pin and build.** Review the exact source and ensure the worktree is clean. Keep the current release and take a consistent state backup before upgrades (SQLite online backup, or copy only after the runtime is fully stopped; do not copy a live WAL database file alone). Build without activating:
   ```powershell
   dotnet run --project src/Ainur.Supervisor -- build --source $AinurSource --home $AinurHome
   ```
   Record the printed release ID and its `release.json` revision/dirty flag. Runtime releases live under `<home>/releases/`; the supervisor itself is not replaced by a runtime upgrade.
2. **Validate deliberately.** Run the offline suite from the pinned source. The agent tool `validate_release` checks a candidate against a disposable database copy, readiness/migrations and old-runtime compatibility. Its default workflow **makes a real model request**; `skip_model_workflow: true` skips that request, but does not prove model behavior. `AINUR_VALIDATION=1` disables automatic host startup, not all explicit wake paths; it is not a network-denial sandbox. For strict offline integration tests use synthetic providers and no production credentials. See [release tools](src/Ainur.Core/Tools/ReleaseTools.cs).
3. **Activate only after acceptance and a safe drain.** Prefer the agent `build_release` → `validate_release` → `activate_release` workflow, whose activation tool checks the recorded validation state. The operator CLI also has:
   ```powershell
   # Replace with an actual reviewed, validated release ID; this queues a live upgrade.
   dotnet run --project src/Ainur.Supervisor -- activate RELEASE_ID --home $AinurHome
   ```
   The CLI **does not enforce validation** and returns after writing a request, not after successful activation. Use the same home as the running supervisor. Watch the attempt and verify the running release afterward; do not submit overlapping requests.
4. **Inspect outcomes and recover conservatively.** `<home>/supervisor/releases.json` records active, previous and failed releases; `<home>/supervisor/supervisor.log` records lifecycle decisions. Runtime stdout/stderr goes to the supervisor's console/service logs. The activity journal records upgrade attempts; `<home>/runtime/inflight.json` and `restart-reason.json` help diagnose watchdog exits. Automatic rollback attempts select the prior release after probation failure or repeated crashes, and suppress reactivation of failed releases. Manual rollback is an explicit activation request for a reviewed, non-failed prior release **after checking schema compatibility**; rollback does not undo database migrations or tool side effects. Do not clear failure state to bypass investigation.

**Current lifecycle caveats:** drain timeout aborts the upgrade and attempts undrain; check ready/draining status afterward, since an abort is not proof of quiescence or resumed admission. Pausing an agent does not cancel an existing model request. The accepted minimal branch improves recovery, but cannot repair an older running process until replaced. SIGINT/SIGTERM normally requests child shutdown with a kill timeout; arbitrary exceptions and cancellation during upgrades do not guarantee cleanup. Before a replacement or direct-to-supervised handoff, verify old PID exit, lock release and port ownership. Do not delete a living scheduler's lock, force-kill to disguise a failed drain, or replay unknown-outcome tools. See [supervisor lifecycle code](src/Ainur.Supervisor/Supervisor.cs) and [runtime recovery](src/Ainur.Server/Program.cs).

## Direct development alternative

For local debugging only, with a **different disposable home and port** from any supervised install:

```powershell
Push-Location web
npm ci
npm run build
Pop-Location
dotnet run --project src/Ainur.Server -- --home .ainur/scratch-dev --port 5190
```

Open http://127.0.0.1:5190. This starts the server directly: no supervisor crash/deadline watchdog or automatic rollback. It can still dispatch real models when work is created. A directory name containing `scratch` is not an isolation or spending control. Do not point this at a running supervised home.

## Testing and contributing

```powershell
dotnet build
dotnet test tests/Ainur.Tests --filter 'Category!=Live'
```

Offline provider tests use scripted/synthetic transports, though browser tests may start local Chrome and timing-sensitive tests can be flaky. Record actual test results for the source you intend to release; do not silently discard failures. `Category=Live` tests contact real providers and may spend money or consume subscription quota: run only explicitly authorized targeted cases, not as the default validation command.

Push each landed commit promptly to your **owned branch**, not only at release time. For example, `git push origin HEAD:refs/heads/ainur/YOUR_NAME/TOPIC`, followed by `git ls-remote origin refs/heads/ainur/YOUR_NAME/TOPIC` to verify the exact SHA. No force-push/history rewriting, no concurrent writes to another owner's branch, and no credentials, runtime databases, artifacts or private machine paths in commits. Label checkpoints honestly; publication is not review acceptance or deployment. Merge to main only through the project's review process. Provider/runtime changes should update their user-facing guidance and status here.

Further reading: [confirmed product backlog](docs/backlog.md) (future directions, not implementation claims), [architecture/specification](docs/spec.md), [model catalog](docs/model-catalog.md), [budget controls](docs/budget-controls.md), [OpenAI backend](docs/openai-backend.md), [browser contract](docs/browser.md), [image checkpoint contract](docs/conversation-images.md). Specifications and checkpoint documents describe intent as well as implementation; the exact source and verification status take precedence.
