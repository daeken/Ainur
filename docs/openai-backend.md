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

## Known risks

- **Unofficial use.** Driving the ChatGPT subscription backend from a non-Codex client is unofficial and may break or
  violate OpenAI's terms. It was requested explicitly by the project owner; it is isolated behind this adapter and can
  be disabled via config (`AINUR_OPENAI_ROUTE=api` plus an API key).
- **Refresh-token rotation.** The adapter may rotate the refresh token in `~/.codex/auth.json`, which could desync a
  running Codex CLI. Write-back is atomic and preserves unknown fields, but this is a known uncertainty.
- **API route not live-verified.** `OPENAI_API_KEY` is not set on the development machine and no keychain entry exists,
  so the API route is covered by scripted-HTTP unit tests only, not a live call.
- **Billing attribution for in-attempt failover.** The within-one-attempt subscription→API fallback re-sends the same
  catalog row (billing `subscription`). Cross-route failover with per-route billing (`-api` twin rows and
  `models.fallback_model_id`) is a follow-up (model-level fallback) objective.
