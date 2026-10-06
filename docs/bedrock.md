# AWS Bedrock and Mantle

Ainur has opt-in OpenAI Responses adapters for the public AWS endpoints. They use AWS SDK SigV4 signing with a refreshable shared AWS profile. A profile may use `credential_process` with any helper that issues temporary credentials. Authentication is resolved lazily, and the SDK credential object is reused and asked for current credentials for each request. Access keys, session tokens, and Authorization headers are not included in request artifacts.

## Configuration

Configure the AWS profile on the machine running the supervisor. For example, in `~/.aws/config`:

```ini
[default]
region = us-west-2
credential_process = /absolute/path/to/your-credential-helper
```

The process must implement the AWS credential-process JSON protocol, including `Expiration` for temporary credentials. Verify it through `aws sts get-caller-identity --profile default`; refresh the host login if the helper cannot obtain new credentials. The supervisor inherits the profile settings and PATH, so use an absolute helper path in the profile. Ainur does not copy credentials from another machine.

Set these variables in the **supervisor environment** (shell syntax shown):

```sh
export AWS_PROFILE=default
export AWS_REGION=us-west-2
export AINUR_BEDROCK_ENABLED=1
export AINUR_MANTLE_REGION=us-east-1
export AINUR_ENGINEERING_ALLOW_API=1
export AINUR_ENGINEERING_MODEL=mantle-gpt-6.1-sol
export AINUR_MANAGER_MODEL=mantle-gpt-6.1-sol
export AINUR_SPECIALIST_MODEL=mantle-gpt-6.1-sol
export AINUR_CHEAP_MODEL=mantle-gpt-6.1-sol
```

For a systemd environment file omit `export`. Restart the service after editing the environment. Changing defaults does not retarget existing agents or sessions. New project creation reads the configured engineering default from the server and labels models as API- or subscription-billed.

## Routes

| Ainur model | Provider | Upstream model |
| --- | --- | --- |
| `bedrock-gpt-6.1-sol` | `bedrock` | `us.openai.gpt-6.1-sol` |
| `bedrock-gpt-6-astra` | `bedrock` | `us.openai.gpt-6-astra` |
| `mantle-gpt-6.1-sol` | `bedrock-mantle` | `openai.gpt-6.1-sol` |
| `mantle-gpt-6-astra` | `bedrock-mantle` | `openai.gpt-6-astra` |

- `bedrock` uses `https://bedrock-runtime.{AWS_REGION}.amazonaws.com/openai/v1/responses` and US geographic inference profile IDs. `AWS_DEFAULT_REGION` is accepted if `AWS_REGION` is unset; otherwise the default is `us-west-2`.
- `bedrock-mantle` uses `https://bedrock-mantle.{AINUR_MANTLE_REGION}.api.aws/openai/v1/responses`, falling back to the AWS region when no Mantle region is supplied. The `/openai/v1` path is required for these closed-weight OpenAI models. This adapter is not the `/v1` GPT-OSS adapter.
- Both use SigV4 signing service `bedrock`, include temporary session tokens, disable automatic HTTP redirects, send `store=false`, stream Responses events, and enforce the requested output-token limit.
- Tool call identifiers and function outputs are replayed through the existing Responses codec. Reasoning effort is passed through; model-specific unsupported efforts surface upstream errors.
- The direct `openai` provider remains subscription-only. AWS calls have their own provider IDs and are never relabeled as subscription usage.

As of 2026-10-05, the AWS model cards list Mantle GPT-6.1 Sol in `us-east-1` and GPT-6 Astra in `us-east-1`/`us-west-2`. Runtime and Mantle have independent availability and quotas. Published regional listings do not establish availability or entitlement for a particular installation. Verify the selected service, region and model before admitting work. There is no automatic cross-provider fallback configured.

## Accounting and current scope

AWS routes are API-billed. Input, cached-input, output and reasoning usage are recorded, with normal request reservation/settlement. The current scalar catalog pricing does not represent AWS's long-context tiers and cache-write rates, so the seeded AWS cash prices are deliberately unknown. Effective-dollar valuation uses the configured conservative fallback schedule. **A cash ceiling rejects unknown-price calls**, including these seeded routes; a project can use the existing no-cash-ceiling setting with or without a finite effective budget. Cash is shown as unknown, not zero. Full AWS tiered pricing remains follow-up work.

Supported in this adapter: text, client-side function tools, streaming, reasoning effort, shared-profile authentication. Image inputs and native server-side web search are rejected explicitly. Other Bedrock APIs/providers (Converse, Anthropic Messages, GPT-OSS `/v1`) are not implemented by this adapter.

## Verification

Offline coverage includes session-token rotation, SigV4 scope and payload hashes, both endpoint forms, tool-call replay, truncated-stream accounting, credential-error redaction, and opt-in API engineering onboarding without dispatch.

The live test uses a disposable home, performs a two-request calculator-tool round trip, and checks durable usage/accounting. It is excluded by the normal `Category!=Live` filter and additionally requires explicit opt-in:

```sh
AINUR_LIVE_BEDROCK=1 AWS_PROFILE=default AINUR_MANTLE_REGION=us-east-1 \
  dotnet test tests/Ainur.Tests -c Release --filter 'FullyQualifiedName~LiveBedrockTests'
```

References: [Responses API](https://docs.aws.amazon.com/bedrock/latest/userguide/inference-responses-api.html), [GPT-6.1 Sol](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-6-1-sol.html), [GPT-6 Astra](https://docs.aws.amazon.com/bedrock/latest/userguide/model-card-openai-gpt-6-astra.html).
