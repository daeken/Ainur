import { useBrowserStream, type StreamStatus } from './browserSource'

const STATUS_LABEL: Record<StreamStatus, string> = {
  'no-session': 'no browser session',
  connecting: 'connecting…',
  streaming: 'streaming',
  disconnected: 'disconnected',
  closed: 'session closed',
}

function StatusPill({ status, attempt }: { status: StreamStatus; attempt: number }) {
  const label = status === 'disconnected' ? `disconnected — reconnecting (attempt ${attempt})` : STATUS_LABEL[status]
  return <span className={`state-pill browser-status ${status}`}>{label}</span>
}

/**
 * Live embedded view of an agent's browser session.
 *
 * Backed by `useBrowserStream` (web/src/browserSource.ts): a local mock generator today,
 * Tulkas's real stream once `VITE_BROWSER_STREAM` points at his endpoint.
 */
export function SessionBrowser({ agentId, agentName, sessionId }: { agentId: string; agentName: string; sessionId?: string }) {
  const b = useBrowserStream(agentId, sessionId)
  return (
    <div className="browser-panel">
      <div className="browser-toolbar">
        <StatusPill status={b.status} attempt={b.attempt} />
        <span className="badge browser-source" title={b.mock ? 'Frames come from the local mock generator (web/src/browserSource.ts); Tulkas has not published the stream contract yet.' : 'Frames come from the live browser-session stream endpoint.'}>
          {b.mock ? 'MOCK SOURCE' : 'LIVE SOURCE'}
        </span>
        <span className="grow" />
        <span className="small muted mono">
          {b.frameCount} frames{b.status === 'streaming' && b.fps > 0 ? ` · ~${b.fps} fps` : ''}
          {b.session ? ` · ${b.session.viewport.width}x${b.session.viewport.height}` : ''}
        </span>
      </div>

      {b.session && (
        <div className="small muted browser-location mono" title={b.session.title}>
          {b.session.url} · session {b.session.id}
        </div>
      )}

      <div className="browser-viewport">
        {b.frame ? (
          <img src={b.frame.data_url} alt={`Browser frame ${b.frame.seq} for ${agentName}`} />
        ) : (
          <div className="browser-placeholder">
            {b.status === 'no-session' ? 'No browser session' : 'Waiting for the first frame…'}
          </div>
        )}

        {b.status === 'no-session' && (
          <div className="browser-overlay">
            <div className="browser-overlay-card">
              <b>No browser session</b>
              <div className="small muted">
                {agentName} does not have an active browser session. Ones appear here live when the agent starts driving a browser.
              </div>
            </div>
          </div>
        )}
        {b.status === 'connecting' && (
          <div className="browser-overlay soft">
            <div className="browser-overlay-card">
              <b>Connecting…</b>
              <div className="small muted">Opening the frame stream{b.mock ? ' (mock source)' : ''}.</div>
            </div>
          </div>
        )}
        {b.status === 'disconnected' && (
          <div className="browser-overlay">
            <div className="browser-overlay-card">
              <b>Stream disconnected</b>
              <div className="small muted">{b.error ?? 'The frame stream stopped.'} Showing the last frame received.</div>
              <button className="small" onClick={b.reconnectNow}>Reconnect now</button>
            </div>
          </div>
        )}
      </div>

      <div className="browser-footer small muted">
        {b.frame ? <>last frame #{b.frame.seq} · captured {new Date(b.frame.captured_at).toLocaleTimeString()}</> : 'no frames received yet'}
        {b.session && b.session.state !== 'streaming' ? ` · session ${b.session.state}` : ''}
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
