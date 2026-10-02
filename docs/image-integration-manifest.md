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

## Post-review correction on frozen 478b46e (2026-10-02)

Independent review blocked the original candidate on escaped JSON diagnostic echoes
and trailing compressed IDAT payloads. A separate `.ainur/fix-image-review` worktree
preserves frozen 478b46e and applies only these fixes plus a test-only UI fixture:

- Image-bearing request diagnostics are now wholly omitted, not regex-sanitized JSON.
  This includes request/response artifacts, HTTP bodies and stream/transport exception
  text. Typed usage, status and billing uncertainty remain; ordinary generated model
  output is intentionally not generally redacted. See conversation-images.md.
- SharpZipLib 1.4.2's managed zlib inflater proves checksum, stream-end and zero unused
  input across IDAT chunks before Skia decode. Original byte hashes are retained.
  NuGet package metadata declares MIT, repository commit
  `33f64eb0f28cdd2b084cb822fcc224c7c5aba553`; selected net6.0 asset has no dependencies.
  `dotnet list src/Ainur.Core/Ainur.Core.csproj package --vulnerable --include-transitive`
  reported no known vulnerable packages using current NuGet advisory sources.
- `ImageBoundaryTests` adds escaped/split/malformed diagnostic and durable persistence
  regressions, original generated-content/billing semantics, checksum/truncation/
  concatenation/every-IDAT-split tests, decompression bomb rejection, near-2-MiB valid
  input (three validations bounded by 10 seconds) and maximum-pixel decode.
- `tests/Ainur.Tests/OfflineUiProviders.cs` is an explicitly injected disposable UI
  fixture, NOT production startup. Every catalog provider is synthetic or fail-closed;
  Responses uses an in-memory handler with synthetic auth and no external transport.
  Its accounting rows are synthetic test accounting, not real provider spending.

Evidence: Námo's unmodified four independent tests in temporary
`ZzIndependentImage.cs` failed 3/passed 1 against frozen 478b46e, then passed 4/4 after
fixes. The shipped persistence regressions retain those cases without committing
verifier-owned temporary source. Final shipped suite command:
`dotnet test --filter 'Category!=Live&FullyQualifiedName!~ZzIndependentImage' --nologo`
passed 170/170; build passed with 0 warnings/errors. Earlier full-suite runs hit the
known 50ms `CoordinationTests.ExpiredPauseIsRevokedAndReported` race; that test passed
alone and the final full shipped run passed without excluding it. No lifecycle edits.
Independent source acceptance, fresh UI composition, live vision and eventual explicit
composition atop the accepted drain-recovery release remain separate gates.

## Exact inflated scanline accounting follow-up on frozen b0dfa87

Independent review confirmed the diagnostic and after-zlib-member fixes but found
that a single checksum-valid zlib member could contain extra inflated pixel bytes.
The previous dimensional bound was safe for resource use but not an exact length
check; Skia ignores such suffixes. The isolated `.ainur/fix-png-scanlines` correction
requires the precise PNG scanline length from IHDR using checked arithmetic, all
15 legal color-type/bit-depth combinations, and all seven Adam7 passes (empty passes
contribute zero). Both underlength and surplus inflated streams are rejected before
Skia. No format support was added or removed, no canonicalization, and no provider,
runtime/lifecycle or accounting behavior changed.

Independent unmodified `ZzImageFollowup` against b0dfa87: 5 passed / 2 failed
(+1 and +8 inflated-byte cases). After this fix: 7/7 passed. Shipped tests add 90
valid-format cases (15 formats x ordinary/Adam7 x 1x1, 2x2, 9x11), each also asserting
rejection for missing one byte and surplus one/eight bytes. Test fixtures enumerate
an Adam7 pass-number grid independently rather than copying production arithmetic.
Combined boundary/follow-up run: 106/106 passed; temporary independent source is
retained outside the worktree in `.ainur/png-scanline-evidence`, not committed.
Final `dotnet build --nologo -v q`: success, 0 warnings/errors.
Final `dotnet test --filter 'Category!=Live' --nologo`: 260 passed / 0 failed.
`git diff --check`: clean. Logs are in `.ainur/png-scanline-evidence`.
Frozen b0dfa87/478 candidates remain intact. Independent re-review and later explicit
composition onto the accepted drain-recovery release remain required.
