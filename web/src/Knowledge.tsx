import { useEffect, useState } from 'react'
import { api, ago, type Project } from './api'
import { Markdown } from './Markdown'

interface Doc { id: string; doc_key: string; revision: number; kind: string; title: string; content: string; author_agent_id?: string; provenance: string; created_at: number }

export function Knowledge({ project, tick }: { project: Project; tick: number }) {
  const [docs, setDocs] = useState<Doc[]>([])
  const [selected, setSelected] = useState<string>()
  useEffect(() => { api.get<Doc[]>(`/projects/${project.id}/knowledge`).then(setDocs).catch(console.error) }, [project.id, tick])
  const doc = docs.find((d) => d.doc_key === selected)
  return (
    <div className="split">
      <div className="doc-list">
        {docs.length === 0 && <div className="muted">No knowledge documents yet. Agents record requirements, decisions, and observations here.</div>}
        {docs.map((d) => (
          <div key={d.id} className={`doc ${selected === d.doc_key ? 'selected' : ''}`} onClick={() => setSelected(d.doc_key)}>
            <span className={`state-pill ${d.kind}`}>{d.kind}</span> {d.title} <span className="small muted mono">{d.doc_key} r{d.revision}</span>
          </div>
        ))}
      </div>
      {doc && (
        <div className="detail">
          <h3>{doc.title}</h3>
          <div className="small muted">{doc.doc_key} · revision {doc.revision} · {doc.kind} · {ago(doc.created_at)}</div>
          <Markdown text={doc.content} />
          {doc.provenance && <div className="small muted">Provenance: {doc.provenance}</div>}
        </div>
      )}
    </div>
  )
}
