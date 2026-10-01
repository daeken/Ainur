import { useEffect, useRef, useState } from 'react'
import { api, ago, type Agent, type ConversationEntry, type Project } from './api'
import { Markdown } from './Markdown'

export function Conversation({ project, agents, tick, deltas }: { project: Project; agents: Agent[]; tick: number; deltas: Record<string, string> }) {
  const [entries, setEntries] = useState<ConversationEntry[]>([])
  const [text, setText] = useState('')
  const [error, setError] = useState<string>()
  const [sending, setSending] = useState(false)
  const bottom = useRef<HTMLDivElement>(null)
  const root = agents.find((a) => a.id === project.root_agent_id)

  useEffect(() => { api.get<ConversationEntry[]>(`/projects/${project.id}/conversation`).then(setEntries).catch(console.error) }, [project.id, tick])
  useEffect(() => { bottom.current?.scrollIntoView({ behavior: 'smooth' }) }, [entries.length])

  const send = async () => {
    if (!text.trim()) return
    setSending(true)
    setError(undefined)
    try {
      await api.post(`/projects/${project.id}/conversation`, { text })
      setText('')
      setEntries(await api.get<ConversationEntry[]>(`/projects/${project.id}/conversation`))
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setSending(false)
    }
  }

  const working = root && root.status && root.status !== 'idle'
  const streaming = root?.primary_session_id ? deltas[root.primary_session_id] : undefined
  return (
    <div className="conversation">
      <div className="messages">
        {entries.length === 0 && <div className="empty">Tell {root?.name ?? 'the manager'} what you want built. The manager is your only point of contact; the team works in the background.</div>}
        {entries.map((e) => (
          <div key={e.id} className={`message ${e.author}`}>
            <div className="message-meta">{e.author === 'user' ? 'You' : e.author === 'system' ? 'Runtime' : root?.name ?? 'Manager'} · {ago(e.created_at)}</div>
            <div className="message-body"><Markdown text={e.body} /></div>
          </div>
        ))}
        {working && (
          <div className="message manager pending">
            <div className="message-meta">{root?.name} · {root?.status}</div>
            {streaming && <div className="message-body streaming">{streaming}</div>}
          </div>
        )}
        <div ref={bottom} />
      </div>
      <form className="composer" onSubmit={(e) => { e.preventDefault(); send() }}>
        <textarea value={text} onChange={(e) => setText(e.target.value)} placeholder={`Message ${root?.name ?? 'the manager'}…`}
          onKeyDown={(e) => { if (e.key === 'Enter' && (e.metaKey || e.ctrlKey)) { e.preventDefault(); send() } }} rows={3} />
        <div className="composer-actions">
          {error && <span className="error">{error}</span>}
          <span className="muted small">⌘↩ to send</span>
          <button disabled={sending || !text.trim()}>Send</button>
        </div>
      </form>
    </div>
  )
}
