# Isolated image integration candidate

## Boundary and provenance

Base: `766fa9f930` (remove provider-internal paid fallback, based on deployed `e022406`).
Worktree: `.ainur/integrate-images`. This is a source-review candidate, not a deployed
release. No unrelated shared-tree ancestry, UI, effort-policy change or title fix was
imported. In particular `2867c66` title CAS and `b5671ac` image UI remain separate.

Composition checkpoints, in order:

1. `02d07a9e8e9df2026795def9a874bf0144780e65`: selected browser lifecycle prerequisites
   from shared `0282125` state, including `16be3a9` owned process/profile disposal and
   `d63b1b4` unexpected-Chrome-exit cleanup. Explicit paths:
   - `src/Ainur.Core/Browser/{BrowserManager,BrowserSession,CdpConnection}.cs`
   - `src/Ainur.Core/Runtime/{AinurRuntime,SessionHost}.cs` (browser wiring/disposal only)
   - `src/Ainur.Server/{Api,BrowserApi}.cs` (BrowserApi registration only in Api)
   - `tests/Ainur.Tests/{BrowserLifecycleTests,BrowserTests}.cs`
2. `e7de4b2`: explicit ten-path typed-browser-image checkpoint from frozen `3208628`,
   transplanted as diffs without restoring the old paid-route behavior:
   - `src/Ainur.Core/Context/{ContextBuilder,Payloads}.cs`
   - `src/Ainur.Core/Providers/{ChatCompletionsProvider,OpenAiResponsesProvider,Provider}.cs`
   - `src/Ainur.Core/Runtime/{BrowserImageInput,ModelGateway,SessionHost}.cs`
   - `src/Ainur.Core/Tools/{BrowserTools,ToolContract}.cs`
3. `f8014de`: separate upload/storage checkpoint from `cab575d76dfcea7092ac2d46937e80a866299e80`:
   - `src/Ainur.Core/Ainur.Core.csproj`
   - `src/Ainur.Core/Model/Records.cs`
   - `src/Ainur.Core/Persistence/{Migrations,Store}.cs`
   - `src/Ainur.Core/Runtime/{ConversationImages,ConversationImageValidation}.cs`
   - `src/Ainur.Server/{Api,ConversationImageApi}.cs`
   - `tests/Ainur.Tests/ConversationImageTests.cs`
   - `docs/conversation-images.md`
4. Common integration patch atop those checkpoints: user notification -> typed user
   item -> bounded context -> same-session/source authorization -> hash-checked PNG
   hydration -> Responses wire image. It also validates browser pixels before artifact
   persistence; retains tool-call linkage and untrusted caption; preserves legacy text
   inbox batching; fixes schema-version expectations to migration 7; adds hermetic tests.

Checkpoints 1–3 were explicitly UNVERIFIED WIP at creation. Existing browser lifecycle,
CDP, browser API and inherited typed-image code are included in the review boundary,
not independently accepted merely by transplantation. The combined candidate requires
independent review. Browser web/UI/docs were not imported; this manifest does not claim
acceptance of the broader browser product.

## Implementer validation (2026-10-02, macOS)

Run from the isolated worktree root:

- `dotnet build --nologo -v q`: success, 0 warnings, 0 errors.
- `dotnet test --filter 'Category!=Live' --nologo`: 161 passed, 0 failed, 0 skipped.
- `dotnet test --filter 'Category!=Live&FullyQualifiedName~MultimodalTests' --nologo`:
  6 passed. Includes actual SessionHost loop, actual local Chrome screenshot, and
  scripted Responses HTTP (no remote model). Chrome is installed on this validation
  host; the Chrome-specific test returns early on hosts without Chrome.
- `dotnet list src/Ainur.Core/Ainur.Core.csproj package --vulnerable --include-transitive`:
  no vulnerable packages reported by current NuGet sources.
- `git diff --check`: clean.

`MultimodalTests` evidence separates user uploads from browser provenance:

- `ActualHostLoopExecutesBrowserScreenshotAndDispatchesUserAndToolPixels`: image-only
  durable conversation notification enters normal host loop; first scripted request
  contains user image; model-scripted tool call executes browser_screenshot against a
  local data URL; second request contains both exact PNGs, high effort and untrusted
  tool caption; durable requests are redacted, marginal cash is zero.
- `NormalInboxAndRecordedToolResultReachWireWithDistinctProvenanceAndRedactedPersistence`:
  exact typed roles, function_call_output adjacency, high effort, actual data URLs only
  on wire, no base64 in items/journal/request artifact, zero-cash subscription routing.
- `DurableReplayProjectionCompactionAndNewUploadsNeverWedgeOrSilentlyDropHistory`:
  repeated uploads, four-image cap, visible older-pixel omission, text-only compactor,
  latest upload across full compaction and runtime restart.
- `TamperedSourceRoleProjectUnboundUploadMissingAndMalformedArtifactsFailBeforeAdmission`:
  cross-project/role/source forgery, unbound draft, missing/malformed artifact and
  oversized image-count rejection before any HTTP or cash reservation.
- `UnsupportedDirectAndFallbackNeverDispatchTextOnlyAndCashUnknownApiFallbackRemainsBlocked`:
  unsupported provider fails closed; eligible subscription error does not dispatch
  unknown-price API transport under cash ceiling; no stale reservations.
- `ProviderRejectsUnhydratedImagesAndRedactsEchoedErrorBeforeGatewayJournal`:
  missing hydration, unsupported serializer, HTTP error image-data redaction.

`ConversationImageTests` separately covers real in-process HTTP, validation, scope,
ordered binding, restart replay, concurrent idempotent retries, quotas/tombstones,
legacy trimmed text, and rejection behavior. Existing 766 route/admission tests remain
in the full offline suite. No live test suite, paid API probe, live DB write or runtime
restart occurred for this candidate.

## Remaining gates and limitations

- Independent exact-candidate review, disposable real UI acceptance and authorized
  live image-only vision proof have not yet occurred. Scripted model HTTP proves byte
  delivery, not visual understanding. Browser screenshot-only canary is a separate gate.
- SkiaSharp 3.119.4 and matching native assets are pinned. Actual native decode is
  tested on this macOS host; Linux/Windows assets exist but execution on those platforms
  is not claimed. See conversation-images.md for licenses and trust/retention limits.
- Supported transport is OpenAI Responses; unsupported primary/fallback fails closed.
  Image eligibility is transport-based, so an upstream model lacking image support can
  still reject explicitly. No automatic conversion or silent text-only downgrade.
- No release approval is implied by the composition or implementer's offline results.
