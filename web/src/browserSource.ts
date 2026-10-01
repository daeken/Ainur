import { useCallback, useEffect, useMemo, useRef, useState } from 'react'

/**
 * Browser frame source for the session browser view.
 *
 * Wire contract: knowledge doc `reference/browser-stream-contract` (r1, provisional, owner Tulkas,
 * revision kr_01a0f84d06e8713f9267c59c23517c39). Reconciled field-by-field 2026-10-01.
 *
 *   GET <base>/{id}/stream   SSE; events `session` | `frame` | `closed` | `error`
 *   GET <base>/{id}/frame    one-shot latest frame (same `frame` payload)
 *   GET <base>               index: JSON array of `session` payloads
 *
 * `base` defaults to the contract's same-origin route `/api/v1/browser/sessions` — there is NO fake
 * activity by default: if the route is absent the panel honestly reports the no-session / route
 * unavailable state. The local generator is an explicit, labelled opt-in for tests and demos only:
 * `?browserStream=mock` on the UI URL, or a build-time `VITE_BROWSER_STREAM=mock`. Any other value of
 * either switches to that URL (so the panel can be pointed at a contract fixture without a rebuild).
 * Resolution order: query parameter, then `VITE_BROWSER_STREAM`, then the default route.
 *
 * Contract points implemented here:
 *  - `frame.seq` is monotonic; a lower seq is ignored (replay-on-connect must not rewind the view).
 *  - `session`/`frame` are idempotent snapshots, not increments.
 *  - `closed` is terminal: show ended state, keep the last frame, stop reconnecting.
 *  - `error` carries `{message, recoverable}`; recoverable errors keep the last frame and the next
 *    `frame` clears the banner.
 *  - `disconnected`/`reconnecting` are client-derived from EventSource state, never server events.
 *  - HTTP 404 (unknown session) must show the no-session state and must NOT reconnect-loop.
 *  - The index is consulted first so an agent whose browser session lives on a non-primary Ainur
 *    session is still attributed correctly; while no session exists the index is polled.
 */

export type StreamStatus = 'no-session' | 'connecting' | 'streaming' | 'disconnected'

export interface BrowserFrame {
  seq: number
  /** `data:image/png;base64,....` — directly usable as an <img src>. */
  data_url: string
  width: number
  height: number
  url: string
  title: string
  captured_at: number
  /** Additive per contract §3.2 — content-addressed ref; ignored by the panel in v1. */
  artifact?: string
  id?: string
  agent_id?: string
}

export interface BrowserSessionInfo {
  id: string
  agent_id: string
  state: 'starting' | 'streaming' | 'idle' | 'ended'
  url: string
  title: string
  viewport: { width: number; height: number }
  started_at: number
}

interface Handlers {
  onSession: (s: BrowserSessionInfo) => void
  onFrame: (f: BrowserFrame) => void
  onStatus: (s: StreamStatus, detail?: string) => void
  onIgnoredFrame: () => void
  onError: (message: string | undefined, recoverable?: boolean) => void
  onEnded: (reason: string) => void
  /** Accounting only: record that a contract event was consumed. */
  onEvent: (label: string) => void
}

interface FrameSourceHandle {
  stop: () => void
}

const ENV_BASE: string | undefined = import.meta.env.VITE_BROWSER_STREAM
const POLL_MS = 5000
/** A `frame` clears a recoverable error banner (contract §3), but not before it has been readable:
 *  an error co-delivered with a frame in the same network read would otherwise flash for 0 ms. */
const MIN_NOTICE_MS = 1000

/** The contract's default, same-origin: the server route owned by Tulkas. */
const DEFAULT_BASE = '/api/v1/browser/sessions'

/** Query parameter overrides build-time env overrides the real default; `mock` = local generator. */
function runtimeBase(): string | undefined {
  let fromQuery: string | null = null
  try { fromQuery = new URLSearchParams(window.location.search).get('browserStream') } catch { fromQuery = null }
  const raw = (fromQuery ?? ENV_BASE ?? DEFAULT_BASE).trim()
  if (raw === '' || raw === 'mock') return undefined // explicit opt-in to the labelled mock source
  return raw.replace(/\/+$/, '')
}

const MOCK_WIDTH = 960
const MOCK_HEIGHT = 600
const MOCK_INTERVAL_MS = 250

/** Draws a plausible "page" so the view has something real to render. */
function paintMockFrame(canvas: HTMLCanvasElement, agentId: string, seq: number, elapsedMs: number) {
  const ctx = canvas.getContext('2d')
  if (!ctx) return
  const W = canvas.width
  const H = canvas.height
  const t = elapsedMs / 1000
  const url = `https://example.com/agent/${encodeURIComponent(agentId)}`

  ctx.fillStyle = '#ffffff'
  ctx.fillRect(0, 0, W, H)

  // Browser chrome.
  ctx.fillStyle = '#e9e7e2'
  ctx.fillRect(0, 0, W, 46)
  const dots = ['#f06a5a', '#f3bd4e', '#57c15a']
  dots.forEach((c, i) => {
    ctx.fillStyle = c
    ctx.beginPath()
    ctx.arc(22 + i * 20, 23, 6, 0, Math.PI * 2)
    ctx.fill()
  })
  ctx.fillStyle = '#ffffff'
  ctx.fillRect(96, 11, W - 120, 24)
  ctx.strokeStyle = '#c9c6bf'
  ctx.strokeRect(96, 11, W - 120, 24)
  ctx.fillStyle = '#33312c'
  ctx.font = '13px ui-monospace, Menlo, monospace'
  ctx.fillText(url, 106, 28)

  // Page content.
  ctx.fillStyle = '#141311'
  ctx.font = 'bold 28px -apple-system, Helvetica, sans-serif'
  ctx.fillText('Mock page', 48, 118)
  ctx.font = '15px -apple-system, Helvetica, sans-serif'
  ctx.fillStyle = '#5c5951'
  ctx.fillText(`agent ${agentId} · watching frames from the mock source`, 48, 146)

  ctx.fillStyle = '#f2f0ec'
  ctx.fillRect(48, 176, W - 96, 150)
  ctx.fillStyle = '#8b877e'
  ctx.font = '14px ui-monospace, Menlo, monospace'
  for (let i = 0; i < 5; i++) {
    const w = 240 + ((seq * 37 + i * 91) % 380)
    ctx.fillRect(72, 202 + i * 26, w, 8)
  }

  // "Click" ripple that moves around the page.
  const cx = 140 + ((t * 160) % (W - 280))
  const cy = 200 + 120 * Math.sin(t * 1.3)
  const pulse = (t * 2) % 1
  ctx.strokeStyle = `rgba(58,91,217,${1 - pulse})`
  ctx.lineWidth = 3
  ctx.beginPath()
  ctx.arc(cx, cy, 6 + pulse * 22, 0, Math.PI * 2)
  ctx.stroke()
  ctx.fillStyle = '#141311'
  ctx.beginPath()
  ctx.moveTo(cx, cy)
  ctx.lineTo(cx, cy + 16)
  ctx.lineTo(cx + 5, cy + 12)
  ctx.lineTo(cx + 10, cy + 18)
  ctx.lineTo(cx + 13, cy + 15)
  ctx.lineTo(cx + 8, cy + 9)
  ctx.lineTo(cx + 14, cy + 8)
  ctx.closePath()
  ctx.fill()

  // Frame counter strip.
  ctx.fillStyle = 'rgba(20,19,17,0.78)'
  ctx.fillRect(0, H - 34, W, 34)
  ctx.fillStyle = '#f5f4f1'
  ctx.font = '13px ui-monospace, Menlo, monospace'
  ctx.fillText(`MOCK FRAME #${seq}  ·  ${(elapsedMs / 1000).toFixed(1)}s  ·  ${MOCK_WIDTH}x${MOCK_HEIGHT}`, 12, H - 13)
}

function createMockSource(agentId: string, h: Handlers): FrameSourceHandle {
  const canvas = document.createElement('canvas')
  canvas.width = MOCK_WIDTH
  canvas.height = MOCK_HEIGHT
  const startedAt = Date.now()
  const session: BrowserSessionInfo = {
    id: `mock-browser-${agentId}`,
    agent_id: agentId,
    state: 'starting',
    url: `https://example.com/agent/${encodeURIComponent(agentId)}`,
    title: `Mock page for ${agentId}`,
    viewport: { width: MOCK_WIDTH, height: MOCK_HEIGHT },
    started_at: startedAt,
  }
  let seq = 0
  let stopped = false
  let timer: number | undefined

  h.onStatus('connecting', 'opening mock frame source')
  const connectTimer = window.setTimeout(() => {
    if (stopped) return
    session.state = 'streaming'
    h.onSession({ ...session })
    h.onEvent('session')
    h.onStatus('streaming')
    const tick = () => {
      if (stopped) return
      seq += 1
      const elapsed = Date.now() - startedAt
      paintMockFrame(canvas, agentId, seq, elapsed)
      h.onEvent('frame')
      h.onFrame({
        seq,
        data_url: canvas.toDataURL('image/jpeg', 0.72),
        width: MOCK_WIDTH,
        height: MOCK_HEIGHT,
        url: session.url,
        title: session.title,
        captured_at: Date.now(),
        id: session.id,
        agent_id: agentId,
      })
      timer = window.setTimeout(tick, MOCK_INTERVAL_MS)
    }
    tick()
  }, 350)

  return {
    stop() {
      stopped = true
      window.clearTimeout(connectTimer)
      if (timer !== undefined) window.clearTimeout(timer)
    },
  }
}

function parseData<T>(e: Event): T | undefined {
  const data = (e as MessageEvent).data
  if (typeof data !== 'string') return undefined
  try { return JSON.parse(data) as T } catch { return undefined }
}

type Target = { kind: 'stream'; id: string } | { kind: 'poll'; detail: string } | { kind: 'stop'; detail: string }

type Resolution = { target: Target; /** true when the index route answered 404/405 (not deployed). */ indexMissing: boolean }

/**
 * Resolve which browser session to watch. The contract's index is authoritative: if it answers and
 * the agent has no live session we show no-session (and poll). If the index route is missing or
 * unreachable we fall back to the Ainur session id the panel already knows, and let a 404 there
 * produce the no-session state per contract §2. `indexMissing` lets the panel say plainly that the
 * route is not deployed here, instead of implying the agent simply has no browser open.
 */
async function resolveTarget(base: string, agentId: string, hintSessionId?: string): Promise<Resolution> {
  const fallback = (indexMissing: boolean): Resolution => ({
    target: hintSessionId
      ? { kind: 'stream', id: hintSessionId }
      : { kind: 'stop', detail: `browser stream route ${base} is unavailable on this server` },
    indexMissing,
  })
  try {
    const res = await fetch(base, { headers: { accept: 'application/json' } })
    if (!res.ok) {
      // 404/405: the route is not deployed. Other statuses: present but broken.
      return fallback(res.status === 404 || res.status === 405)
    }
    // A deployed server with no such route answers the SPA fallback (`MapFallbackToFile`): 200 plus
    // index.html. That is not the contract's JSON array, and is the route-missing signature here.
    let list: unknown
    try { list = await res.json() } catch { return fallback(true) }
    if (!Array.isArray(list)) return fallback(true)
    const mine = (list as BrowserSessionInfo[]).filter((s) => s && s.agent_id === agentId && typeof s.id === 'string')
    const pick = mine.find((s) => s.id === hintSessionId) ?? mine[0]
    if (!pick) return { target: { kind: 'poll', detail: 'no live browser session for this agent' }, indexMissing: false }
    return { target: { kind: 'stream', id: pick.id }, indexMissing: false }
  } catch {
    return fallback(false)
  }
}

function createSseSource(base: string, agentId: string, hintSessionId: string | undefined, h: Handlers): FrameSourceHandle {
  let stopped = false
  let pollTimer: number | undefined
  let es: EventSource | undefined
  let lastSeq = -1
  let lastStartedAt: number | undefined
  let indexMissing = false

  const stop = () => {
    stopped = true
    window.clearTimeout(pollTimer)
    es?.close()
    es = undefined
  }

  const attach = (id: string) => {
    if (stopped) return
    const url = `${base}/${encodeURIComponent(id)}/stream`
    const src = new EventSource(url)
    es = src

    src.addEventListener('session', (e) => {
      const s = parseData<BrowserSessionInfo>(e)
      if (!s) return
      h.onEvent('session')
      // A new browser incarnation can reuse the Ainur session id. Its seq restarts at 1, while a
      // transport reconnect to the same incarnation replays the latest seq and must be deduped.
      if (lastStartedAt !== undefined && lastStartedAt !== s.started_at) {
        lastSeq = -1
        h.onEvent('session:new-incarnation')
      }
      lastStartedAt = s.started_at
      h.onSession(s)
      if (s.state === 'ended') h.onStatus('no-session', 'browser session ended')
      else h.onStatus('streaming')
    })

    src.addEventListener('frame', (e) => {
      const f = parseData<BrowserFrame>(e)
      if (!f) return
      if (typeof f.seq === 'number' && f.seq <= lastSeq) { h.onEvent('frame:stale'); h.onIgnoredFrame(); return }
      lastSeq = f.seq
      h.onEvent('frame')
      h.onFrame(f) // the hook clears any error banner, but only after a readable dwell
    })

    src.addEventListener('closed', (e) => {
      const c = parseData<{ reason?: string }>(e)
      h.onEvent('closed')
      h.onEnded(c?.reason ?? 'closed')
      h.onStatus('no-session', c?.reason ? `session ended: ${c.reason}` : 'session ended')
      // `closed` terminates THIS stream, not the agent's future browser sessions. Poll the index
      // at the same modest cadence as an empty index so a later acquisition becomes visible.
      src.close()
      es = undefined
      pollTimer = window.setTimeout(() => { void resolve() }, POLL_MS)
    })

    src.addEventListener('error', (e) => {
      const ev = e as MessageEvent
      // A server-sent `error` event is a MessageEvent with `data`; the browser's own transport error
      // event for EventSource is a plain Event with no `data`. Same event name, so discriminate.
      if (typeof ev.data === 'string' && ev.data.length > 0) {
        const p = parseData<{ message?: string; recoverable?: boolean }>(e)
        h.onEvent('error')
        h.onError(p?.message ?? 'browser stream error', p?.recoverable)
        return
      }
      h.onEvent('transport-error')
      if (src.readyState === EventSource.CLOSED) {
        const detail = indexMissing
          ? `the browser stream route ${base} is not deployed on this server yet`
          : `${url} could not be opened (fatal: unknown session, or the stream route is unavailable)`
        h.onStatus('no-session', detail)
        src.close()
        es = undefined
        // On a real route, the browser may have ended during a transport outage. Refresh the
        // authoritative index; don't reconnect-loop against an unknown session id (404).
        if (!indexMissing) pollTimer = window.setTimeout(() => { void resolve() }, POLL_MS)
        else stop()
        return
      }
      h.onStatus('disconnected', 'frame source dropped; the browser is retrying')
    })
  }

  const resolve = async (initial = false) => {
    if (stopped) return
    // A background index poll must not flash 'connecting' over the ended/no-session overlay.
    if (initial) h.onStatus('connecting', 'resolving browser session')
    const { target, indexMissing: missing } = await resolveTarget(base, agentId, hintSessionId)
    indexMissing = missing
    if (stopped) return
    if (target.kind === 'stop') { h.onStatus('no-session', target.detail); return }
    if (target.kind === 'poll') {
      h.onStatus('no-session', target.detail)
      pollTimer = window.setTimeout(() => { void resolve() }, POLL_MS)
      return
    }
    attach(target.id)
  }

  void resolve(true)
  return { stop }
}

export interface BrowserStreamState {
  status: StreamStatus
  session: BrowserSessionInfo | null
  frame: BrowserFrame | null
  frameCount: number
  /** Frames dropped because their `seq` was not newer (contract §3.2). */
  ignoredFrames: number
  fps: number
  attempt: number
  /** Detail for the current status (why no session, why disconnected). */
  detail?: string
  /** Last `error` event message; cleared by the next frame. */
  notice?: string
  /** Set when the server sent `closed`. */
  endedReason?: string
  /** Count of contract events this view consumed, e.g. `{ session: 1, frame: 14, error: 1 }`. */
  eventsSeen: Record<string, number>
  /** Human label for where frames come from ('mock' or the stream base). */
  source: string
  mock: boolean
  simulateDrop: () => void
  reconnectNow: () => void
  endSession: () => void
  startSession: () => void
}

export function useBrowserStream(agentId: string | undefined, hintSessionId?: string): BrowserStreamState {
  const [status, setStatus] = useState<StreamStatus>('no-session')
  const [session, setSession] = useState<BrowserSessionInfo | null>(null)
  const [frame, setFrame] = useState<BrowserFrame | null>(null)
  const [frameCount, setFrameCount] = useState(0)
  const [ignoredFrames, setIgnoredFrames] = useState(0)
  const [fps, setFps] = useState(0)
  const [attempt, setAttempt] = useState(1)
  const [detail, setDetail] = useState<string | undefined>()
  const [notice, setNotice] = useState<string | undefined>()
  const [endedReason, setEndedReason] = useState<string | undefined>()
  const [eventsSeen, setEventsSeen] = useState<Record<string, number>>({})
  const [ended, setEnded] = useState(false)
  const [nonce, setNonce] = useState(0)
  const handleRef = useRef<FrameSourceHandle | undefined>(undefined)
  const timestampsRef = useRef<number[]>([])
  const noticeAtRef = useRef(0)

  const base = useMemo(() => runtimeBase(), [])
  const mock = !base

  useEffect(() => {
    if (!agentId || ended) {
      setStatus('no-session')
      setSession(null)
      setFrame(null)
      setFrameCount(0)
      setIgnoredFrames(0)
      setFps(0)
      setDetail(ended ? 'session ended' : undefined)
      return
    }
    setStatus('connecting')
    setDetail(undefined)
    setNotice(undefined)
    setEndedReason(undefined)
    setSession(null)
    setFrame(null)
    setFrameCount(0)
    setIgnoredFrames(0)
    setFps(0)
    setEventsSeen({})
    timestampsRef.current = []

    const handlers: Handlers = {
      onSession: (s) => { setSession(s); setEndedReason(undefined); setDetail(s.state === 'idle' ? 'browser is idle' : undefined) },
      onFrame: (f) => {
        setFrame(f)
        // The contract sends session metadata on connect, then per-frame URL/title as the page moves.
        // Refresh the visible location from each frame rather than leaving the connect-time about:blank.
        setSession((prev) => prev ? { ...prev, url: f.url, title: f.title } : prev)
        setFrameCount((n) => n + 1)
        const now = Date.now()
        if (noticeAtRef.current && now - noticeAtRef.current >= MIN_NOTICE_MS) {
          noticeAtRef.current = 0
          setNotice(undefined)
        }
        const stamps = timestampsRef.current
        stamps.push(now)
        while (stamps.length > 0 && now - stamps[0] > 2000) stamps.shift()
        setFps(stamps.length > 1 ? Math.round((stamps.length * 1000) / (now - stamps[0])) : 0)
      },
      onStatus: (s, d) => {
        setStatus(s)
        if (d !== undefined) setDetail(d)
        if (s === 'streaming') setEndedReason(undefined)
        if (s === 'disconnected') setAttempt((a) => a + 1)
      },
      onIgnoredFrame: () => setIgnoredFrames((n) => n + 1),
      onEvent: (label) => setEventsSeen((prev) => ({ ...prev, [label]: (prev[label] ?? 0) + 1 })),
      onError: (message, recoverable) => {
        if (message === undefined) { noticeAtRef.current = 0; setNotice(undefined); return }
        noticeAtRef.current = Date.now()
        setNotice(recoverable === false ? `${message} (fatal)` : message)
      },
      onEnded: setEndedReason,
    }
    const handle = base ? createSseSource(base, agentId, hintSessionId, handlers) : createMockSource(agentId, handlers)
    handleRef.current = handle
    return () => { handle.stop(); handleRef.current = undefined }
  }, [agentId, hintSessionId, ended, nonce, base])

  const reconnectNow = useCallback(() => {
    handleRef.current?.stop()
    handleRef.current = undefined
    setStatus('connecting')
    setAttempt((a) => a + 1)
    setNonce((n) => n + 1)
  }, [])

  const simulateDrop = useCallback(() => {
    handleRef.current?.stop()
    handleRef.current = undefined
    setStatus('disconnected')
    setDetail('frame source dropped (simulated)')
    setAttempt((a) => a + 1)
    window.setTimeout(() => setNonce((n) => n + 1), 1500)
  }, [])

  const endSession = useCallback(() => {
    handleRef.current?.stop()
    handleRef.current = undefined
    setEnded(true)
  }, [])

  const startSession = useCallback(() => {
    setEnded(false)
    setAttempt(1)
    setNonce((n) => n + 1)
  }, [])

  return {
    status, session, frame, frameCount, ignoredFrames, fps, attempt, detail, notice, endedReason, eventsSeen,
    source: base ?? 'mock', mock,
    simulateDrop, reconnectNow, endSession, startSession,
  }
}
