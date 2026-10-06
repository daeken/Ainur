import type { SessionItem } from './api'

// Stored sequence is authoritative; timestamps need not be monotonic.
export function mergeTranscript(existing: SessionItem[], incoming: SessionItem[]): SessionItem[] {
  const bySequence = new Map(existing.map(item => [item.seq, item]))
  for (const item of incoming) bySequence.set(item.seq, item)
  const ids = new Set<string>()
  return [...bySequence.values()].sort((a, b) => a.seq - b.seq).filter(item => {
    if (ids.has(item.id)) return false
    ids.add(item.id)
    return true
  })
}

export function atTranscriptTail(scrollTop: number, clientHeight: number, scrollHeight: number): boolean {
  return scrollHeight - scrollTop - clientHeight <= 48
}

export function transcriptCursor(items: SessionItem[]): number {
  return items.at(-1)?.seq ?? 0
}

export function payloadText(value: unknown): string {
  if (typeof value === 'string') return value
  return value == null ? '' : JSON.stringify(value, null, 2)
}
