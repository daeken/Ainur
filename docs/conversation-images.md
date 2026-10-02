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

## Integration status

Upload/storage/HTTP tests are offline. Context projection, gateway authorization,
provider wire images, redacted captures, and bounded replay must pass the common
multimodal integration gate before declaring end-to-end image delivery supported.
No production deployment or real model proof is authorized by these API tests alone.
