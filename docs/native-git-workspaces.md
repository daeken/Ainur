# Native Git workspace workflow (opt-in)

This slice provides durable, objective-linked Git author worktrees, independent exact review receipts, guarded **local fast-forward integration**, and a separate **explicit publication** obligation. It does not automatically move existing projects, create models, schedule agents, edit agent identities, merge, push, or delete anything. A human or agent must explicitly request each mutation.

## Tools and local human API

Agents load `git_workspace` with `load_tools`. Humans use the existing local-only authorized HTTP control plane:

- `GET /api/v1/projects/{projectId}/git` — durable repositories/workspaces/operation states.
- `POST /api/v1/projects/{projectId}/git` — the same action payload as the agent tool, executed as the authorized `human` principal. No caller-supplied actor/agent impersonation.

The API is intentionally small; no new Git UI/framework is required. Server authorization is the existing local control-plane middleware. Repository mutation requires an available, non-paused project actor and admission through the runtime's existing atomic `AdmissionGate`. All mutating lifecycle actions (including human calls, reconciliation and publication readback that changes durable disposition) explicitly reject while draining; they are **not queued** and make no new Git/operation/notification writes. Read-only status/inspection remain available. Already-admitted native actions hold a named running lease: drain cannot claim success until they settle. Call cancellation/deadlines can interrupt subprocesses, but never replay them; unresolved admitted effects retain UNKNOWN blockers. Existing drain timeout rollback semantics are unchanged. Native durable intents still provide restart/unknown reconciliation. On the composed maintenance mainline, the same admission entry pairs each transient drain token with `Maintenance.Admit("git_mutation", referenceId: kind)` under the existing AdmissionGate, and settles both with idempotent finally/Dispose. New mutations reject while maintenance is fenced; admitted mutations finish normally and remain named blockers until settlement. A prior-generation unclosed Git maintenance lease becomes UNKNOWN on restart, preventing verified quiescence and holding further Git mutations even after global abort; manual uncertainty resolution is required, never automatic replay. Read-only status/inspect remain available. Registration adds its own caller-keyed intent before filesystem/Git preconditions and a read-only `reconcile_registration` action. No new scheduler or automatic uncertainty release is added. API calls do not wake/resume intentionally paused authors. Review notifications use existing durable notification/continuity dispatch, not a new scheduler. Notification persistence is not proof that a model has reviewed the change.

## Ordinary assigned change

1. Start with a **clean normal Git checkout**, including no tracked or untracked changes, on the intended integration branch. The repository path must exactly equal this project's configured workspace; overlapping projects are refused. The lead or authorized human registers it and names the integration owner:

   ```json
   {"action":"register","operation_id":"register-123","path":"/absolute/project/repo","integration_branch":"main","owner_id":"agt_delivery_manager"}
   ```

   This is opt-in registration, not blanket permission to operate on arbitrary repositories. Registration records the canonical common Git directory. Only the registered owner or authorized human may integrate, reconcile, or publish.

2. Assign an objective to an implementer with normal objective/assignment tools. The author creates a worktree:

   ```json
   {"action":"create","repository_id":"repo_...","objective_id":"obj_...","operation_id":"change-123-create"}
   ```

   The returned `gws_...` has an owned generated branch `ainur/work/gws_...`, exact `base_sha`, and a path under the runtime home `git-workspaces/gws_...`. **Use that returned absolute path explicitly for edit/build/test commands. This slice does not silently change SessionHost's working directory or file-tool workspace.** Never edit the integration checkout for author work. Existing ordinary Git commit commands are used in the author worktree. No hooks are run by the native lifecycle implementation.

3. Commit the change, run tests in that author worktree, and inspect:

   ```json
   {"action":"inspect","workspace_id":"gws_..."}
   ```

   Inspection returns current source head, reviewed base, target head, dirty status, bounded path/stat diff and durable receipt/operation metadata. Use ordinary `git -C <owned-path> diff <base> <head>` or authorized file inspection for the full patch. The service does not store patch contents, shell command output, or remote credentials in its operations table. Supply test evidence references yourself; evidence is an attestation, not a claim that this service ran or verified tests. Never put secrets in evidence.

4. Submit the **exact current clean committed head** plus test evidence to a distinct named agent reviewer:

   ```json
   {"action":"submit_review","workspace_id":"gws_...","head":"<full SHA>","reviewer_id":"agt_reviewer","evidence":"dotnet test --filter Category!=Live: 249 passed; artifact path/hash; exact scope"}
   ```

   The durable assignment includes exact base/head, objective, workspace and evidence. The named reviewer inspects/tests independently, then accepts:

   ```json
   {"action":"accept_review","workspace_id":"gws_...","head":"<same full SHA>","base":"<exact base SHA>"}
   ```

   The receipt records independent actor, base, head and timestamp. Author self-acceptance is forbidden. A local human may independently review an agent-authored change; the single `human` principal cannot independently accept another `human`-authored change. Human-authored changes therefore require a named agent reviewer in this slice. A clean source HEAD change invalidates the receipt for integration, even if a previous receipt remains visible in history. Fresh submission clears the old acceptance. Exact receipt attests to acceptance, not correctness.

5. Acceptance changes durable state to `integration_owed` and sends the registered integration owner an existing durable result notification. The owner explicitly requests local integration:

   ```json
   {"action":"integrate","workspace_id":"gws_...","operation_id":"change-123-integrate"}
   ```

   One per-common-directory OS file baton serializes native operations across threads/processes. A durable operation intent precedes Git. The target checkout must still be clean, on its registered branch, without an in-progress Git operation. The source must still be its owned branch, clean, and exactly the accepted SHA/base. Only a fast-forward is allowed; hooks are disabled and ignored files are not overwritten. The conservative drift policy accepts **only the exact reviewed target base or already-that-head**. Other target movement refuses without merging, even if Git could technically fast-forward. No auto-rebase, merge conflict resolution, reset, force push or deletion occurs.

6. Local integration is useful with **no remote configured**: state is `integrated`, publication `not_configured`, author workspace/branch `retained`. The objective is not auto-completed; existing owner/reviewer/manager handoffs remain responsible for evidence and completion.

## Structured review-purpose receipt (required schema 14 gate)

Release requires migration 13 registration-intent reliability **and** additive migration 14 structured purpose receipts, with independent review of the combined exact source. Never activate schema 14 without its migration 13 dependency. Exact acceptance and abandonment remain purpose-fenced.

Submit atomically persists `git_review_receipts` keyed by action notification: exact workspace, project, objective, base, head, reviewer and active/accepted/canceled/superseded status. Continuity recognizes completed-artifact review only from this provenance plus exact current workspace identity/lifecycle. Body text, prefixes and dedupe strings are never proof. Original actions and generated continuations share the same origin-purpose fence. Acceptance settles coordination; canceled/superseded/mismatched purposes never dispatch and cannot be revived by generic work disposition.

An exact duplicate Submit creates no action or wake and never resets acceptance or clears a hold. Submitting a changed current head supersedes the old receipt atomically; a held or overlapping-UNKNOWN current action must first be explicitly reconciled. Returning to a previously canceled/superseded source/reviewer identity is refused, not revived. Cancellation/supersession also cannot move a fresh action's timestamp past earlier unresolved UNKNOWN review effects; receipt history and observable legacy holds remain conservative blockers until exact reconciliation. Abandon cancels current receipts; canceling a review obligation cancels its receipt. Accept requires exact current structured purpose and rejects waits, blocks, unresolved UNKNOWN, canceled objectives, stale source and canceled actions. No self-acceptance/automatic integration/authority bypass is added. Workspace-history UNKNOWN is rechecked atomically at acceptance write and at NEW integration intent, not only during Submit. A prior reviewer call still running when a successor head/reviewer is submitted may become UNKNOWN later; the earlier receipt's history then blocks successor acceptance (including repeated acceptance reporting) and NEW mutation even when the old action/continuation is canceled or superseded. Historical tool effects remain unresolved until exact reconciliation, regardless of successor timestamp or reviewer identity. Already-applied operation readback and conservative operation reconciliation remain separate, never replay.

Legacy assignments are **not backfilled**. After inspecting current exact source and obtaining authority, newly supported Submit creates a fresh explicit receipt for an unreceipted identity. Existing legacy acceptance cannot silently authorize continued release; reassert and independently accept the structured review. Migration preserves historical records without guessing. The instruction to keep native implementation objectives nonterminal was a temporary workaround, not proof of purpose.

Migration 13 and 14 preserve their schema identities, source bytes and ordering. `NativeGitReviewPurposeTests` pins the compiled SQL UTF-8 lengths and hashes for both migrations; lexical source-text hashes are not compiled SQL hashes. Do not change migration SQL to manufacture matching evidence.

### NEW integration authority, admission boundaries and rollback evidence

NEW Integrate checks the current exact accepted structured purpose **inside the same DB transaction that persists the new operation intent**, in addition to the existing actor/registered-owner, source/base, target drift, maintenance and typed registration-intent gates. Legacy acceptance fields alone, canceled/superseded purpose, or overlapping UNKNOWN cannot authorize a new mutation. Supersession and cancellation commit against the same DB gate. Revocation ordered before new intent is observed and refused; revocation ordered after an admitted intent cannot undo the external Git effect or guarantee the merge never begins. No attempt is made to serialize arbitrary external Git writers; existing conservative ownership/clean/base/head checks and exact readback still apply.

A previously succeeded operation ID returns readback without re-running Git, even if purpose was later canceled. An UNKNOWN operation is never replayed: use explicit conservative reconciliation to establish an already-applied effect from exact target/source identity. Cancellation is not rollback, permission to retry with a fresh ID, or proof a post-intent effect did not happen. Existing repository-wide pending operation and project-scoped unresolved registration-intent fences remain deliberate conservative blockers and are not cleared by migration, receipt cancellation, or owner return.

Compatibility tests exercise populated schema12 -> actual13 ->14 with legacy work holds/invocations/acceptance preserved and no guessed receipts, actual13 unresolved intents ->14 with mutation refusal, and outstanding active/canceled/superseded14 receipts read by a **pinned schema12 migration-guard/read-query model**. The pinned guard is the exact base4f27 `Db.Migrate` early return for newer `user_version`; read queries and raw schema/row snapshots prove no destructive downgrade or hidden backfill. This is not a full old-runtime binary activation/mutator rollback test. Older binaries cannot enforce new purpose fences and must not be allowed to perform native mutations on upgraded state; rollback requires explicit maintenance/admission hold and separate release authority. Do not lower user_version, drop new tables, clear UNKNOWN, or reinterpret receipts to make rollback runnable.

## Separate publication

If publication is desired, opt in at initial registration using an **existing named remote** and valid branch:

```json
{"action":"register","operation_id":"register-123","path":"/absolute/project/repo","integration_branch":"main","owner_id":"agt_delivery_manager","remote":"origin","remote_branch":"main"}
```

The service provisions no credentials and makes no network request during registration, authoring, review or local integration. Integration then records `publication_owed`; it does **not** push. The registered integration/publication owner or authorized human must explicitly invoke `publish` with an operation ID. Publication requires the target still equal that workspace's integrated SHA. Registration binds cryptographic SHA-256 hashes of the exact effective single fetch and single push endpoints; the publication intent separately persists those hashes and the exact destination branch. Only hashes—not credential-bearing endpoint URLs—are returned or stored. Credential changes count as authority drift. Multiple fetch/push endpoints and Git URL rewrite configurations are rejected; no ambiguous multi-target publication is supported. Distinct fetch/push URLs are supported, but readback always addresses the **actual bound push endpoint**, never the fetch alias. Before intent, at actual push point of use, and before/after remote readback, endpoint authority must still match the registered/intent binding. Commands use the captured explicit endpoint rather than re-resolving a mutable remote alias. Any alias/endpoint/ref binding drift fails closed for manual resolution, including unknown-publication readback, even if the substituted endpoint contains the same SHA. Existing unbound records cannot acquire publication authority automatically. It uses a non-force push of that exact SHA and an exact `ls-remote` SHA readback. Only observed equality changes state to `published`. An unknown push disposition stays unknown/owed; do not retry blindly. Use `publication_readback` with the original operation ID (this is an explicit remote query). If the remote has moved or cannot be read, the obligation remains unresolved for human reconciliation.

## Failure, restart, duplicates and drift

- `operation_id` is a caller-selected stable unique id. Registration now **requires** it (1–128 characters: initial letter/digit, remaining letters/digits/`.`/`_`/`:`/`-`); existing create/integrate/publish rules remain unchanged. This is an intentional registration API compatibility change; old register payloads are rejected before intent/Git. Reusing it for different repository/action/actor/intent is refused. A recorded success is returned idempotently; it never re-executes Git.
- An `intent` or `unknown` disposition blocks **new native mutations for that repository**. Runtime restart does not execute Git or retry operations. Status/inspection remain available.
- For unknown `create`/`integrate`, the owner requests `reconcile` with the original operation ID. It observes actual owned worktree/ref/clean target: a completed exact FF is recorded; an unchanged target can be recorded `not_applied` without executing Git. The original operation ID cannot be replayed; a new explicit request/ID is required after safe not-applied resolution. Partial create/ref-only states or unexpected refs remain blocked for manual reconciliation.
- Moved target/conflicting parallel authors: retain both worktrees. Do not merge unaccepted work. Create a new objective-owned worktree at the current clean target, bring over the intended change using ordinary Git, run tests and obtain new exact independent review. The original workspace base is immutable; abandon it when appropriate.
- If the owner is paused/unavailable, the obligation stays durable; no auto-resume/takeover occurs.
- Native locks do not stop an unrelated human Git process/editor that ignores the baton. All preconditions are checked immediately before the mutation, and exact refs are checked after it; detected races become unknown/reconciliation. Do not use concurrent external writers on the integration checkout. Git's own ref/index locks still apply.
- Git argument vectors use `ProcessStartInfo.ArgumentList`, never concatenated shell commands. Output is bounded, stderr is discarded, interactive prompts are disabled, hooks/fsmonitor disabled, and ambient Git path/config overrides are removed. Exit and concurrent stdout/stderr drains share a 30-second subprocess bound and the caller cancellation token. Every service action also shares a 90-second overall budget across its subprocesses; cleanup gets a separate maximum 2 seconds. Best-effort tree termination is not proof of descendant effects: interruption reports phase/root-exit/pipe confirmation and preserves an admitted UNKNOWN maintenance lease even after Dispose/restart. Both runtime drain status and maintenance verification retain that blocker; further mutations are rejected. No `Task.Run` timeout wrapper abandons a running mutation. Synchronous filesystem/path canonicalization, SQLite access, and best-effort OS tree kill are not preemptible; the deadline is checked between phases, not falsely advertised as a hard wall-clock bound for arbitrary stalled filesystem calls. The service does not capture credentials or create them. This is not a sandbox for arbitrary project code or malicious repository filters/configuration.

## Retention and cleanup

Every created workspace and branch is **retained by default**. `abandon` marks a non-integrated owned workspace abandoned but preserves dirty/untracked files, branch and all history. No automatic cleanup or delete action exists in this first slice. Manual operator cleanup must first establish ownership, inspect all dirty/untracked content, obtain explicit approval, and retain durable evidence; do not force-reset or delete another owner's worktree. Integrated workspaces remain retained. The service never treats objective completion as permission to delete.

## Offline proof and release limits

`NativeGitTests` use only disposable temporary Git repositories and a local bare remote; model calls throw. The ordinary assignment → generated author worktree → committed change/test evidence → independent exact acceptance → local FF → separate publication path is exercised via the same service used by the tool/API, including restart, duplicate ids, stale receipts, dirty/untracked targets, conflicting parallel authors, ownership/ref drift, unknown dispositions, retained abandonment and baton contention. No user's repository or live runtime is touched.

Migration **12 `native_git_workspaces`** adds only `git_repositories`, `git_workspaces`, `git_operations` and their indexes, including repository `fetch_endpoint_hash`/`push_endpoint_hash` and operation `fetch_endpoint_hash`/`push_endpoint_hash`/`publication_branch` columns; no endpoint URL or credential is persisted. Existing migration11 `maintenance_quiescence_v1` is byte-for-byte preserved. Experimental schema11 databases with conflicting table identities are not supported maintenance11 upgrade sources. Composed schema7→12 and maintenance11→12 reopen tests preserve continuity/workbook/input/maintenance state and exercise ordinary Git after release. Runtime construction applies schema migration; source publication does not prove installed schema12 or running-binary identity. Ordinary local FF, remote publication/readback and deployment/restart remain separate. Service quiescence accounts cooperative service-owned activities, not detached/untracked external writers; it is not an OS sandbox. Topic publication is source availability, not deployment or independent composed-target acceptance.

## Registration intent and read-only uncertainty inspection

`status` includes `registration_intents`. Register persists caller ID, actor/project/requested binding before filesystem/Git validation, then exact canonical destination fences before repository insertion. Successful return records completion; interruption/failure after intent records UNKNOWN and marks its admitted maintenance lease UNKNOWN; startup changes prior running intents to UNKNOWN without Git execution. An independent typed **project-wide running/UNKNOWN registration-intent fence** rejects all native mutating actions before subprocess effects or new registration intents—even when no maintenance lease survives. Registration insertion rechecks that fence under the shared admission gate. It deliberately does not compare destination spellings: absent, partial, mismatched or exact bindings all fence the project, so `.`/`..`/aliases and fresh caller IDs cannot evade unresolved admission. Other projects are not fenced by an intent-only blocker; retained UNKNOWN maintenance leases keep their existing service-wide gate. Reusing a registration ID is always refused—even a completed intent is inspected rather than executed again.

```json
{"action":"reconcile_registration","operation_id":"register-123"}
```

Lead/human-only readback is available during maintenance/drain and executes **no** Git, filesystem probe or mutation. It returns `completed_readback` only for an exact durable repository ID/project/path/common-directory/branch/owner/remote/endpoint-hash binding. It does not rewrite the intent, clear any UNKNOWN lease, or release the intent admission fence—even an exact repository proof does not authorize replay. Read-only `status` and `reconcile_registration` remain available while blocked; actor/project authority is unchanged. Intent-only running/UNKNOWN rows are also typed maintenance blockers and runtime drain blockers without requiring a surviving lease. Missing/mismatched destination stays UNKNOWN; absence never means not executed. A crash before candidate binding cannot be proved by this action and needs independent investigation. A historical invocation predating the new intent remains UNKNOWN: the new schema/readback neither associates it with another registration nor proves non-execution. No direct database repair, replay or unsafe automatic release is authorized.

### Conservative failure boundary (review correction)

There is **no** durable proven-no-effects disposition/escape in this slice. Once an intent exists, an ordinary pre-Git validation failure is conservatively UNKNOWN just like an exception after binding or repository insertion, and it fences subsequent native mutations in that project. Absence of binding/lease is not a no-effects proof. Invalid caller identity and cancellation rejected **before** intent creation introduce no intent uncertainty; completed registrations do not create an intent fence. No action here automatically resolves or clears UNKNOWN; continued native work after unresolved uncertainty requires a separately authorized, evidence-based resolution capability outside this slice. Availability is deliberately traded for the no-replay gate rather than silently admitting a fresh ID.

Regression coverage rejects an orphan-intent fresh-ID bypass and exercises intent-only running/UNKNOWN absent/candidate/mismatched/exact-destination cases across startup, alternate spellings/all mutation admission, post-insert completion failure, insertion recheck, drain/maintenance blockers, and project-scoped observation/authority. Disposable regressions do not reconstruct historical live registration uncertainty.
