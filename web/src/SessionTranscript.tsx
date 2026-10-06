import { useEffect, useLayoutEffect, useRef, useState } from 'react'
import { api, type SessionItem } from './api'
import { Markdown } from './Markdown'
import { atTranscriptTail, mergeTranscript, payloadText, transcriptCursor } from './transcriptData'

const WINDOW = 80

export function SessionTranscript({ sessionId, tick }: { sessionId: string; tick: number }) {
  // A session switch must never display the previous session's items or pending responses.
  return <Timeline key={sessionId} sessionId={sessionId} tick={tick} />
}

function Timeline({ sessionId, tick }: { sessionId: string; tick: number }) {
  const [items, setItems] = useState<SessionItem[]>([])
  const [loaded, setLoaded] = useState(false)
  const [error, setError] = useState('')
  const [following, setFollowing] = useState(true)
  const [unseen, setUnseen] = useState(0)
  const scroll = useRef<HTMLDivElement>(null)
  const list = useRef<HTMLDivElement>(null)
  const follow = useRef(true)
  const snapshot = useRef<SessionItem[]>([])
  const prepend = useRef<{ height: number; top: number } | null>(null)
  const refresh = useRef<() => void>(() => {})

  useEffect(() => {
    let disposed = false
    let busy = false
    let queued = false
    const poll = async () => {
      if (busy) { queued = true; return }
      busy = true
      try {
        const after = transcriptCursor(snapshot.current)
        const incoming = await api.get<SessionItem[]>(`/sessions/${encodeURIComponent(sessionId)}/items?after=${after}`)
        if (disposed) return
        const merged = mergeTranscript(snapshot.current, incoming)
        const newCount = merged.filter(item => item.seq > after).length
        if (incoming.length) {
          snapshot.current = merged
          setItems(merged)
          if (!follow.current) setUnseen(count => count + newCount)
        }
        setLoaded(true)
        setError('')
      } catch (cause) {
        if (!disposed) setError(cause instanceof Error ? cause.message : String(cause))
      } finally {
        busy = false
        if (queued && !disposed) { queued = false; void poll() }
      }
    }
    refresh.current = () => { void poll() }
    void poll()
    // Journal ticks are hints, not the source of truth. Poll also repairs SSE disconnect gaps.
    const timer = window.setInterval(() => { void poll() }, 3000)
    const visible = () => { if (!document.hidden) void poll() }
    window.addEventListener('online', visible)
    document.addEventListener('visibilitychange', visible)
    return () => {
      disposed = true
      refresh.current = () => {}
      window.clearInterval(timer)
      window.removeEventListener('online', visible)
      document.removeEventListener('visibilitychange', visible)
    }
  }, [sessionId])

  useEffect(() => { refresh.current() }, [tick])

  // Freeze the history window's start when new rows arrive while reading older content.
  // It grows naturally with new items rather than removing rows above the scroll anchor.
  const [start, setStart] = useState(0)
  const initialized = useRef(false)
  useLayoutEffect(() => {
    if (!loaded || initialized.current) return
    initialized.current = true
    setStart(Math.max(0, items.length - WINDOW))
  }, [loaded, items])

  useLayoutEffect(() => {
    const node = scroll.current
    if (!node) return
    if (prepend.current) {
      node.scrollTop = prepend.current.top + node.scrollHeight - prepend.current.height
      prepend.current = null
    } else if (follow.current) node.scrollTop = node.scrollHeight
  }, [items, start, loaded])

  useEffect(() => {
    const observer = new ResizeObserver(() => {
      if (follow.current && scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight
    })
    if (list.current) observer.observe(list.current)
    return () => observer.disconnect()
  }, [])

  const latest = () => {
    follow.current = true
    setFollowing(true)
    setUnseen(0)
    if (scroll.current) scroll.current.scrollTop = scroll.current.scrollHeight
  }
  const older = () => {
    if (!scroll.current) return
    follow.current = false
    setFollowing(false)
    prepend.current = { top: scroll.current.scrollTop, height: scroll.current.scrollHeight }
    setStart(value => Math.max(0, value - WINDOW))
  }
  const results = new Map<string, SessionItem>()
  for (const item of items) if (item.kind === 'tool_result') results.set(item.payload?.call_id ?? '', item)

  return <section className="session-transcript" aria-label="Session conversation">
    <div className="transcript-toolbar">
      <span className="small muted">{items.length.toLocaleString()} events · {following ? 'Following latest' : 'Reading history'}</span>
      <button className="small" onClick={latest}>Jump to latest{unseen ? ` (${unseen} new)` : ''}</button>
    </div>
    {error && <div className="transcript-error small" role="status">Updates disconnected: {error}. History retained; retrying automatically. <button onClick={() => refresh.current()}>Retry now</button></div>}
    <div className="transcript-scroll" ref={scroll} onScroll={() => {
      const node = scroll.current
      if (!node) return
      const tail = atTranscriptTail(node.scrollTop, node.clientHeight, node.scrollHeight)
      follow.current = tail
      setFollowing(tail)
      if (tail) setUnseen(0)
    }}>
      <div ref={list} className="transcript-timeline">
        {start > 0 && <button className="transcript-older" onClick={older}>Load earlier events ({start.toLocaleString()} earlier)</button>}
        {!loaded && !error && <div className="empty">Loading conversation…</div>}
        {loaded && items.length === 0 && <div className="empty">No messages or tool activity yet.</div>}
        {items.slice(start).map(item => <TranscriptEvent key={item.id} item={item} results={results} />)}
      </div>
    </div>
  </section>
}

function Trace({ item }: { item: SessionItem }) {
  return <details className="transcript-trace"><summary>Full trace · #{item.seq} · turn {item.turn}</summary>
    <div className="small muted">{item.kind} · {item.id} · {new Date(item.created_at).toLocaleString()} · ~{item.token_estimate} tokens</div>
    <pre>{payloadText(item.payload)}</pre>
  </details>
}

function TranscriptEvent({ item, results }: { item: SessionItem; results: Map<string, SessionItem> }) {
  const p = item.payload ?? {}
  const speaker = item.kind === 'assistant' ? 'Ainu' : item.kind === 'user' ? 'Incoming message' : item.kind === 'tool_result' ? 'Tool result' : item.kind === 'summary' ? 'Context summary' : item.kind === 'notice' ? 'Notice' : item.kind
  const content = payloadText(p.content)
  const calls: { id: string; name: string; arguments: unknown }[] = Array.isArray(p.tool_calls) ? p.tool_calls : []
  return <article className={`transcript-event transcript-${item.kind} ${p.is_error ? 'has-error' : ''}`} data-seq={item.seq} data-event-id={item.id}>
    <div className="transcript-meta"><b>{speaker}</b><time dateTime={new Date(item.created_at).toISOString()}>{new Date(item.created_at).toLocaleTimeString()}</time></div>
    {item.kind === 'assistant' ? <>
      {content && <div className="transcript-speech"><Markdown text={content} /></div>}
      {p.reasoning && <details className="transcript-reasoning"><summary>Reasoning · {payloadText(p.reasoning).length.toLocaleString()} characters</summary><pre>{payloadText(p.reasoning)}</pre></details>}
      {calls.map((call, index) => {
        const result = results.get(call.id)
        const outcome = result ? result.payload?.is_error ? 'Error' : 'Returned' : 'Awaiting result'
        return <details key={`${call.id}-${index}`} className="transcript-tool-call"><summary><span className="mono">↳ {call.name}</span> <span className={result?.payload?.is_error ? 'error' : 'muted'}>{outcome}</span></summary>
          <div className="small muted">Call {call.id} · arguments</div><pre>{payloadText(call.arguments)}</pre>
          {result && <a href={`#event-${result.id}`}>Result at #{result.seq}</a>}
        </details>
      })}
      {!content && !p.reasoning && calls.length === 0 && <span className="muted small">No speech or tool calls in this event.</span>}
    </> : item.kind === 'tool_result' ? <div id={`event-${item.id}`}>
      <div className="transcript-result-heading"><span className="mono">{payloadText(p.tool_name) || 'Tool'}</span><b className={p.is_error ? 'error' : 'transcript-success'}>{p.is_error ? 'Error' : 'Returned'}</b></div>
      <div className="small pre transcript-preview">{payloadText(p.text).slice(0, 240)}{payloadText(p.text).length > 240 ? '…' : ''}</div>
      <details className="transcript-tool-output"><summary>Full result · {payloadText(p.text).length.toLocaleString()} characters</summary><pre>{payloadText(p.text)}</pre></details>
    </div> : item.kind === 'user' ? <div className="transcript-speech pre">{payloadText(p.text)}</div>
      : <details className="transcript-administrative"><summary>{item.kind === 'summary' ? `${payloadText(p.mode)} summary · covers #${p.covers_from_seq}–#${p.covers_through_seq}` : payloadText(p.text).slice(0, 160) || 'Event details'}</summary><pre>{payloadText(p.text ?? p)}</pre></details>}
    <Trace item={item} />
  </article>
}
