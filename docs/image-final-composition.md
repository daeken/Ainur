# Final image composition on the accepted minimal-release boundary

This candidate is composed in `.ainur/compose-images-final`, starting at exact
`01d6c043e270df43049dbc5482dd2f4acd10fa09` (766 paid-route invariant + title CAS +
drain recovery/admission gate). No shared-HEAD snapshots were copied. Prior frozen
478b46e, b0dfa87 and 1334155 worktrees and UI evidence remain unchanged.

## Explicit cherry-pick provenance

| Source | Composed commit | Scope |
| --- | --- | --- |
| 02d07a9 | 04b5aff | Scoped browser lifecycle/CDP/API prerequisites |
| e7de4b2 | d4c2dc7 | Typed browser-image checkpoint |
| f8014de | 5c1d652 | Upload/storage/HTTP checkpoint |
| 478b46e | 52985f4 | Common durable user/tool pixel integration |
| b0dfa874806e73088efc79f43a9b3cda83b98394 | 32cecd6 | Diagnostic privacy, zlib framing, offline UI fixture |
| 13341555a398871219e99912a1f6729b82d37937 | 6c49277 | Exact PNG scanline accounting |
| b5671acbbc08b6a51e2d4222c3460a5f04e119b6 | db4bd0d | Image conversation UI |
| e61b309f53aa4a3d964f5f913921bef29ddd3f41 | 1386855 | Project-scoped uncertain UI drafts |

One textual conflict occurred in `Api.cs`: adjacent request-record declarations.
Resolution retains **both** minimal-release `SetAgentModelRequest` Title/ExpectedTitle
and image `MessageRequest` Text/AttachmentIds/ClientMessageId. No title CAS endpoint
logic was replaced. All other picks merged without textual conflicts.

Critical hunk review against 01d6:

- `AinurRuntime.cs`: only browser import/disposal and legacy PostUserMessage delegation
  to the new overload. Admission gate, drain epoch, queued pending wakes and undrain
  behavior are unchanged. The image overload uses the existing gated Wake path.
- `SessionHost.cs`: only browser import/disposal, separate image inbox delivery and
  typed ToolResult images. Run gate/admission cancellation/epoch and operator-pause
  behavior remain minimal-release code.
- `Api.cs`: browser/image route maps, image send handler and message record only.
  Minimal title CAS implementation and request fields remain intact.
- `Organization.cs` is byte-identical to 01d6. Provider/gateway image files are
  byte-identical to accepted 133; there is no old provider-internal paid fallback.
  Each gateway fallback requotes; unknown-price API still cannot bypass cash admission.
- Web files are exact blobs from the selected two UI commits. No browser UI/WIP,
  synthetic Program.cs injection, live DB or additional feature was imported.

`docs/image-integration-manifest.md` retains the detailed original source provenance
and independent blocker/fix evidence. Scoped independent acceptance of 133 covers
PNG/privacy/route/wire, not final merge/UI/live visual understanding. Inherited
browser lifecycle/CDP/API prerequisites remain part of final composition review;
this document does not imply acceptance of the entire browser product or deployment.

## Composition-specific regression evidence

Two tests added at freeze, no production edits beyond the explicit picks:

- `Migration6Tests.Schema6To7PreservesFallbackPricesHistoryAndLegacyConversationProjection`:
  constructs real schema 6 via historical migrations; reopens through real Db to 7;
  preserves custom fallback/null prices/notes, prior model request and cost event,
  and legacy text conversation projection; new image/receipt tables start empty.
  Old-writer conversation SQL remains usable, reopening is idempotent. Existing
  schema-5 fallback migration tests remain, now explicitly expect migrations 6+7.
  This does not claim an old binary understands new image attachments.
- `DrainRecoveryTests.ImageReceiptSurvivesDrainTitleCasAndOperatorPauseWithoutDuplicateDispatch`:
  accepts an image while drained, applies title CAS, applies operator pause after
  receipt, retries identical image receipt, undrains without resuming that pause;
  explicit resume produces exactly one synthetic image response/cost event and no
  lost notification or image ref. Registry is all-synthetic; cash is zero, no model
  transport/credentials/network. Existing title/drain/HTTP/idempotency tests retained.

Commands from candidate worktree (logs `.ainur/image-final-evidence`):

- `dotnet build --nologo -v q`: success, 0 warnings/errors.
- Focused Migration6/DrainRecovery/AgentTitle/ConversationImage/Multimodal/OpenAiRoute
  filter: 39 passed / 0 failed.
- `dotnet test --filter 'Category!=Live' --nologo`: first run 271 passed / 2 failed:
  known 50ms CoordinationTests.ExpiredPauseIsRevokedAndReported race, and local Chrome
  profile-cleanup timeout in UnexpectedOwnedChromeExitCleansProfileBeforeSameKeyReacquires.
  Focused BrowserLifecycle+Coordination rerun: 6 passed / 0 failed. Full repeat:
  272 passed / 1 failed (only the known TTL race; browser lifecycle passed).
- `dotnet test --filter 'Category!=Live&FullyQualifiedName!~CoordinationTests.ExpiredPauseIsRevokedAndReported' --nologo`:
  272 passed / 0 failed. TTL race not hidden or repaired in this image composition.
- `npm ci` and `npm run build` in web: success. `git diff --check`: clean.

No live model calls, deployment, runtime controls or minimal-release source changes.
Final real-UI regression and independent composition review must use the exact freeze
commit containing this document, not an intermediate worktree state. Live screenshot
and user-image understanding proofs remain separately authorized gates.

## Published-main integration review boundary (source-only)

The history above describes **frozen 9c82b68** and its exact selected paths; accepted
5bdbf638 adds the narrowly scoped `web/src/Conversation.tsx` owner guard. The
subsequent published-main integration has a different ancestor and is not itself a
claim that any historical source commit has been deployed. Its base is published
29ba9e7c01fe30b26e7167b0e63048c733a1be70; independent changed-source review,
release validation, deployment and real-model vision remain separate gates.

A clean `git merge-tree --write-tree 29ba9e7 5bdbf638` exposed 11 textual conflicts:
`docs/conversation-images.md`, `src/Ainur.Core/Ainur.Core.csproj`,
`src/Ainur.Core/Context/ContextBuilder.cs`,
`src/Ainur.Core/Providers/OpenAiResponsesProvider.cs`,
`src/Ainur.Core/Runtime/BrowserImageInput.cs`,
`src/Ainur.Core/Runtime/ConversationImageValidation.cs`,
`src/Ainur.Core/Runtime/ConversationImages.cs`,
`src/Ainur.Core/Runtime/ModelGateway.cs`, `src/Ainur.Server/Api.cs`,
`tests/Ainur.Tests/ConversationImageTests.cs`, and
`tests/Ainur.Tests/DrainRecoveryTests.cs`. Each conflict uses the **accepted
5bdb image branch** blob: this simultaneously preserves the 766 provider-internal
cash invariant, 01d6 drain/title CAS and the 133 PNG/privacy boundaries. Exact
blob comparisons to 5bdb are required before this integration is accepted.

The two newer main browser UI source files `web/src/browserSource.ts` and
`web/src/SessionBrowser.tsx` are retained byte-for-byte from 29ba rather than
regressing to the older 5bdb browser source. `docs/browser.md` is also kept from
29ba, not silently deleted by the merge. Main's browser C# lifecycle/API/tests,
accounting catalog, schema migrations and `AinurRuntime.cs` were already identical
in the two parents. The result deliberately combines *newer browser-web source*
with the accepted image conversation/web owner-guard source. This browser-file
choice and any cross-feature effects need independent changed-source review; the
old "web exact selected blobs" statement above applies to the historical 9c82
checkpoint only. No new production code or browser capability is introduced by
manual conflict resolution. The README update distinguishes synthetic Chrome
API/storage and scripted model-wire verification from undeployed source and genuine
model understanding; it must not claim perception merely from encoded image input.
