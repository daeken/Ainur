# Incremental maintenance quiescence (source contract)

This is source functionality, not proof that any running release has these controls. Older releases without this fence do not provide its guarantees. Publication does not authorize a live transition or replay an unresolved action.

## Operator control (no agent/model/launcher session dependency)

The existing process-receipt loopback bearer authentication protects these endpoints. Use the operator-owned `<home>/supervisor/receipt-secrets/route-receipt.key`, protected as a regular private file in private no-follow directories. Never put the key in a shared transcript, knowledge, Git, or command-line argument. Responses are `Cache-Control: no-store`.

All paths start with `/api/v1/control/maintenance`:

| Method/path | Purpose |
|---|---|
| `POST` | JSON `{"reason":"operator maintenance","deadlineSeconds":120}` creates the durable service-wide admission fence. Returns operation/status. One active operation maximum. |
| `GET` | Read status while sessions/tools are still running; no agent tool or model needed. |
| `POST /<operation-id>/abort` | Release this operation's fence/holds after failed collection. No cancellation, transcript rewriting, or uncertain-action replay. |
| `POST /<operation-id>/release` | Release only after verified quiescence. Existing intentional pauses, budget policy, workspace selection and uncertainty remain authoritative. |
| `POST /<operation-id>/handoff` | Require a verified service-wide boundary and durably mark `handoff_ready`. Does **not** replace/stop anything. External supervised replacement, backup/state/pinned-release gates remain separate. |

The response includes `admissionFenced`, `verifiedQuiescent`, `deadlineElapsed`, durable operation/id/reason/generation/deadline, per-session `holds`, and exact `blockers` (kind/id/session/state). `running_to_checkpoint` means admitted work still owns the session; `held` acknowledges an actual loop/lease boundary; `unresolved` retains uncertainty. No health-ready, model-idle, requested-pause or elapsed-deadline signal substitutes for a checkpoint. An elapsed deadline does not auto-abort, cancel, kill, replay, or declare success. Operator polling is optional and does not drive the fence.

## Execution contract

- One runtime `AdmissionGate` serializes new ordinary admission with fence creation. All projects/Ardas are covered without examining their contents. Session steps, tool entry (including nested tools), model calls/attempts (including consultations/knowledge), project/agent/service creation, workbooks, continuity reconciliation and automatic host activation are fenced. Native Git's generic `AdmitMutation(string kind)` in `src/Ainur.Core/Runtime/MutationAdmission.cs` now pairs `Maintenance.Admit("git_mutation", referenceId: kind)` with its transient drain lease under the same gate, preserving drain checks and idempotent finally settlement. An UNKNOWN prior Git lease after restart manually holds Git mutations even after global abort, without automatic replay; status/inspect remain available. Combined-source tests and independent exact-target acceptance are required.
- Already admitted model/tool work settles normally. Maintenance does **not** increment pause epochs, cancel a token, request Stop, or discard a paid response. The loop is held before its next new model/tool/turn admission. Holds accumulate as sessions arrive; a long/noncooperative tool remains an explicit blocker.
- User input, messages, results and continuity/review obligations remain durable. Input acceptance is independent of activation: a paused-root conversation POST (text or image attachments) returns accepted once its message/notification commit succeeds, even while fenced or while its maintenance activity is UNKNOWN after restart. The post-commit resume check shares AdmissionGate with fence creation, so an intervening fence cannot create a false 409 for committed input. Idempotency retries return the same entry/notification; maintenance release does not auto-resume an intentionally paused root. Outside maintenance/UNKNOWN, the existing ordinary-message root-resume behavior is preserved (including its independent scoped-pause policy). Incoming notifications are not delivered to the model while fenced. Paid assistant output and its unadmitted tool calls are retained with the pinned model-request tool bindings. `maintenance_pending_calls` positively identifies never-admitted calls; only these may execute once after release, not historic missing-result/UNKNOWN calls.
- Activity admission is durable (`maintenance_activities`) and its lease settles in `finally`. The per-session holds are separate from existing agent/pause records. Release never changes intentional agent/pause/budget/workspace/model policy.
- Legacy drain is rejected while an active maintenance operation is not verified quiescent: it cannot override this fence to discard an admitted result. This does not make shutdown/Stop a maintenance control. Do not call shutdown or pause/cancel as a substitute for the maintenance boundary.
- Crash/restart converts prior-generation running maintenance activities to UNKNOWN, preserves the fence, and reports unresolved owners. Abort can release global admission but sessions with UNKNOWN maintenance activity stay blocked; this slice offers no uncertainty-resolve/replay control. Existing unknown invocation/request evidence is also a quiescence blocker, without changing its preexisting non-maintenance recovery semantics.
- Only an explicitly attested `handoff_ready` operation with no surviving blockers can release automatically after successful runtime construction/recovery. A normal collecting operation—even if idle—stays fenced across restart. Backup/restoration/release identity and external handoff authority must be independently verified before using `handoff`.

Schema increment identity: migration **11 `maintenance_quiescence_v1`**, tables `maintenance_operations`, `maintenance_holds`, `maintenance_activities`, `maintenance_pending_calls`. Composed Native Git is additive migration **12 `native_git_workspaces`**, with its endpoint fingerprint columns. Actual schema7→12 and maintenance11→12 reopen paths are tested; maintenance11 migration text remains unchanged. Experimental schema11 databases with conflicting table identities are not supported maintenance11 upgrade inputs. Publication does not attest installed schema/runtime, and accounted service work is not an OS sandbox: detached/untracked external/background writers remain outside the service quiescence proof.

## Older-runtime bootstrap limitation

An older runtime without the durable maintenance admission/checkpoint boundary cannot acquire it merely by publishing source or invoking its transient drain/pause controls. Host-only drain observations do not cover direct model/consultation work, and cancellation or a requested pause is not proof of settled service-wide activity. Verify the actual installed runtime before using these endpoints.

If an unresolved action prevents a verified boundary, preserve the uncertainty and obtain separate operator authorization for a controlled outage/replacement or other evidence-based intervention. Do not cancel, kill or replay an unresolved action as a substitute for quiescence. This source contract does not authorize a live transition, database repair or runtime replacement.

## Offline evidence

`MaintenanceTests`: staggered all-Arda boundaries/incoming user/results/policy preservation; paid-response retention and single unadmitted-call continuation; long noncooperative tool and deadline honesty; restart UNKNOWN without replay; clean handoff release preserving pause; direct model/consultation/workbook/inbox admission denial.

`MaintenanceApiTests`: disposable in-process real HTTP server, operator authentication, anonymous denial without side effects, no-store status and abort while the launcher session itself is held. No live provider/model or current home is used.

`MaintenanceInputTests`: direct and real HTTP paused-root text/image acceptance with idempotency; original pause reasons/scoped pauses preserved; deterministic synchronous post-commit fence race with no false rejection; no host/model activation; restart UNKNOWN accepts attachments without resume/replay; normal outside-maintenance paused-root input behavior unchanged.
