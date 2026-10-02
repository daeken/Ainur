# Conversation image uploads (PNG-only initial increment)

## Trust and storage

The server currently assumes a trusted loopback operator. This feature enforces real
project/current-root-primary-session scope; it does **not** introduce multiuser login
or claim that knowing an API URL authenticates an operator. There is no arbitrary
artifact-hash/file lookup endpoint. User uploads belong to actual user messages;
browser screenshots remain untrusted tool output, never user instructions.

Only PNG is accepted: MIME `image/png`, matching signature/chunk framing/checksums,
single-frame, at most 2 MiB encoded, 2048 pixels per edge and 3,000,000 decoded pixels.
Skia performs a full pixel decode after header limits. Truncated/malformed data,
APNG animation chunks, trailing payloads, and compressed profile/text metadata
(`iCCP`, `zTXt`, `iTXt`) are rejected. JPEG, WebP, SVG, HTML, and remote URLs are not
supported; there is no silent conversion. Decoder failures do not create uploads.

Validated bytes live in the existing content-addressed ArtifactStore. Migration 7
stores opaque upload IDs, project/session ownership, hash, dimensions, MIME, byte
count, timestamps and eventual conversation binding. API metadata omits the artifact
hash. Journals and conversations contain references, never encoded pixels.

Per project the retained quota is **128 uploads / 256 MiB**. Every accepted upload
consumes quota even if identical to another upload. Deleted and expired rows remain
as quota-accounted tombstones. Draft references expire after 24 hours; sent references
do not expire. There is no automatic immutable-artifact garbage collection. Deleting
a draft does not delete potentially shared content-addressed bytes. Metadata is
committed before artifact storage, so filesystem failure consumes quota and leaves a
missing-content reference rather than unaccounted orphan bytes. Such references fail
content retrieval/send explicitly. Administrative reclamation is outside this change.

## API

All paths below are under `/api/v1`.

- `POST /projects/{id}/conversation/images`: raw bytes, `Content-Type: image/png`
  (not multipart). Returns `id`, `mime_type`, `width`, `height`, `bytes`,
  `content_url`, scope/timestamps. Body is bounded even without Content-Length.
- `GET /projects/{id}/conversation/images/{imageId}/content`: PNG bytes with
  `X-Content-Type-Options: nosniff` and `Cache-Control: no-store`. Foreign,
  deleted, expired, or wrong-session references are not found.
- `DELETE /projects/{id}/conversation/images/{imageId}`: tombstones an unbound draft,
  returns 204. A sent attachment cannot be deleted (409).
- `POST /projects/{id}/conversation`: existing `text`, optional `attachment_ids`
  (ordered list of at most two distinct IDs), optional `client_message_id`.
  Text or images must be nonempty. Image-only messages are supported.
  **Image messages require a stable client_message_id**, 1..128 characters,
  no control characters. Returns the original ConversationEntry including
  `attachments` metadata. `GET /projects/{id}/conversation` also returns it;
  legacy entries have an empty attachments array.

With a client_message_id, exact text plus ordered attachment IDs is the retry key's
content. The same project/key and identical content returns the original receipt
without a second notification or wake. A reused key with different content returns
409. Keep the same key/payload after a lost HTTP response; do not generate a new key
on each retry. Text-only clients without a key retain the existing trimmed-HTTP-text
behavior and no idempotency guarantee. With a key, text is preserved exactly.

Binding, receipt, conversation, and notification are one DB transaction. All refs,
bytes, quotas and scope are checked before message persistence or wake; one ref cannot
be bound to two sends. Failed sends do not partially append a conversation or start a
turn. Draft upload does not wake an agent.

Errors use `{ "error": "..." }`: 400 for unsupported/invalid/oversize PNG, malformed
send, quota exhaustion or unusable content/reference; 404 for project/content/delete
scope not found; 409 for conflicting retry key or already-bound image. No response
includes encoded image bytes except the authorized binary content endpoint.

## Dependency evidence

Decoder packages are pinned to `SkiaSharp` and
`SkiaSharp.NativeAssets.Linux.NoDependencies` **3.119.4**, MIT-licensed package
wrappers. SkiaSharp pulls matching macOS and Win32 native assets; Linux native assets
are explicitly referenced. Native Skia and bundled dependencies retain their own
third-party notices in the distributed package license; MIT is not a claim that all
native components share one license. Package RID assets include osx, Windows
x86/x64/arm64, and Linux glibc/musl x64/arm/arm64. Current validation executes the
actual decoder on this macOS host; Linux and Windows execution is not yet claimed.
`dotnet list src/Ainur.Core/Ainur.Core.csproj package --vulnerable --include-transitive`
reported no known vulnerable packages during implementation (NuGet advisory feed,
not an independent native security audit). ImageSharp's conditional license was
considered and rejected; no ImageSharp dependency is retained.

Compressed PNG framing is independently checked with pinned **SharpZipLib 1.4.2**
(MIT, NuGet package license expression; net6.0 asset has no transitive dependencies).
The inflater requires zlib end-of-stream, validates Adler-32 and requires no remaining
compressed input after consecutive IDAT chunks. Concatenated members, truncation,
bad checksums and trailing compressed payloads are rejected, not canonicalized:
the artifact hash still addresses the original accepted bytes. Input is capped at
2 MiB; inflation uses an 8 KiB scratch buffer and requires the exact scanline length
from IHDR color type, bit depth and interlace mode. Each nonempty pass contributes
`rows * (1 + ceil(columns * channels * bitDepth / 8))` bytes: one filter byte per
row plus byte-rounded packed samples. Adam7 uses all seven passes, skipping passes
with zero rows or columns. Checked arithmetic rejects both surplus and missing
inflated bytes, even inside a checksum-valid single zlib member. The existing
RGBA16/Adam7 upper bound still follows from the dimensions; at most one extra
buffer is decoded to detect excess. Every successful loop makes output progress;
zero progress before stream-end fails closed. Full Skia decode is still required.

## Model delivery and bounded history

SessionHost resolves attachments only from the persisted, bound user-message
notification, and records typed artifact references on that user item. Image-bearing
messages are not merged with agent inbox notices. Legacy text-only batches remain
unchanged. Browser images are recorded on their original tool-result item instead.
Neither path writes base64 to session history. The gateway requires a matching source
item in the calling project/session and, for user images, the original conversation
binding. It verifies hash, full decode and metadata again before dispatch.

A normal request projects at most **four images / 8 MiB**, each at most 2 MiB.
One current, non-elided tool screenshot after the latest user/notice has priority;
remaining slots take whole user image messages newest first. Older user messages
receive an explicit pixel-omission marker, not silent loss. Original references stay
in history. The latest user image message is restored across text compaction;
compactor transcripts explicitly say pixels are not included, and text summaries
must not infer visual content. This prevents the third or later image send from
permanently exceeding the request cap. Older tool screenshots require a fresh
browser_screenshot; they are not silently treated as currently visible.

OpenAI Responses sends user uploads as actual user `input_image` parts. Tool images
are adjacent to their string `function_call_output` with an explicit UNTRUSTED TOOL
OUTPUT caption. For image-bearing requests, raw request/response artifacts and
provider error diagnostics are fixed omission markers, not provider-controlled JSON,
SSE, transport text or substrings sanitized by regex. This includes valid escaped,
split and malformed diagnostic echoes. HTTP status, typed usage, locally generated
request identifiers and billing-uncertainty classification are preserved. Provider
model-name/incomplete-reason diagnostic strings are not retained for image requests.
Ordinary generated assistant text, reasoning, tool arguments and citations remain
functional model output; they are **not** generally redacted. This is not a guarantee
that a model cannot deliberately echo image data in its generated content.
Only OpenAI Responses image routes are supported; unsupported direct routes or any
unsupported configured fallback fail explicitly before HTTP, not as a text-only
request. Every actual fallback still requotes under the existing cash admission
boundary; unknown-price API fallback remains blocked by a cash ceiling. Provider
usage determines settlement; the context estimate budgets 2048 tokens per image.

## Integration status

Offline tests cover upload/storage/HTTP, actual SessionHost delivery and browser
screenshot execution using local Chrome plus scripted model HTTP, request-wire pixels,
redacted persistence, authorization tampering, history/compaction/restart replay,
unsupported providers and cash-unknown API fallback. These tests prove plumbing,
not that a real model understood an image. Independent review, real disposable UI
acceptance, and a separately authorized model vision proof remain release gates.
No production deployment or live model proof is authorized by the offline tests.
