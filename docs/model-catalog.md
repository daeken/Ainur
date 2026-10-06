# Ainur Initial Model Catalog

This is the starting model inventory for Ainur. It records seeded model identifiers, example routes and estimates, with proposed assignments for the first team. The initial inventory does not establish provider availability or entitlement. Current billing rates and complete provider capabilities require independent validation.

**Assignment policy (2026-10-01).** Ainur standardizes on `deepseek-v4.1-flash` for managers, specialists, compaction, and the inexpensive service model (`RuntimeOptions.ManagerModelId`, `SpecialistModelId`, `CheapModelId`, `ContextPolicy.CompactorModelId`). `deepseek-v4-pro` and `deepseek-v4-flash` remain disabled catalog rows because historical `model_requests` can reference them and their recorded prices; they must not receive new assignments. Validate upstream model availability for each deployment.
The seeded inventory is maintained in `src/Ainur.Core/Accounting/ModelCatalog.cs`. Example proxy routes and capability declarations are not provider guarantees. Configuration outside this repository is not required to inspect the seeded catalog.

## Proposed starting assignments

| Role | Initial model | Reason for starting here |
| --- | --- | --- |
| Root manager | `gpt-6` | A current configured management candidate; delegates detailed work. |
| Implementation specialist | `claude-sonnet-5` | A configured specialist candidate that exercises a different provider from the root. |
| Independent verification specialist | `gpt-5.6-sol` | A configured coding model that gives verification a separate model from implementation. |
| Routine knowledge worker | `deepseek-v4.1-flash` | The single DeepSeek route in use since 2026-10-01 (see the assignment policy note above). |

These assignments are proposed defaults, not claims of benchmarked suitability or globally optimal cost. A manager can change any assignment, reasoning setting, or team structure. Stronger models, lower-cost alternatives, and alternative providers below remain selectable. The routine knowledge-worker model is also a proposed starting choice for the inexpensive tool-finder service. If a route is unavailable, select an explicit alternative and record its identity and price; do not silently substitute a different model under the original model's name.

## OpenAI route assignment and model-level fallback (2026-10-01)

**Route assignment policy** (ChatGPT subscription is primary; API fallback is described here only for the explicitly configured gateway pairs, not ordinary subscription-only server startup):

| Role | Model | Reasoning |
| --- | --- | --- |
| Root manager | `gpt-6-astra` | `max` |
| Subordinate, deep reasoning | `gpt-6-astra` | `high` |
| Subordinate, raw/mechanical work | `gpt-6-sol` | default |
| OpenAI route unavailable | `deepseek-v4.1-flash` | — |

**Model-level fallback.** Each enabled subscription row (`gpt-6`, `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`,
`gpt-5.6-luna`, `gpt-6-sol`, `gpt-6.1-sol`) carries `fallback_model_id` pointing at its `-api` twin (same upstream slug,
`billing=api`). `ModelGateway.CallAsync` retries the fallback only when the attempt failed without billing
(`ProviderException.MayHaveBilled == false`) and the failure class is eligible (auth 401/403, quota 402/429, upstream
5xx, timeout/unavailable, model-unavailable), and never after any output delta reached the caller or after a billed
reservation was committed. Each attempt records its own `model_request` row with its own quote and settlement. There is
no api→subscription direction. A disabled row is never selected as a fallback target, and the gateway refuses to call a
disabled model.

**Pricing honesty for the openai rows.** No public per-token price has been sourced for these slugs, so every openai row
ships null rates. Subscription rows settle cash=0 (`billing=subscription`); the `-api` twins settle with
`cash_basis=unknown` (`Pricing.Settle`, no prices configured). Effective-dollar valuation still applies through the
conservative reference schedule. Do not invent per-token numbers for these slugs — source and cite real published prices
before filling them in.

## Model inventory and illustrative routing

| Provider family | Model identifiers | Illustrative routing |
| --- | --- | --- |
| OpenAI | `gpt-6`, `gpt-6-astra` | ChatGPT subscription routes are configured. |
| OpenAI | `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5` | Subscription and API routes are configured. |
| Anthropic | `claude-fable-5`, `claude-opus-5`, `claude-opus-4-8`, `claude-sonnet-5`, `claude-haiku-4-5` | Subscription and API transports are represented; enabling a paid route requires explicit configuration. |
| Anthropic | `claude-opus-4-6` | Listed in the catalog; the configured request key is `anthropic`, which targets this upstream model. |
| xAI | `grok-4.5` | Listed in the catalog; the configured request key is `xai`, which targets this upstream model. |
| Z.AI | `glm-5.1`, `glm-5.2`, `glm-5.3` | Direct Z.AI and OpenRouter routes are configured; OpenRouter uses `z-ai/<model-id>`. |
| DeepSeek | `deepseek-v4-pro`, `deepseek-v4-flash` | Direct routes use matching model identifiers; OpenRouter uses `deepseek/<model-id>`. |
| DeepSeek | `deepseek-v4.1-flash` | The direct upstream identifier is `deepseek-flash`; OpenRouter uses `deepseek/deepseek-v4.1-flash`. |

Proxy integrations may expose exact aliases such as `openai`, `anthropic`, `xai` or `deepseek` in addition to catalog identifiers. Additional optional model IDs must undergo capability and entitlement verification before ordinary assignment.

A model exposed by another UI or tool list does not prove that a proxy supports it. The direct OpenAI subscription route described below is a separate transport with its own availability requirements.

A proxy can require an exact routing-policy key. A catalog slug therefore does not establish a working proxy request; configure the route explicitly and retain the logical model identity while recording the actual alias and upstream model sent.

## Capability metadata

These are illustrative capability declarations, not independently verified provider limits:

| Models | Declared context tokens | Declared modality |
| --- | ---: | --- |
| `deepseek-v4-pro`, `deepseek-v4-flash` | 1,000,000 | Text |
| `deepseek-v4.1-flash` | 1,000,000 | Text and image |
| `glm-5.1` | 204,800 | Text |
| `glm-5.2`, `glm-5.3` | 1,048,576 | Text |
| Claude Fable 5, Opus 5, Opus 4.8, Opus 4.6, Sonnet 5 | 1,000,000 | Text |
| `claude-haiku-4-5` | 200,000 | Text |
| `grok-4.5` | 500,000 | No local override |
| `gpt-6`, `gpt-6-astra` | No local override | No local override |

The explicit context declarations use a 95 percent effective-context setting. Ainur must also budget its system instructions, tools, output allowance, and consultation material; a nominal context number is not an available input budget.

The DeepSeek catalog entries explicitly declare reasoning levels `low`, `high`, and `max`, with `high` as the default. The adapter additionally preserves an explicit `none`; maps `minimal` and `low` to `low`; maps `medium`, `high`, and `xhigh` to `high`; and maps `max` and `ultra` to `max`.

For GLM 5.3, the adapter maps `none`, `minimal`, and `low` to `low`; `medium`, `high`, and `xhigh` to `high`; and `max` and `ultra` to `max`. Its absent-effort default is `low`. These are adapter behaviors, not evidence that the same mapping applies to every GLM model or every direct API.

Catalog metadata can inherit values from templates rather than explicit model declarations. Track explicit values separately from inherited UI metadata. A displayed reasoning option does not prove that a provider adapter transmits it; verify the specific request semantics.

Provider integrations can use Responses-compatible or Messages-compatible transports. Each native adapter must validate its own request semantics and supported capabilities; a proxy transport selection is not sufficient evidence.

## Configured price references

The following historical configuration estimates are expressed in USD per million tokens. They require validation against current provider price sheets and are not settled charges. A missing cached rate remains unspecified.

| Route | Input | Cached input | Output |
| --- | ---: | ---: | ---: |
| DeepSeek direct V4 Pro | 0.435 | 0.003625 | 0.87 |
| OpenRouter V4 Pro | 0.435 | Unspecified | 0.87 |
| DeepSeek direct V4 Flash | 0.14 | 0.0028 | 0.28 |
| OpenRouter V4 Flash | 0.09 | Unspecified | 0.18 |
| DeepSeek direct V4.1 Flash | 0.30 | 0.006 | 1.20 |

Only the deepseek-v4.1-flash row is an active assignment rate as of 2026-10-01; the V4 Pro and V4 Flash rows are retained for historical settlement only.
| OpenRouter V4.1 Flash | 0.15 | 0.015 | 0.60 |
| Anthropic API Opus 4.8 and Opus 5 | 5.00 | Unspecified | 25.00 |
| Anthropic API Sonnet 5 | 3.00 | Unspecified | 15.00 |
| Anthropic API Haiku 4.5 | 1.00 | Unspecified | 5.00 |
| Anthropic API Fable 5 | 10.00 | Unspecified | 50.00 |

The Anthropic API rows describe configured but currently disabled routes. They do not price the enabled subscription routes. Many other policy rates are zero, including some API routes; those values do not prove that usage is free. Treat unvalidated zero values as unknown cash prices, never as zero budget charges for subscription consumption. Keep genuine zero marginal cash cost, allocated subscription expense, conservative effective dollar charges, and unknown cost distinct.

Proxy route selection may use request size, expected output, configured prices, cache-residency heuristics and priority. A routing estimate is not a billing receipt. Calculate cash costs from reported usage and a versioned schedule for the actual route, then reconcile charges when billing data is available. Unknown routes or prices must remain estimates or unknown amounts, not invented exact costs. Admission still needs a conservative effective-dollar quote.

For proxy-controlled fallback, that quote must cover a fixed route or a bounded set of routes and attempts, including cumulative cash and quota exposure. If the proxy cannot provide such a bound, use an Ainur-controlled route or disable its automatic fallback. Do not assume a subscription-only reservation covers a later paid API attempt. Illustrative route candidates do not establish that a proxy implements Ainur's admission or per-attempt accounting contract.

## Subscription budget pricing

Subscription routes must receive a nonzero dollar valuation for consumed quota under the [subscription quota policy](spec.md#subscription-quota-valuation). Use a validated API-equivalent reference schedule or an explicitly configured conservative fallback, apply an uncertainty or subsidy premium, and increase the charge when quota is scarce. The effective charge may deliberately exceed estimated cash expense; this is Ainur's allocation policy, not a claim about the provider's operating costs.

Where reliable per-request quota estimates exist, also value the fraction consumed in each relevant window. The policy takes the maximum of the adjusted reference price and the values implied by overlapping windows, while enforcing all windows independently. Shared account telemetry includes other projects and clients, so aggregate percentage changes cannot be assigned directly to one request. Record the valuation version, quote-time observations, premium, and uncertainty with each charge.

Quota-window dollar values, scarcity schedules and nonzero reference pricing require explicit validated configuration; they cannot be inferred from zero routing rates. Managers compare effective dollar charges while the UI separately reports actual or estimated cash and quota headroom. Actual paid API fallback must remain within the project's cash ceiling as well as its effective budget.

## Source and validation scope

The repository's seeded catalog, native provider adapters and offline tests are the inspectable references. Proxy-specific behavior described here is illustrative historical configuration, not a public provider contract or a claim that a proxy implements Ainur's admission/accounting requirements. Verify model availability, credentials, capability limits and published price schedules for each deployment; unverified rates remain estimates.

### Catalog reseeding and operator notes

Catalog-row `notes` are **code-owned** and replaced by `ModelCatalog.EnsureSeeded`
on startup. Operator annotations on a seeded id (for example, "pinned by operator")
will be lost. This is intentional here; no schema or seed merge policy is changed.
For operator-specific models and annotations, create a non-catalog row with a unique
id; reseeding leaves those rows untouched. Do not use seeded-row notes as durable
operator configuration.

Gateway fallback excludes request-validation failures (HTTP 400 and 422). The
stored row is re-read at call time so a stale enabled object cannot bypass a stored
disabled flag. `model.failover` references the failed attempt's actual request id.
If any output delta is observed, the attempt is treated as possibly billed even
when the provider labels its error unbilled: it settles the same conservative
input estimate as cancellation, never releases at zero or retries elsewhere.
This holds even when the caller does not subscribe to streaming callbacks.

### Direct OpenAI Sol 6 route (2026-10-01)

The exact upstream slug `gpt-6-sol` is now seeded as a direct OpenAI subscription
worker route, distinct from `gpt-6` and `gpt-6.1-sol`. Route configuration does
not establish availability or entitlement for another deployment. Verify the
route explicitly before assigning work; this is not a price or benchmark claim.

The primary links to `gpt-6-sol-api` (same exact upstream slug, API-key route).
Both retain null token rates; subscription settlement has zero marginal cash,
while the unverified API route retains unknown cash cost without verified prices.
The API twin is configuration, not an API availability claim. Historical
`gpt-6.1-sol` rows and attribution remain unchanged; no aliases or replacements
of historical ids were made. Delivery requires a clean runtime upgrade/reseed;
this source change does not mutate a running catalog or switch agents itself.