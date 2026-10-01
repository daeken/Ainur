import { useCallback, useEffect, useRef, useState } from 'react'

/**
 * Browser frame source for the session browser view.
 *
 * CONTRACT OWNERSHIP: Tulkas owns the wire contract for the browser-session stream. As of
 * 2026-10-01 no browser/frame-stream document exists in the knowledge store (checked with
 * knowledge_search and ask_knowledge), so this module serves frames from a local MOCK
 * generator and the real SSE transport is implemented against the *expected* shape below,
 * behind `VITE_BROWSER_STREAM`. When Tulkas publishes the contract, only this file needs to
 * change; SessionBrowser consumes the hook and does not care which source is behind it.
 *
 * Expected SSE frame shape (ASSUMED — must be reconciled with Tulkas's contract):
 *   GET <VITE_BROWSER_STREAM>/<sessionId>/stream        (text/event-stream)
 *   event: session  data: { id, agent_id, state, url, title, viewport:{width,height}, started_at }
 *   event: frame    data: { seq, data_url, width, height, url, title, captured_at }
 *   event: closed   data: { reason? }
 */

export type StreamStatus = 'no-session' | 'connecting' | 'streaming' | 'disconnected' | 'closed'

export interface BrowserFrame {
  seq: number
  /** `data:image/...;base64,....` — directly usable as an <img src>. */
  data_url: string
  width: number
  height: number
  url: string
  title: string
  captured_at: number
}

export interface BrowserSessionInfo {
  id: string
  agent_id: string
  state: 'starting' | 'streaming' | 'closed'
  url: string
  title: string
  viewport: { width: number; height: number }
  started_at: number
}

interface Handlers {
  onSession: (s: BrowserSessionInfo) => void
  onFrame: (f: BrowserFrame) => void
  onStatus: (s: StreamStatus, detail?: string) => void
}

interface FrameSourceHandle {
  stop: () => void
}

const STREAM_BASE: string | undefined = import.meta.env.VITE_BROWSER_STREAM
export const IS_MOCK_SOURCE = !STREAM_BASE

const MOCK_WIDTH = 960
const MOCK_HEIGHT = 600
const MOCK_INTERVAL_MS = 250

/** Draws a plausible "page" so the view has something real to render while the contract lands. */
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
    h.onStatus('streaming')
    const tick = () => {
      if (stopped) return
      seq += 1
      const elapsed = Date.now() - startedAt
      paintMockFrame(canvas, agentId, seq, elapsed)
      h.onFrame({
        seq,
        data_url: canvas.toDataURL('image/jpeg', 0.72),
        width: MOCK_WIDTH,
        height: MOCK_HEIGHT,
        url: session.url,
        title: session.title,
        captured_at: Date.now(),
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

function createSseSource(base: string, sessionId: string | undefined, h: Handlers): FrameSourceHandle {
  if (!sessionId) {
    h.onStatus('no-session')
    return { stop() {} }
  }
  const es = new EventSource(`${base}/${encodeURIComponent(sessionId)}/stream`)
  es.addEventListener('session', (e) => {
    h.onSession(JSON.parse((e as MessageEvent).data) as BrowserSessionInfo)
    h.onStatus('streaming')
  })
  es.addEventListener('frame', (e) => h.onFrame(JSON.parse((e as MessageEvent).data) as BrowserFrame))
  es.addEventListener('closed', (e) => {
    let reason: string | undefined
    try { reason = (JSON.parse((e as MessageEvent).data) as { reason?: string }).reason } catch { reason = undefined }
    h.onStatus('closed', reason)
    es.close()
  })
  es.onerror = () => h.onStatus('disconnected', 'event source error (browser will retry)')
  return { stop: () => es.close() }
}

export interface BrowserStreamState {
  status: StreamStatus
  session: BrowserSessionInfo | null
  frame: BrowserFrame | null
  frameCount: number
  fps: number
  attempt: number
  error?: string
  mock: boolean
  /** Mock-only dev controls so the disconnected/no-session states are demonstrable. */
  simulateDrop: () => void
  reconnectNow: () => void
  endSession: () => void
  startSession: () => void
}

export function useBrowserStream(agentId: string | undefined, sessionId?: string): BrowserStreamState {
  const [status, setStatus] = useState<StreamStatus>('no-session')
  const [session, setSession] = useState<BrowserSessionInfo | null>(null)
  const [frame, setFrame] = useState<BrowserFrame | null>(null)
  const [frameCount, setFrameCount] = useState(0)
  const [fps, setFps] = useState(0)
  const [attempt, setAttempt] = useState(1)
  const [error, setError] = useState<string | undefined>()
  const [ended, setEnded] = useState(false)
  const [nonce, setNonce] = useState(0)
  const handleRef = useRef<FrameSourceHandle | undefined>(undefined)
  const timestampsRef = useRef<number[]>([])

  useEffect(() => {
    if (!agentId || ended) {
      setStatus('no-session')
      setSession(null)
      setFrame(null)
      setFrameCount(0)
      setFps(0)
      return
    }
    setStatus('connecting')
    setError(undefined)
    timestampsRef.current = []
    const handlers: Handlers = {
      onSession: setSession,
      onStatus: (s, detail) => { setStatus(s); if (s === 'disconnected') setError(detail ?? 'stream disconnected') },
      onFrame: (f) => {
        setFrame(f)
        setFrameCount((n) => n + 1)
        const now = Date.now()
        const stamps = timestampsRef.current
        stamps.push(now)
        while (stamps.length > 0 && now - stamps[0] > 2000) stamps.shift()
        setFps(stamps.length > 1 ? Math.round((stamps.length * 1000) / (now - stamps[0])) : 0)
      },
    }
    const handle = STREAM_BASE ? createSseSource(STREAM_BASE, sessionId, handlers) : createMockSource(agentId, handlers)
    handleRef.current = handle
    return () => { handle.stop(); handleRef.current = undefined }
  }, [agentId, sessionId, ended, nonce])

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
    setError('frame source dropped (simulated)')
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

  return { status, session, frame, frameCount, fps, attempt, error, mock: IS_MOCK_SOURCE, simulateDrop, reconnectNow, endSession, startSession }
}
