import { useBrowserStream, type StreamStatus } from './browserSource'

const STATUS_LABEL: Record<StreamStatus, string> = {
  'no-session': 'no browser session',
  connecting: 'connecting…',
  streaming: 'streaming',
  disconnected: 'disconnected',
}

function StatusPill({ status, attempt }: { status: StreamStatus; attempt: number }) {
  const label = status === 'disconnected' ? `disconnected — reconnecting (attempt ${attempt})` : STATUS_LABEL[status]
  return <span className={`state-pill browser-status ${status}`}>{label}</span>
}

/**
 * Live embedded view of an agent's browser session.
 *
 * Backed by `useBrowserStream` (web/src/browserSource.ts): the local mock generator by default, or
 * Tulkas's SSE stream (`reference/browser-stream-contract`) when `?browserStream=<url>` or
 * `VITE_BROWSER_STREAM` is set.
 */
export function SessionBrowser({ agentId, agentName, sessionId }: { agentId: string; agentName: string; sessionId?: string }) {
  const b = useBrowserStream(agentId, sessionId)
  const ended = b.status === 'no-session' && b.endedReason !== undefined
  return (
    <div
      className="browser-panel"
      data-status={b.status}
      data-frames={b.frameCount}
      data-ignored={b.ignoredFrames}
      data-mock={String(b.mock)}
      data-source={b.source}
      data-ended={b.endedReason ?? ''}
      data-notice={b.notice ?? ''}
      data-events={Object.entries(b.eventsSeen).map(([k, v]) => k + ':' + v).join(' ')}
    >
      <div className="browser-toolbar">
        <StatusPill status={b.status} attempt={b.attempt} />
        <span className="badge browser-source" title={b.mock ? 'Frames come from the local mock generator (web/src/browserSource.ts).' : `Frames come from the live browser-session stream: ${b.source}`}>
          {b.mock ? 'MOCK SOURCE' : 'LIVE SOURCE'}
        </span>
        {!b.mock && <span className="small muted mono browser-source-url" title={b.source}>{b.source}</span>}
        <span className="grow" />
        <span className="small muted mono browser-metrics">
          {b.frameCount} frames
          {b.status === 'streaming' && b.fps > 0 ? ` · ~${b.fps} fps` : ''}
          {b.ignoredFrames > 0 ? ` · ${b.ignoredFrames} stale ignored` : ''}
          {b.session ? ` · ${b.session.viewport.width}x${b.session.viewport.height}` : ''}
        </span>
      </div>

      {b.session && (
        <div className="small muted browser-location mono" title={b.session.title}>
          {b.session.url} · session {b.session.id} · agent {b.session.agent_id}
        </div>
      )}

      {b.notice && (
        <div className="browser-notice small" data-notice-text={b.notice}>
          stream error: {b.notice}
        </div>
      )}

      <div className="browser-viewport">
        {b.frame ? (
          <img src={b.frame.data_url} alt={`Browser frame ${b.frame.seq} for ${agentName}`} />
        ) : (
          <div className="browser-placeholder">
            {b.status === 'no-session' ? (ended ? 'Browser session ended' : 'No browser session') : 'Waiting for the first frame…'}
          </div>
        )}

        {b.status === 'no-session' && (
          <div className="browser-overlay">
            <div className="browser-overlay-card">
              <b>{ended ? 'Browser session ended' : 'No browser session'}</b>
              <div className="small muted browser-overlay-detail">
                {b.endedReason
                  ? `The stream reported the session closed (${b.endedReason}).`
                  : b.detail ?? `${agentName} does not have an active browser session. One appears here live when the agent starts driving a browser.`}
              </div>
            </div>
          </div>
        )}
        {b.status === 'connecting' && (
          <div className="browser-overlay soft">
            <div className="browser-overlay-card">
              <b>Connecting…</b>
              <div className="small muted browser-overlay-detail">{b.detail ?? 'Opening the frame stream.'}{b.mock ? ' (mock source)' : ''}</div>
            </div>
          </div>
        )}
        {b.status === 'disconnected' && (
          <div className="browser-overlay">
            <div className="browser-overlay-card">
              <b>Stream disconnected</b>
              <div className="small muted browser-overlay-detail">{b.detail ?? 'The frame stream stopped.'} Showing the last frame received.</div>
              <button className="small" onClick={b.reconnectNow}>Reconnect now</button>
            </div>
          </div>
        )}
      </div>

      <div className="browser-footer small muted">
        {b.frame ? <>last frame #{b.frame.seq} · captured {new Date(b.frame.captured_at).toLocaleTimeString()}</> : 'no frames received yet'}
        {b.session ? ` · session ${b.session.state}` : ''}
        <span className="mono browser-event-counts" title="Contract events consumed by this view">
          {Object.entries(b.eventsSeen).map(([k, v]) => `${k}:${v}`).join(' ')}
        </span>
      </div>

      {b.mock && (
        <div className="browser-dev-controls small">
          <span className="muted">mock controls:</span>
          <button className="small" onClick={b.simulateDrop} disabled={b.status === 'no-session'}>Simulate drop</button>
          <button className="small" onClick={b.reconnectNow}>Reconnect</button>
          {b.status === 'no-session'
            ? <button className="small" onClick={b.startSession}>Start session</button>
            : <button className="small" onClick={b.endSession}>End session</button>}
        </div>
      )}
    </div>
  )
}
