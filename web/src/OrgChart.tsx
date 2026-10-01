import { dollars, type Agent } from './api'

export function OrgChart({ agents, deltas, selected, onSelect }: { agents: Agent[]; deltas: Record<string, string>; selected?: string; onSelect: (id: string) => void }) {
  const live = agents.filter((a) => a.state !== 'retired' && a.state !== 'terminated')
  const former = agents.filter((a) => a.state === 'retired' || a.state === 'terminated')
  const children = (id?: string) => live.filter((a) => (a.manager_id ?? undefined) === id)
  const roots = live.filter((a) => !a.manager_id || !live.some((m) => m.id === a.manager_id))

  const node = (a: Agent) => {
    const kids = children(a.id)
    const stream = a.primary_session_id ? deltas[a.primary_session_id] : undefined
    return (
      <li key={a.id}>
        <button className={`agent-card ${a.role} ${selected === a.id ? 'selected' : ''} state-${a.state}`} onClick={() => onSelect(a.id)}>
          <div className="agent-name">{a.name} <span className="badge">{a.role === 'manager' ? 'Vala · manager' : 'Maia · specialist'}</span>{a.lifetime === 'ephemeral' && <span className="badge">ephemeral</span>}</div>
          <div className="small muted">{a.title}</div>
          <div className="small"><span className={`dot ${a.status && a.status !== 'idle' ? 'busy' : ''}`} />{a.state}{a.status && a.status !== 'idle' ? ` · ${a.status}` : ''}</div>
          <div className="small muted"><span className="mono">{a.model_id}</span>{a.reasoning_effort && <> <span className="badge effort-badge" title="Reasoning effort">{a.reasoning_effort}</span></>}</div>
          <div className="small">direct {dollars(a.direct_nanos)}{a.delegated_nanos > 0 ? ` · delegated ${dollars(a.delegated_nanos)}` : ''}</div>
          {a.pause && <div className="small pause-note">⏸ paused by {a.pause.requester} ({a.pause.state}) on {a.pause.scope}: {a.pause.reason}; until {a.pause.release_condition}</div>}
          {(a.consultations ?? []).map((c) => <div key={c.id} className="small consult-note">↳ consultation fork from #{c.checkpoint}: {c.question?.slice(0, 140)}</div>)}
          {stream && <div className="stream small">{stream.slice(-160)}</div>}
        </button>
        {kids.length > 0 && <ul>{kids.map(node)}</ul>}
      </li>
    )
  }

  return (
    <div className="org">
      <ul className="tree">{roots.map(node)}</ul>
      {former.length > 0 && (
        <div className="former">
          <div className="sidebar-section">Former agents</div>
          {former.map((a) => <button key={a.id} className="link" onClick={() => onSelect(a.id)}>{a.name} ({a.state}, {dollars(a.direct_nanos)})</button>)}
        </div>
      )}
    </div>
  )
}
