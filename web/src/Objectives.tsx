import { useEffect, useState } from 'react'
import { api, ago, type Agent, type Objective, type Project } from './api'

interface Data { objectives: Objective[]; dependencies: { objective_id: string; depends_on_id: string }[] }

export function Objectives({ project, agents, tick, onSelectAgent }: { project: Project; agents: Agent[]; tick: number; onSelectAgent: (id: string) => void }) {
  const [data, setData] = useState<Data>({ objectives: [], dependencies: [] })
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
          {sel.description && <p>{sel.description}</p>}
          <p><b>Completion conditions:</b> {sel.completion_conditions || <span className="muted">none recorded</span>}</p>
          <div><b>Dependencies:</b> {data.dependencies.filter((d) => d.objective_id === sel.id).map((d) => <button key={d.depends_on_id} className="link" onClick={() => setSelected(d.depends_on_id)}>{data.objectives.find((o) => o.id === d.depends_on_id)?.title ?? d.depends_on_id}</button>)}</div>
          <div><b>Evidence</b></div>
          {evidence.length === 0 ? <div className="muted small">none yet</div> : evidence.map((e, i) => <div key={i} className="evidence"><div className="small muted">{e.by} · {e.at}</div><pre>{e.evidence}</pre></div>)}
          <div><b>Responsible team:</b> {byParent(sel.id).map((c) => name(c.owner_id)).filter((v, i, a) => a.indexOf(v) === i).join(', ') || name(sel.owner_id)}</div>
        </div>
      )}
    </div>
  )
}
