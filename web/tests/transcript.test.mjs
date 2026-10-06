import { test } from 'node:test'
import assert from 'node:assert/strict'
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join, resolve } from 'node:path'
import { pathToFileURL, fileURLToPath } from 'node:url'

const root = resolve(fileURLToPath(new URL('..', import.meta.url)))
const dir = mkdtempSync(join(tmpdir(), 'ainur-transcript-tests-'))
let data
try {
  execFileSync(process.execPath, [join(root, 'node_modules/typescript/bin/tsc'), join(root, 'src/transcriptData.ts'), '--target', 'ES2022', '--module', 'ES2022', '--lib', 'ES2022,DOM', '--skipLibCheck', '--outDir', dir], { cwd: root })
  const module = join(dir, 'transcriptData.mjs')
  writeFileSync(module, readFileSync(join(dir, 'transcriptData.js')))
  data = await import(pathToFileURL(module))
} finally { rmSync(dir, { recursive: true, force: true }) }

const item = (seq, extra = {}) => ({ id: `item-${seq}`, session_id: 'session', seq, turn: Math.floor(seq / 3), kind: 'assistant', payload: { content: `speech ${seq}` }, created_at: 10000 - seq, token_estimate: 5, ...extra })

test('sequence order beats timestamps, overlaps/reconnects do not duplicate events', () => {
  const original = [item(1), item(2), item(3)]
  const merged = data.mergeTranscript(original, [item(5), item(3), item(4), item(2)])
  assert.deepEqual(merged.map(i => i.seq), [1, 2, 3, 4, 5])
  assert.deepEqual(data.mergeTranscript(merged, [item(4), item(5)]), merged)
  assert.equal(original.length, 3)
})
test('a disconnected interval is repaired using last durable sequence cursor', () => {
  const old = Array.from({ length: 1000 }, (_, n) => item(n + 1))
  assert.equal(data.transcriptCursor(old), 1000)
  const fresh = data.mergeTranscript(old, Array.from({ length: 20 }, (_, n) => item(n + 1001)))
  assert.equal(fresh.length, 1020)
  assert.equal(data.transcriptCursor(fresh), 1020)
  assert.equal(data.transcriptCursor([]), 0)
})
test('message, tool result, notice and summary all remain in stored order', () => {
  const kinds = ['user', 'assistant', 'tool_result', 'notice', 'summary', 'future_kind']
  assert.deepEqual(data.mergeTranscript([], kinds.map((kind, n) => item(n + 1, { kind })).reverse()).map(i => i.kind), kinds)
})
test('corrected overlap retains one event and stable event identity', () => {
  const changed = item(2, { payload: { text: 'correction' } })
  assert.deepEqual(data.mergeTranscript([item(1), item(2)], [changed]), [item(1), changed])
})
test('follow threshold is bounded; reading older history never qualifies as tail', () => {
  assert.equal(data.atTranscriptTail(952, 500, 1500), true)
  assert.equal(data.atTranscriptTail(951, 500, 1500), false)
  assert.equal(data.atTranscriptTail(0, 500, 5000), false)
  assert.equal(data.atTranscriptTail(1100, 500, 1500), true)
})
test('untrusted strings/entities are retained verbatim, structured detail stays inspectable', () => {
  const unsafe = '&amp; <img src=x onerror=alert(1)> `code`'
  assert.equal(data.payloadText(unsafe), unsafe)
  assert.equal(data.payloadText(null), '')
  assert.equal(data.payloadText({ text: unsafe }), JSON.stringify({ text: unsafe }, null, 2))
})
