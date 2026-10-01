# OpenAI backend (subscription-first, API fallback)

Ainur's `openai` provider speaks the **OpenAI Responses API** and can authenticate two ways:

1. **Subscription (primary)** — your ChatGPT Pro subscription, via the Codex backend endpoint.
2. **API key (fallback)** — an OpenAI platform API key.

The design decision is recorded in `decisions/openai-backend` (knowledge store).

## How to enable

The provider is registered in `AinurRuntime.DefaultProviders()` (`src/Ainur.Core/Runtime/AinurRuntime.cs`)
and the catalog rows are enabled by default. No action is needed if a credential source exists; the provider
resolves credentials at request time.

Choose the auth route with `AINUR_OPENAI_ROUTE` (default `auto`):

| Value          | Behavior |
|----------------|----------|
| `auto`         | Subscription when a usable subscription credential exists, else API. Fails over subscription → API *within one attempt* only before any output was emitted and only when the upstream reported no usage (401/403/402/429, auth-refresh failure, connect error). |
| `subscription` | Subscription only. Never bills the API key. |
| `api`          | API key only. Fails fast with a clear error if no key is configured. |

## Credential sources

**Subscription** (`~/.codex/auth.json`, override with `AINUR_CODEX_AUTH`):

- Read at request time. Uses `tokens.access_token` as the OAuth bearer and `tokens.account_id` as the
  `chatgpt-account-id` header (falling back to the JWT `https://api.openai.com/auth.chatgpt_account_id` claim).
- The access-token JWT is decoded locally (no signature check) for `exp`, `iss`, `client_id`. When
  `exp - now < 5 min`, the token is refreshed via `POST {iss}/oauth/token` and the refreshed token set is written
  back **atomically** (temp file + rename, mode 0600, unknown fields preserved) so your Codex install stays usable.
- If refresh fails but the current token is still valid, the request proceeds with the current token; if the token is
  expired and refresh fails, the subscription credential is treated as unusable (and `auto` falls back to the API route).
- Token values are never written to logs, DB rows, artifacts, or commits.

**API key** (env `OPENAI_API_KEY`, then macOS keychain service `ai.openai.api`):

- Mirrors the DeepSeek/Zai credential resolution (see `Credentials.cs`).
- The subscription OAuth token is **not** a platform credential and is never sent to `api.openai.com`.

## Endpoints

- Subscription: `https://chatgpt.com/backend-api/codex/responses`
- API: `https://api.openai.com/v1/responses`

## Model-level fallback

Each enabled subscription catalog row links to its `-api` twin through `models.fallback_model_id` (migration 6).
`ModelGateway.CallAsync` retries the fallback only when the primary attempt failed without billing
(`ProviderException.MayHaveBilled == false`) and the failure class is eligible (auth 401/403, quota 402/429, upstream
5xx, timeout/unavailable, model-unavailable), and never after any output delta reached the caller or a billed
reservation was committed. Every attempt is its own `model_request` row with its own quote and settlement: the
subscription attempt settles cash=0, the `-api` attempt settles with `cash_basis=unknown` (no published prices).
See `docs/model-catalog.md` for the fallback semantics and the owner's route-assignment policy.

## Known risks

- **Unofficial use.** Driving the ChatGPT subscription backend from a non-Codex client is unofficial and may break or
  violate OpenAI's terms. It was requested explicitly by the project owner; it is isolated behind this adapter and can
  be disabled via config (`AINUR_OPENAI_ROUTE=api` plus an API key).
- **Refresh-token rotation.** The adapter may rotate the refresh token in `~/.codex/auth.json`, which could desync a
  running Codex CLI. Write-back is atomic and preserves unknown fields, but this is a known uncertainty.
- **API route not live-verified.** `OPENAI_API_KEY` is not set on the development machine and no keychain entry exists,
  so the API route is covered by scripted-HTTP unit tests only, not a live call. (The owner's platform key currently
  returns `insufficient_quota` 429 on `api.openai.com` — the 62,500 credits are subscription-continuation credits, not
  platform API credits — so the subscription route remains primary.)

## Native web search (opt-in)

Set `ProviderRequest.EnableWebSearch = true` (or `ModelCall.EnableWebSearch = true`
through the accounting gateway) to append exactly `{"type":"web_search"}` to the
Responses tools array, alongside ordinary function tools. It is off by default.
For agent turns, hosts can set `RuntimeOptions.EnableOpenAiWebSearch = true`; only
OpenAI sessions receive this capability, not compaction or background calls.
The model chooses whether to search; no local function invocation is dispatched.

`ProviderResponse.WebSearchCalls` preserves completed native search items, including
`action.query`, `action.queries`, or `action.url` when supplied upstream.
`ProviderResponse.Annotations` preserves text annotation events, including URL
citation titles, URLs and text offsets. These are caller-facing metadata; the
current agent transcript retains the model text, not a separate citation UI.
Full events also remain in `RawResponse` (and the gateway response artifact).

Retrieved pages count as **input tokens**. A trivial live endpoint probe reported
12,810 input tokens (4,096 cached), not merely the prompt's length. The provider
passes full reported usage to the gateway, whose settlement uses that usage rather
than the preflight estimate. Preflight reservations cannot predict retrieved-page
size and are not a hard cap on final usage. No invented per-search fee is added;
API rows without verified prices continue to have unknown cash cost. Native search
status events and items are not function calls and never request local execution.