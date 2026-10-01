# Ainur Initial Model Catalog

This is the starting model inventory for Ainur, based on the local configuration in `~/projects/FlatlineProxy` inspected on October 1, 2026. It records configured models and routes, with proposed assignments for the first team. No live provider calls or entitlement checks were performed. Availability, current billing rates, and complete provider capabilities still require validation when the integrations are implemented.

**Assignment policy (2026-10-01).** The project owner reports that the DeepSeek V4 Pro and V4 Flash ids no longer exist upstream and are now served by the V4.1 Flash route, so Ainur standardizes on a single DeepSeek model: `deepseek-v4.1-flash` for managers, specialists, compaction, and the inexpensive service model (`RuntimeOptions.ManagerModelId`, `SpecialistModelId`, `CheapModelId`, `ContextPolicy.CompactorModelId`). `deepseek-v4-pro` and `deepseek-v4-flash` stay in the catalog as disabled rows because historical `model_requests` reference them and carry recorded prices; they must not receive new assignments. The inventory, routes, and rates below remain an accurate record of the inspected FlatlineProxy configuration as of October 1, 2026, which is a fact independent of this assignment policy.
The primary source is the local, untracked `flatline.json`, which is the proxy's default configuration file. The checked-in example is different. FlatlineProxy source HEAD was `5f3ab4b`; that commit does not capture the local configuration. Source links below refer to this machine's adjacent FlatlineProxy checkout. Only model identifiers, routing behavior, capability metadata, and configured rates are summarized here; credentials and endpoint details are excluded.

## Proposed starting assignments

| Role | Initial model | Reason for starting here |
| --- | --- | --- |
| Root manager | `gpt-6` | A current configured management candidate; delegates detailed work. |
| Implementation specialist | `claude-sonnet-5` | A configured specialist candidate that exercises a different provider from the root. |
| Independent verification specialist | `gpt-5.6-sol` | A configured coding model that gives verification a separate model from implementation. |
| Routine knowledge worker | `deepseek-v4.1-flash` | The single DeepSeek route in use since 2026-10-01 (see the assignment policy note above). |

These assignments are proposed defaults, not claims of benchmarked suitability or globally optimal cost. A manager can change any assignment, reasoning setting, or team structure. Stronger models, lower-cost alternatives, and alternative providers below remain selectable. The routine knowledge-worker model is also a proposed starting choice for the inexpensive tool-finder service. If a route is unavailable, select an explicit alternative and record its identity and price; do not silently substitute a different model under the original model's name.

## OpenAI route assignment and model-level fallback (2026-10-01)

**Owner assignment policy** (ChatGPT subscription is primary; the OpenAI platform API key is fallback):

| Role | Model | Reasoning |
| --- | --- | --- |
| Root manager | `gpt-6-astra` | `max` |
| Subordinate, deep reasoning | `gpt-6-astra` | `high` |
| Subordinate, raw/mechanical work | `gpt-6.1-sol` | default |
| OpenAI route unavailable | `deepseek-v4.1-flash` | — |

**Model-level fallback.** Each enabled subscription row (`gpt-6`, `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`,
`gpt-5.6-luna`, `gpt-6.1-sol`) carries `fallback_model_id` pointing at its `-api` twin (same upstream slug,
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

## Configured model inventory

| Provider family | Model identifiers | Observed routing |
| --- | --- | --- |
| OpenAI | `gpt-6`, `gpt-6-astra` | ChatGPT subscription routes are configured. |
| OpenAI | `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5` | Subscription and API routes are configured. |
| Anthropic | `claude-fable-5`, `claude-opus-5`, `claude-opus-4-8`, `claude-sonnet-5`, `claude-haiku-4-5` | Subscription and API routes are configured; the paid Anthropic API provider is disabled in the inspected local configuration. |
| Anthropic | `claude-opus-4-6` | Listed in the catalog; the configured request key is `anthropic`, which targets this upstream model. |
| xAI | `grok-4.5` | Listed in the catalog; the configured request key is `xai`, which targets this upstream model. |
| Z.AI | `glm-5.1`, `glm-5.2`, `glm-5.3` | Direct Z.AI and OpenRouter routes are configured; OpenRouter uses `z-ai/<model-id>`. |
| DeepSeek | `deepseek-v4-pro`, `deepseek-v4-flash` | Direct routes use matching model identifiers; OpenRouter uses `deepseek/<model-id>`. |
| DeepSeek | `deepseek-v4.1-flash` | The direct upstream identifier is `deepseek-flash`; OpenRouter uses `deepseek/deepseek-v4.1-flash`. |

Additional configured request keys include `openai`, targeting `gpt-5.6-sol`, and `deepseek`, targeting `deepseek-v4-pro`. The local routing policies also contain `gpt-daybreak-blue-latest`, `gpt-daybreak-red-latest`, `gpt-reserve`, and `codex-auto-review` on subscription routes. Retain these as optional inventory requiring capability and entitlement verification, rather than assigning them ordinary project work by default.

The inspected local policies do not contain `gpt-6-sol` or `gpt-6-luna`. Do not add them merely because another UI or tool list exposes those names. Likewise, the example Codex configuration's selection of `gpt-5.6-sol` is not evidence that it is the user's currently selected model.

Flatline requires an exact routing-policy key. A request for catalog slug `grok-4.5` or `claude-opus-4-6` therefore cannot be assumed to work through this configuration; use the existing `xai` or `anthropic` alias when invoking those models through Flatline, or configure the corresponding route explicitly. Ainur should retain the logical model identity while recording the actual alias and upstream model sent.

## Capability metadata

These are explicit local catalog declarations, not independently verified provider limits:

| Models | Declared context tokens | Explicit local modality |
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

Flatline builds additional catalog entries by cloning the upstream catalog's first model and overlaying local fields. Unspecified fields can therefore describe that template instead of the named provider model. Ainur must track explicit values separately from inherited UI metadata. In particular, the inspected Anthropic translation does not map a reasoning or thinking setting into its Messages request, so a reasoning option displayed by the catalog does not establish that it reaches Anthropic.

The inspected provider configuration uses Responses for OpenAI, xAI, Z.AI, DeepSeek, and OpenRouter, and Messages for Anthropic. This describes the existing configured transports; Ainur's native adapters must validate their own request semantics and supported capabilities.

## Configured price references

The following nonzero prices appear in local routing policies, expressed in USD per million tokens. They are source-labeled estimates to validate, not a claim about current provider price sheets or settled charges. A missing cached rate remains unspecified.

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

Flatline selects among routes using approximate request size, expected output, configured prices, cache-residency heuristics, and priority tie-breaking. Its routing estimate is not a billing receipt. Ainur should calculate cash costs from reported usage and a versioned schedule for the actual selected route, then reconcile charges when a billing source is available. If the selected route or its cash prices cannot be determined, expose the estimate or unknown amount rather than assigning an invented exact cost. Admission and performance accounting still need a conservative effective-dollar quote.

For proxy-controlled fallback, that quote must cover a fixed route or a bounded set of routes and attempts, including cumulative cash and quota exposure. If the proxy cannot provide such a bound, use an Ainur-controlled route or disable its automatic fallback. Do not assume a subscription-only reservation covers a later paid API attempt. The inspected configuration supplies route candidates; this document does not establish that the proxy already implements Ainur's admission or per-attempt accounting contract.

## Subscription budget pricing

Subscription routes must receive a nonzero dollar valuation for consumed quota under the [subscription quota policy](spec.md#subscription-quota-valuation). Use a validated API-equivalent reference schedule or an explicitly configured conservative fallback, apply an uncertainty or subsidy premium, and increase the charge when quota is scarce. The effective charge may deliberately exceed estimated cash expense; this is Ainur's allocation policy, not a claim about the provider's operating costs.

Where reliable per-request quota estimates exist, also value the fraction consumed in each relevant window. The policy takes the maximum of the adjusted reference price and the values implied by overlapping windows, while enforcing all windows independently. Shared account telemetry includes other projects and clients, so aggregate percentage changes cannot be assigned directly to one request. Record the valuation version, quote-time observations, premium, and uncertainty with each charge.

The inspected FlatlineProxy configuration provides no validated quota-window dollar values, scarcity schedule, or nonzero reference pricing for every subscription model. Those remain implementation configuration to supply, rather than facts inferred from its zero rates. Managers compare effective dollar charges while the UI separately reports actual or estimated cash and quota headroom. Actual paid API fallback must remain within the project's cash ceiling as well as its effective budget.

## Source references

References point into the local FlatlineProxy checkout inspected for this draft:

- Runtime configuration selection: [src/main.rs](/Users/daeken/projects/FlatlineProxy/src/main.rs:43).
- Provider configuration and disabled Anthropic API provider: [flatline.json](/Users/daeken/projects/FlatlineProxy/flatline.json:4) and [Anthropic provider](/Users/daeken/projects/FlatlineProxy/flatline.json:57).
- GPT 6 routes: [gpt-6](/Users/daeken/projects/FlatlineProxy/flatline.json:367) and [gpt-6-astra](/Users/daeken/projects/FlatlineProxy/flatline.json:380).
- Other OpenAI routes: [gpt-5.6-sol](/Users/daeken/projects/FlatlineProxy/flatline.json:608), [gpt-5.6-terra](/Users/daeken/projects/FlatlineProxy/flatline.json:566), [gpt-5.6-luna](/Users/daeken/projects/FlatlineProxy/flatline.json:283), and [gpt-5.5](/Users/daeken/projects/FlatlineProxy/flatline.json:241).
- Anthropic routes and prices: [Fable 5](/Users/daeken/projects/FlatlineProxy/flatline.json:427), [Opus 5](/Users/daeken/projects/FlatlineProxy/flatline.json:262), [Opus 4.8](/Users/daeken/projects/FlatlineProxy/flatline.json:406), [Sonnet 5](/Users/daeken/projects/FlatlineProxy/flatline.json:469), and [Haiku 4.5](/Users/daeken/projects/FlatlineProxy/flatline.json:325).
- Request aliases: [Anthropic](/Users/daeken/projects/FlatlineProxy/flatline.json:194), [xAI](/Users/daeken/projects/FlatlineProxy/flatline.json:215), [OpenAI](/Users/daeken/projects/FlatlineProxy/flatline.json:346), and [DeepSeek](/Users/daeken/projects/FlatlineProxy/flatline.json:173).
- GLM routes: [5.1](/Users/daeken/projects/FlatlineProxy/flatline.json:587), [5.2](/Users/daeken/projects/FlatlineProxy/flatline.json:490), and [5.3](/Users/daeken/projects/FlatlineProxy/flatline.json:448).
- DeepSeek routes and prices: [V4 Pro](/Users/daeken/projects/FlatlineProxy/flatline.json:304), [V4 Flash](/Users/daeken/projects/FlatlineProxy/flatline.json:511), and [V4.1 Flash](/Users/daeken/projects/FlatlineProxy/flatline.json:532).
- Local catalog declarations: [catalog_models](/Users/daeken/projects/FlatlineProxy/flatline.json:632).
- Catalog template inheritance: [src/main.rs](/Users/daeken/projects/FlatlineProxy/src/main.rs:261).
- Exact route-key matching and route-cost estimation: [src/routing.rs](/Users/daeken/projects/FlatlineProxy/src/routing.rs:47).
- Reasoning mappings: [src/adapters.rs](/Users/daeken/projects/FlatlineProxy/src/adapters.rs:423).
- Anthropic request translation: [src/adapters.rs](/Users/daeken/projects/FlatlineProxy/src/adapters.rs:521).
