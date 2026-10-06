# Bounded model waits and targeted session recovery

This is a **source contract**, not deployment evidence or authority to recover a historic UNKNOWN invocation. Confirm active build/API identity before using controls. These changes do not resolve historical billing, tool or maintenance UNKNOWN state.

## Deadlines and stream completion

`RuntimeOptions.ModelCallTimeout` defaults to 10 minutes and `ModelStreamIdleTimeout` to 90 seconds. Gateway full-call deadline bounds credential/header/body waits for all providers. Responses adapters apply full-call and underlying body-read idle deadlines themselves; partial lines and comments count as body progress. Continuous bytes cannot evade the full-call deadline. The AWS credential refresh receives the full-call bound through the Responses helper; a cancellation-ignoring refresh is tracked as unsettled and cannot proceed to HTTP dispatch once it returns.

`response.completed` returns immediately without waiting for `[DONE]` or EOF. `[DONE]` without completion and EOF without completion are incomplete/possibly billed failures. Transport ambiguity is possibly billed; retry/fallback is allowed only if no output was emitted **and** the provider explicitly proves no billing. Session-level retry also requires `MayHaveBilled=false`. Caller cancellation and stale epochs prohibit transcript and tool admission. A response that wins the cancellation race may settle observed usage while its output is discarded.

Bounding an await does not prove upstream cancellation. Cancellation-ignoring operations are tracked separately from the session loop, late faults observed, and model maintenance admission retained until actual task settlement. Their canceled requests remain UNKNOWN with explicit estimated/uncertain billing; late output is not replayed. No `Task.Run` wrapper claims to terminate synchronous provider/filesystem operations.

## Cancellation versus owned timeout

The gateway, OpenAI outer call, shared Responses send, and idle-read wrapper convert `OperationCanceledException` to a timeout only when their own linked deadline token is canceled and their upstream caller tokens are not canceled. An independent provider/header/body cancellation while all timers and caller tokens remain live propagates as cancellation; the finder does not disguise it as keyword fallback. A true gateway-owned timeout can still use the finder's local keyword fallback without dispatching another model. Caller cancellation, including a pre-canceled caller, is not relabeled timeout or authority for model fallback/retry.

These four filters establish **timer eligibility**, not perfect upstream causality. An independent cancellation racing an expired timer can still satisfy the timeout filter. Cancellation never proves that the provider was not dispatched or billed. UNKNOWN requests, uncertain cost records, and separately tracked unsettled operations remain intact; there is no automatic refund, retry, release, or replay. The filters do not introduce an earliest pre-admission guard and do not establish zero reservation, intent, or provider entry for pre-canceled callers.

`CancellationContractTests` uses only scripted providers, HTTP handlers, credential callbacks, and streams to cover independent cancellation, caller cancellation, owned deadlines, and controlled caller/timer precedence. The existing finder cancellation assertion is unchanged. The retained fake-only adapted focused verification runs this matrix together with `FinderReliabilityTests` and `BoundedModelTests`: 59 passed (21 cancellation cases, 19 finder cases, 19 bounded-model cases), with no live/default providers. This is source-level targeted evidence, not an unmodified broad-suite pass, final aggregate acceptance, migration/rollback proof, or deployment evidence.

## Readback and health

`GET /api/v1/sessions/{id}/lifecycle` distinguishes `stop_requested`, `loop_exit_confirmed`, `stop_confirmed`, and `unsettled_operations`. Confirmation additionally requires no running maintenance activity for that session (other than the resume control's own admission). UNKNOWN is not proof of no admitted side effects.

`GET /api/v1/health` adds advisory `model_waits`: start/progress/age, aged flag, and deadline metadata. One aged model wait does **not** make health 503, because the supervisor's unhealthy policy would cause a prohibited broad restart.

## Stop one matching live host

```http
POST /api/v1/sessions/<session-id>/control/stop
Content-Type: application/json
X-Ainur: 1

{"agent_id":"<agent-id>","generation":14,"actor_id":"<actor-id>","wait_ms":5000}
```

Use current runtime generation from `/api/v1/health` (top-level `generation`); the lifecycle response does not include generation. The illustrative value `14` is not an active-process identity. Wait range is 0–30000 milliseconds. Exact agent+session+generation must match an existing live host; absence never certifies historic unwind. The receipt is live-host/generation-fenced, not portable across restarts. The request durably pauses the agent/session, invalidates its epoch, cancels its token, and wakes the loop to unwind. Durable journal records capture receipt, actor, request and observed result. Repeating readback/stop in that generation returns the same receipt. Unconfirmed unwind remains blocked.

## Recreate only after confirmed stop

```http
POST /api/v1/sessions/<session-id>/control/resume
Content-Type: application/json
X-Ainur: 1

{"agent_id":"<agent-id>","generation":14,"receipt_id":"<returned-receipt-id>","actor_id":"<actor-id>","acknowledge_uncertain_model_billing":false}
```

Requires the exact unconsumed receipt and the same stopped host. It rejects unconfirmed stop, changed identity/generation, finished/nonprimary sessions, UNKNOWN/running tools or unknown maintenance. If model billing is UNKNOWN/dispatched, explicit acknowledgement is required and audited. This is **not** a side-effect waiver or gate bypass: UNKNOWN tools/maintenance remain blocked, ledger uncertainty remains intact, and no historic tool call is replayed.

A new host replaces the old one only after these checks. A preexisting pause is preserved and the replacement is not woken. A newer ordinary `PauseAgent` is detected by its monotonic durable `agent.paused` journal ID, captured at stop and compared inside the receipt-resume transaction while holding the admission lock shared with `PauseAgent`; its authority is preserved even though the agent was already stop-paused. The audit records both journal versions and whether the replacement remains paused. This is deliberately conservative: a newer ordinary pause requires a separate supported `ResumeAgent` **after** host recreation, not a stop receipt used as a release token. A later coordinated pause blocks receipt resume until its own release; a preexisting coordinated pause remains independently enforced after recreation. When both ordinary and coordinated holds exist, ordinary `ResumeAgent` and the corresponding `ReleasePause` are separate authorizations; neither token clears the other hold. The stopped loop normalizes its persisted session state to idle before confirmed loop exit; this correction does not redesign that fence or introduce a new pause schema.

A successful receipt is consumed so stale repeat resume is rejected. Ordinary `ResumeAgent` rejects stopped hosts rather than waking a permanently canceled loop. Receipts and their journal-version snapshot belong to one live host/runtime generation; restart does not reconstruct or certify them. Durable pause requests and pause journal history survive independently, and absence of a receipt or host after restart is not release authority or UNKNOWN reconciliation. Direct database edits and unjournaled pause-state changes are outside this supported control contract.

These examples match the source routes under `Api.MapGroup("/api/v1")`; they do not establish that the active server has these routes or that a live stop receipt exists. Request/receipt JSON uses snake_case (`Program.cs` configures `JsonUtil.Options.PropertyNamingPolicy`). Mutating requests require the nonsecret `X-Ainur: 1` header under the existing loopback-only middleware; foreign Host headers and cross-origin writes remain rejected. This header is not a credential or authorization bypass. `actor_id` is audit attribution, not an authentication token. No provider secrets or remote target state are returned.

## Deterministic local evidence

`BoundedModelTests` covers credential/header/body operations ignoring cancellation; idle empty/partial bodies; continuously progressing full-call deadline; completed and DONE open tails; incomplete EOF; cancellation races; aged diagnostics; separate loop/transport confirmation; late tool suppression; exact receipt and generation; required uncertain billing acknowledgement; UNKNOWN-tool rejection; preexisting/newer ordinary pause preservation through separately authorized release and a runnable turn; independent coordinated release; safe retry versus MayHaveBilled; and maintenance retention until actual settlement.

Browser fixtures require an installed Playwright chromium executable; missing browser binaries are environment failures, not skipped safety evidence. No live provider or external production traffic is used by these tests.
