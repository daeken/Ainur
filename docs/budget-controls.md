# Project budget controls and accounting

Effective dollars are valuation/accounting measures, not cash payments. Projects can
have **No effective limit** while independently restricting cash admission. All cost
events retain project, agent, objective, session and sponsor attribution in either mode.
No efficiency score is inferred from these amounts.

## HTTP contract

`POST /api/v1/projects` accepts existing `budget_dollars` plus optional
`no_effective_limit` and `cash_ceiling_dollars`.

- Omitted/null effective fields retain the legacy default ($5 unless runtime configured otherwise).
- `no_effective_limit: true` or legacy `budget_dollars: 0` means no effective limit.
- A positive amount with `no_effective_limit: true` is a 400 conflict.
- `no_effective_limit: false` requires a resulting positive effective limit.
- Cash omitted/null on creation means no cash ceiling; `cash_ceiling_dollars: 0` means **$0 cash**, not unlimited.

`PATCH /api/v1/projects/{id}` accepts those fields plus existing `clear_cash_ceiling`.
Omitted/null amounts and omitted/null flags preserve existing values. Only explicit
`clear_cash_ceiling: true` removes an existing cash cap; supplying an amount together
with clear is a 400 conflict. A change to effective mode never changes cash controls.
All supplied fields are validated before mutation, and updates read/write the project
in one DB transaction. Negative, nonnumeric/nonfinite, overflow, and positive amounts
that round to zero nanodollars are rejected; no partial description/budget writes occur.

Project responses preserve `effective_budget_nanos` and `cash_ceiling_nanos` and add
`no_effective_limit` and nullable `effective_limit_nanos`. Nested `costs` and the
existing costs endpoint preserve their old fields and additionally expose:

- `no_effective_limit`, `effective_limit_nanos`, `effective_remaining_nanos` (null for no limit).
- `cash_remaining_nanos`: cap minus known spend and held reservations, or null when uncapped/unknown.
- `cash_remaining_status`: `no_ceiling`, `unknown_cost`, or `known`.
- `reserved_cash_unknown_count`: held requests whose cash quote is unknown, separate from settled `cash_unknown_count`.

Unknown cash is not zero. Display known cash and unknown counts explicitly. Remaining
amounts can be negative after an overrun; do not clamp them or show unlimited as 0%.
`costs` tool and system prompts now describe unlimited effective accounting and independent
cash controls, including unknown costs. Amounts are nanodollars; divide by 1,000,000,000.

## Cash admission safety and limits

Reservations are checked atomically, so simultaneous known-price calls cannot reserve
more than available headroom. A configured cash cap rejects unknown-priced cash-incurring
routes instead of treating their zero numeric reservation as free. This includes an API
fallback from a failed subscription attempt, before the fallback request is dispatched.
Existing unknown settled cash or held unknown-priced requests also prevent new
cash-incurring admissions under a cap. Zero-marginal-cash subscription calls remain
eligible even with a $0 cash cap or unknown historical cash; effective limits still apply.
Without a cash cap, unknown-priced calls remain allowed and are disclosed as unknown.

This is an **admission ceiling, not an absolute bank-balance guarantee**. Input/output
quotes are estimates; native web retrieval, image content, provider usage changes and
actual token usage can exceed them. Settlement preserves actual reported charges even
when that makes remaining cash/effective headroom negative. An already dispatched request
is not canceled when a cap is lowered or enabled. Unknown historical cash requires operator
resolution or an explicit cap-clear decision; this change does not invent missing prices
or reconcile an external account balance. SQL sums retain existing signed-64-bit storage;
per-field amount conversion and admission arithmetic reject/avoid overflow, but this is
not an arbitrary-precision lifetime ledger migration.
