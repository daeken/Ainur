# Durable result-to-review handoff

A result to your manager can name an independent reviewer in that receiving manager's reporting subtree when the sender owns the specified objective. The reviewer assignment is issued under the receiving manager's authority:

```json
{"to":"manager-id","type":"result","objective_id":"obj_...","reviewer":"reviewer-name-or-id","body":"Result and evidence"}
```

`send_message` commits the result notification to the manager, **one** reviewer assignment (or one manager escalation if the reviewer is missing, paused or has no runnable primary session), and a `review_handoffs` receipt in one database transaction. An exact duplicate objective/reviewer handoff returns the existing receipt and cannot create or wake a second assignment. To nominate a *different* reviewer after a stall, the accountable manager must arrange a new reviewer; the sender can issue a distinct named handoff on the same objective. Reviewer names resolve to agent IDs where possible; use an ID for ambiguous names. A paused reviewer is **never** resumed automatically.

Inspect `objectives` with `objective_id` to see `Review handoff: queued|paused|stalled ...` and the durable action notification ID. `queued` describes a committed assignment, **not** confirmation that the reviewer acted. `paused` requires operator action. `stalled` means the reviewer is absent or has no runnable primary session; its one escalation is addressed to the manager. The ordinary inbox marks delivery separately. Manager session absent during commit does not discard the result: its pending inbox row remains through a process restart. Existing `SessionHost` delivery and the work-continuity reconciliation in the existing expiry timer recover missed wakes and delayed valid session creation. Accepted result/reviewer/manager obligations survive final responses; explicit blocked/wait dispositions and paused/budget-gated agents are never automatically resumed. See `docs/work-continuity.md` for bounded owner/manager continuation and status contract.

## Exact-source handoff and nonwaking decisions

`decision` is background information, **never** a review assignment or a wake. A decision saying “please review” plus a final answer does not dispatch review. Use the supported named result above; include the exact immutable source identity and bounded evidence, for example:

```json
{"to":"delivery-manager-id","type":"result","objective_id":"obj_...","reviewer":"independent-reviewer-id","body":"Candidate for independent review only. Author path /absolute/objective-owned/worktree; branch ainur/topic; base <full-base-sha>; head <full-head-sha>; changed paths Runtime/WorkContinuity.cs, tests/...; clean git status; command dotnet test ... --filter Category!=Live: exit 0, N passed. Inspect this exact base/head; no integration/deploy authority implied."}
```

The ordinary named handoff stores a durable action receipt, not a structured Git acceptance receipt: exact source in this example is reviewer evidence, not a substitute for Git lifecycle identity/authority checks. Named handoffs dedupe by objective/reviewer, **not source revision**; repeating with a changed head cannot create a new review of that head. Accountable managers must explicitly arrange a fresh supported review identity and acceptance after source changes; never reuse an old GO for a new head. When native Git lifecycle is admitted, `git_workspace submit_review` provides its own exact base/head review identity. If native operations are suspended or UNKNOWN, do not retry them; use the authorized manual handoff and keep the implementation objective nonterminal until independent acceptance.

Native Git exact-purpose receipts are **required**; see the source contract in `docs/native-git-workspaces.md`. Once the independently reviewed schema13+14 candidate is installed, `git_workspace action=submit_review` persists action/workspace/project/objective/base/head/reviewer purpose and fences cancellation/supersession for completed artifacts. Legacy text/dedupe is never inferred or backfilled: newly supported Submit explicitly reasserts inspected current identity. Duplicate exact Submit does not re-wake, clear holds, or reset acceptance. Installation/combined review remains a release gate; ordinary named `review_handoffs` authority is unchanged.

Implementation: `src/Ainur.Core/Tools/ReviewHandoff.cs`, `src/Ainur.Core/Tools/OrgTools.cs`, schema 8 in `src/Ainur.Core/Persistence/Migrations.cs`. Offline regressions: `tests/Ainur.Tests/ReviewHandoffTests.cs` (`Category!=Live`).
