import { useEffect, useState } from 'react'
import { api, ago, dollars, type Agent, type Objective, type Project } from './api'

interface Work { id: string; objective_id?: string; owner_id: string; manager_id?: string; state: string; next_action: string; reason: string; resume_condition: string }
interface Data { objectives: Objective[]; dependencies: { objective_id: string; depends_on_id: string }[]; spend?: Record<string, { direct_nanos: number; total_nanos: number }> }

export function Objectives({ project, agents, tick, onSelectAgent }: { project: Project; agents: Agent[]; tick: number; onSelectAgent: (id: string) => void }) {
  const [data, setData] = useState<Data>({ objectives: [], dependencies: [] })
  const [work, setWork] = useState<Work[]>([])
  const [workError, setWorkError] = useState(false)
  useEffect(() => {
    let current = true
    api.get<Work[]>(`/projects/${project.id}/work`).then((rows) => { if (current) { setWork(rows); setWorkError(false) } }).catch(() => { if (current) setWorkError(true) })
    return () => { current = false }
  }, [project.id, tick])
  const [selected, setSelected] = useState<string>()
  useEffect(() => { api.get<Data>(`/projects/${project.id}/objectives`).then(setData).catch(console.error) }, [project.id, tick])
  const byParent = (id?: string) => data.objectives.filter((o) => (o.parent_id ?? undefined) === id)
  const name = (id?: string) => agents.find((a) => a.id === id)?.name ?? 'unowned'
  const sel = data.objectives.find((o) => o.id === selected)

  const node = (o: Objective) => {
    const kids = byParent(o.id)
    const deps = data.dependencies.filter((d) => d.objective_id === o.id)
    return (
      <li key={o.id}>
        <div className={`objective state-${o.state} ${selected === o.id ? 'selected' : ''}`} onClick={() => setSelected(o.id)}>
          <span className={`state-pill ${o.state}`}>{o.state}</span>
          <span className="objective-title">{o.title}</span>
          {!o.required && <span className="badge">optional</span>}
          <button className="link small" onClick={(e) => { e.stopPropagation(); if (o.owner_id) onSelectAgent(o.owner_id) }}>{name(o.owner_id)}</button>
          {deps.length > 0 && <span className="small muted">depends on {deps.length}</span>}
          {(data.spend?.[o.id]?.total_nanos ?? 0) > 0 && <span className="small muted">{dollars(data.spend![o.id].total_nanos)}</span>}
        </div>
        {kids.length > 0 && <ul>{kids.map(node)}</ul>}
      </li>
    )
  }

  let evidence: { at?: string; by?: string; evidence: string }[] = []
  try { evidence = sel ? JSON.parse(sel.evidence) : [] } catch { /* malformed */ }
  return (
    <div className={`split ${sel ? "has-detail" : ""}`}>
      <ul className="objective-tree">{byParent(undefined).map(node)}</ul>
      {sel && (
        <div className="detail">
          <h3>{sel.title}</h3>
          <div className="small muted mono">{sel.id} · updated {ago(sel.updated_at)}</div>
          <p><b>Owner:</b> {name(sel.owner_id)}{sel.delegated_by_id && <> · delegated by {name(sel.delegated_by_id)}</>}</p>
          <p><b>State:</b> {sel.state}{sel.required ? '' : ' (optional for its parent)'}</p>
          {data.spend?.[sel.id] && <p><b>Spending (effective):</b> {dollars(data.spend[sel.id].direct_nanos)} direct · {dollars(data.spend[sel.id].total_nanos)} including sub-objectives</p>}
          {sel.description && <p>{sel.description}</p>}
          <p><b>Completion conditions:</b> {sel.completion_conditions || <span className="muted">none recorded</span>}</p>
          <div><b>Dependencies:</b> {data.dependencies.filter((d) => d.objective_id === sel.id).map((d) => <button key={d.depends_on_id} className="link" onClick={() => setSelected(d.depends_on_id)}>{data.objectives.find((o) => o.id === d.depends_on_id)?.title ?? d.depends_on_id}</button>)}</div>
          <div><b>Accepted work / actual dispatch</b></div>
          {workError && <p role="alert">Work status unavailable; objective labels are not dispatch evidence.</p>}
          {!workError && work.filter((w) => w.objective_id === sel.id).length === 0 && <p className="muted small">No accepted actionable obligation. Planned/open objectives alone do not dispatch.</p>}
          {work.filter((w) => w.objective_id === sel.id).map((w) => <div key={w.id} className="evidence">
            <div><b>{w.state}</b> · owner <button className="link" onClick={() => onSelectAgent(w.owner_id)}>{name(w.owner_id)}</button>{w.manager_id && <> · accountable manager {name(w.manager_id)}</>}</div>
            <div className="small mono">Obligation {w.id} · next action {w.next_action}</div>
            {w.reason && <p><b>Reason:</b> {w.reason}</p>}
            {w.resume_condition && <p><b>Resumes when:</b> {w.resume_condition}</p>}
          </div>)}
          <div><b>Evidence</b></div>
          {evidence.length === 0 ? <div className="muted small">none yet</div> : evidence.map((e, i) => <div key={i} className="evidence"><div className="small muted">{e.by} · {e.at}</div><pre>{e.evidence}</pre></div>)}
          <div><b>Responsible team:</b> {byParent(sel.id).map((c) => name(c.owner_id)).filter((v, i, a) => a.indexOf(v) === i).join(', ') || name(sel.owner_id)}</div>
        </div>
      )}
    </div>
  )
}
