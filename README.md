# Ainur

Ainur is a local agent-development platform for autonomous teams working on substantial projects over long periods. Each project (an **Arda**) has a manager, specialists, persistent identities, owned objectives, durable context and visible costs. You work through the team's manager; the team can write code, create its own tools, delegate work and propose upgrades to Ainur itself.

The runtime is C# on **.NET 10**, with a React/TypeScript web UI, SQLite storage and embedded PowerShell. **Use the supervisor for long-running work and release upgrades.** Direct server execution is useful for development, but does not provide crash/deadline recovery or supervised release switching.

> **Trusted-machine software, not a sandbox.** Agents execute tools with your OS account's authority. The HTTP service is loopback-only; do not expose it as a public or multi-tenant service. Use a dedicated account or machine when appropriate, protect its credentials, and review spending and proposed changes.

## Source status and release boundaries

This README describes **this source commit**, not whatever process happens to be running. Publication, review acceptance, merging into `main`, and deployment are distinct events. Check the running `/api/v1/version` and `/api/v1/health` and the supervisor's release state before assuming a source fix is active.

| Source checkpoint | Scope; not a deployment claim |
| --- | --- |
| `29ba9e7` (published `main` when this integration began) | Browser development and image checkpoints, with legacy provider-internal paid fallback. A historical branch point, not a statement about later `main` revisions. |
| `01d6c043` (`ainur/release/drain-title-01d6`) | Independently accepted minimal cash-safe OpenAI route, title compare-and-set and drain/admission recovery. |
| `5bdbf638` (accepted image/UI candidate) | Accepted backend/lifecycle and browser/UI synthetic tests, including owner-guard completion. Real model visual understanding remains unproven. |
| This integration source | Selectively combines the accepted image candidate and newer browser files from `29ba9e7`; **independent changed-source review, release validation and deployment remain separate gates**. |

For reproducibility, use a clean worktree pinned to a reviewed **full commit SHA**, not a moving branch name. Do not combine unreviewed worktree changes into a release. The image source and synthetic verification history are in [composition](docs/image-final-composition.md) and [integration manifest](docs/image-integration-manifest.md); see the [image contract](docs/conversation-images.md) for limits.

## What exists today

- **Organization and durable work.** Root managers and reporting trees; owned objectives with dependencies, evidence and delegated-work acceptance; versioned identities and knowledge; persistent and temporary agents; consultation forks with isolated git worktrees.
- **Execution and tools.** Dedicated serial session execution, typed inter-agent notifications, PowerShell as the tool shell, live .NET object handles, atomic file edits, MCP integration, tool discovery and agent-authored versioned PowerShell tools. Release tools can build, validate and request activation. There is no tool permission sandbox.
- **Persistence and context.** SQLite WAL with transactional journals, content-addressed artifacts, rolling/full compaction, tool-result elision and raw-history retrieval. Interrupted tools recover as **outcome unknown**, not blind retries. In-flight model requests retain conservative accounting. Object handles do not survive process restarts.
- **Providers and reasoning.** OpenAI **Responses** supports ChatGPT/Codex subscription authentication and a separately configured paid API route. DeepSeek and Z.ai use streaming Chat Completions. Provider adapters preserve tool-call identifiers and supported reasoning content/effort; effort names and capabilities are model-specific, not a universal guarantee that every model accepts `max`. The catalog contains GPT-6 Astra/Sol, DeepSeek and GLM routes as well as entries without adapters. Anthropic and xAI adapters are not implemented. Seeded availability/prices are not a live provider guarantee; inspect the actual catalog, since existing databases retain operator edits. Native OpenAI `web_search` serialization is opt-in per request, not automatically enabled for every agent turn.
- **Accounting.** Effective-dollar valuation is distinct from cash. Projects may have an explicit unlimited effective budget while retaining a separate cash ceiling. Quotes, reservations and settlement remain recorded; subscription valuation is not an invoice. Unknown cash pricing is not zero and cannot safely be treated as free. Quota windows are configured, not fetched from provider telemetry. See [budget controls](docs/budget-controls.md) and the explicit route distinction below.
- **Browser and images — source integration, not live perception.** CDP browser tools, sessions and screenshot artifacts are present; see the [browser contract](docs/browser.md). This source joins durable authorized user uploads and tool screenshots to real `input_image` payloads on supported OpenAI Responses vision routes, refusing unsupported primary or fallback routes instead of silently dropping pixels. Offline scripted-wire tests verify pixel delivery; they do **not** prove a live model saw or understood a screenshot. Uploads are **PNG only**, at most **2 MiB**, **2048 pixels per edge**, **3,000,000 pixels**, and **two attachments per message**. JPEG/WebP, animation and arbitrary remote-image URLs are unsupported. No general visual-computer-use claim until a separately authorized real vision proof.
- **Web UI.** Manager conversation and streaming, organization, objectives/evidence, accounting, journal, knowledge, runtime status and agent identity/context/transcripts. Image selection, preview, upload, durable history and idempotent retry in the accepted `5bdbf638` source were verified in Chrome against an isolated real API/storage server with **synthetic offline model responses**. The old-completion owner guard also passed a deferred-API test; this does not assert a reproduced real-network scheduling race or imply that the running deployment includes image UI.
- **Supervisor.** Immutable runtime releases, an OS scheduler lock per home, crash backoff, readiness checks, deadline watchdog, drain/activate/probation and rollback attempts. These are recovery mechanisms, not a promise that every cancellation or failed drain recovers automatically; see the operator caveats below.

## Credentials and spending safety

Configure credentials in the environment of the **supervisor process** (its runtime children inherit them), or through the supported host credential store. Do not put secrets into source, prompts, artifacts or repository files.

| Provider | Host-managed source |
| --- | --- |
| OpenAI subscription | Codex OAuth tokens in `~/.codex/auth.json`; refresh uses atomic write-back. Treat this file as a credential, not an Ainur configuration example. |
| OpenAI API | `OPENAI_API_KEY`, or macOS keychain service `ai.openai.api` |
| DeepSeek | `DEEPSEEK_API_KEY`, or keychain service `ai.deepseek.api`, account `FlatlineProxy` |
| Z.ai | `ZAI_API_KEY`, or keychain service `ai.z.api`, account `FlatlineProxy` |

The resolver can also use credential-source names declared in a local FlatlineProxy configuration. Secrets are not written to Ainur's database; OAuth refresh can update the external Codex credential file. See [OpenAI design and route configuration](docs/openai-backend.md); its historical fallback discussion must be read with the accepted source change described below.

**Route boundary:** historical `29ba9e7` source and any older runtime built from it could internally fall back from a subscription request to a paid OpenAI API credential without a new gateway quote. A cash ceiling alone is **not** a safe guarantee for that older route. This integration retains the independently accepted `01d6c043` fix: subscription catalog rows use subscription transport only; paid API requires an explicit API model route and a separately quoted gateway admission, where unknown-price API requests can be blocked. `AINUR_OPENAI_ROUTE=subscription` applies to subscription rows, not an explicit API row or other providers. To insist on no paid OpenAI route, also review enabled API/fallback model rows and credential availability. **Publishing this source does not change an older running process**; verify deployed release and route before relying on this boundary.

The default manager, specialist and cheap-service model is DeepSeek Flash, so OpenAI credentials alone do not configure every background role. Before starting a project, choose available models and set deliberate budgets. The shipped server uses the defaults in [`RuntimeOptions`](src/Ainur.Core/Runtime/AinurRuntime.cs); it does not expose environment overrides for those three model defaults. Selecting a different manager in the UI does not change the cheap-service default. Creating a project or sending a message can start model work and incur charges.

## First run: supervisor recommended

Prerequisites: Git, the **.NET 10 SDK**, and **Node.js 20.19+ or 22.12+** with npm (matching the checked-in Vite toolchain). Chrome/Chromium and CDP setup are needed only for browser work. Commands below are **PowerShell**, run from the repository root; no globally installed `ainur-supervisor` command is assumed.

For a new home and deliberately selected source:

```powershell
# Inspect/pin your reviewed source before building.
git rev-parse HEAD
git status --short
dotnet build

$AinurHome = Join-Path $HOME '.ainur'
$AinurSource = (Get-Location).Path

# For subscription rows (not a global no-spend switch); review the route boundary above.
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

Offline provider tests use scripted/synthetic transports, though browser tests may start local Chrome and timing-sensitive tests can be flaky. The accepted composition adds schema-6-to-7 migration compatibility tests; a pause-expiry timing race can still fail and should be reported rather than hidden. Keep failure evidence; do not silently discard failed tests. `Category=Live` tests contact real providers and may spend money or consume subscription quota: run only explicitly authorized targeted cases, not as the default validation command.

Push each landed commit promptly to your **owned branch**, not only at release time. For example, `git push origin HEAD:refs/heads/ainur/YOUR_NAME/TOPIC`, followed by `git ls-remote origin refs/heads/ainur/YOUR_NAME/TOPIC` to verify the exact SHA. No force-push/history rewriting, no concurrent writes to another owner's branch, and no credentials, runtime databases, artifacts or private machine paths in commits. Label checkpoints honestly; publication is not review acceptance or deployment. Merge to main only through the project's review process. README ownership is assigned to **Aulë**: provider/runtime changes should update their user-facing guidance and status here.

Further reading: [architecture/specification](docs/spec.md), [model catalog](docs/model-catalog.md), [budget controls](docs/budget-controls.md), [OpenAI backend](docs/openai-backend.md), [browser contract](docs/browser.md), [conversation-image contract](docs/conversation-images.md). Specifications and checkpoint documents describe intent as well as implementation; the exact source and verification status take precedence.
