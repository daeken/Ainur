import { useEffect, useState } from 'react'
import { api, ago, type Agent, type JournalEvent, type Project } from './api'

const quiet = new Set(['session.item', 'session.status', 'model.dispatched', 'cost.recorded'])

export function Activity({ project, agents, tick }: { project: Project; agents: Agent[]; tick: number }) {
  const [events, setEvents] = useState<JournalEvent[]>([])
  const [verbose, setVerbose] = useState(false)
  useEffect(() => { api.get<JournalEvent[]>(`/projects/${project.id}/events?limit=600`).then(setEvents).catch(console.error) }, [project.id, tick])
  const name = (id?: string) => agents.find((a) => a.id === id)?.name
  const shown = events.filter((e) => verbose || !quiet.has(e.kind)).slice().reverse()
  return (
    <div className="activity">
      <label className="small"><input type="checkbox" checked={verbose} onChange={(e) => setVerbose(e.target.checked)} /> show all events</label>
      <table>
        <tbody>{shown.map((e) => {
          let payload: any = {}
          try { payload = JSON.parse(e.payload) } catch { /* ignore */ }
          return (
            <tr key={e.id}>
              <td className="small muted nowrap">{ago(e.created_at)}</td>
              <td className="nowrap">{name(e.agent_id) ?? ''}</td>
              <td className="nowrap mono small">{e.kind}</td>
              <td className="small">{describe(e.kind, payload, name)}</td>
            </tr>
          )
        })}</tbody>
      </table>
    </div>
  )
}

function describe(kind: string, p: any, name: (id?: string) => string | undefined): string {
  switch (kind) {
    case 'tool.finished': return `${p.tool_name} ${p.state}${p.error ? `: ${p.error}` : ''}`
    case 'tool.invoked': return `${p.tool_name}`
    case 'notification.sent': return `${p.type} → ${name(p.to_agent_id) ?? p.to_agent_id}: ${p.preview ?? ''}`
    case 'model.completed': return `${p.model} ${p.purpose}: ${p.input_tokens} in (${p.cached_input_tokens} cached), ${p.output_tokens} out, ${p.tool_calls} tool calls`
    case 'model.failed': return p.error
    case 'compaction.succeeded': return `${p.mode} compaction #${p.from}–#${p.through}: ${p.source_tokens} → ${p.summary_tokens} tokens`
    case 'agent.created': return `${p.name} (${p.role}, ${p.title}) on ${p.model_id}`
    case 'objective.created': return p.title
    case 'objective.updated': return JSON.stringify(p)
    case 'conversation.message': return `${p.author}: ${(p.body ?? '').slice(0, 200)}`
    default: return JSON.stringify(p).slice(0, 300)
  }
}
