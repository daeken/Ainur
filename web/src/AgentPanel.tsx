import { useEffect, useState } from 'react'
import { api, ago, dollars, type Agent, type Objective, type Session } from './api'
import { Markdown } from './Markdown'
import { SessionBrowser } from './SessionBrowser'
import { SessionTranscript } from './SessionTranscript'

interface AgentDetail {
  agent: Agent & { instructions: string; termination_condition?: string }
  identity?: { revision: number; content: string; created_at: number }
  identity_revisions: number
  manager?: Agent
  reports: Agent[]
  objectives: Objective[]
  sessions: Session[]
  spend: { direct_nanos: number; delegated_nanos: number; cash_direct_nanos?: number }
}

interface ContextInfo {
  session: Session
  compaction_mode: string
  view: { summary_item_id?: string; cutoff_seq: number; elided: string[] }
  policy: Record<string, number | string>
  usable_budget: number
  estimated_tokens?: number
  tool_tokens: number
  tool_budget: number
  loaded_tools: { name: string; version: string; pinned: boolean; tokens: number }[]
  compactions: { id: string; mode: string; from_seq: number; through_seq: number; source_tokens: number; summary_tokens?: number; state: string; error?: string; created_at: number }[]
  elided: { invocation_id: string; tool_name: string; chars: number; description?: string }[]
  objects?: { handle: string; type_name: string; summary: string }[]
  status?: string
}

export function AgentPanel({ agentId, agents, tick, onClose, onSelect }: { agentId: string; agents: Agent[]; tick: number; onClose: () => void; onSelect: (id: string) => void }) {
  const [detail, setDetail] = useState<AgentDetail>()
  const [sessionId, setSessionId] = useState<string>()
  const [view, setView] = useState<'overview' | 'browser' | 'context' | 'transcript'>('overview')
  useEffect(() => { api.get<AgentDetail>(`/agents/${agentId}`).then((d) => { setDetail(d); setSessionId((s) => s && d.sessions.some((x) => x.id === s) ? s : d.agent.primary_session_id ?? d.sessions[0]?.id) }).catch(console.error) }, [agentId, tick])
  if (!detail) return <aside className="panel" />
  const a = detail.agent
  const live = agents.find((x) => x.id === a.id)
  return (
    <aside className="panel">
      <div className="panel-header">
        <div>
          <h2>{a.name}</h2>
          <div className="small muted">{a.title} · {a.role} · {a.lifetime} · <span className="mono">{a.id}</span></div>
          <div className="small muted"><span className="mono">{a.model_id}</span>{a.reasoning_effort && <> <span className="badge effort-badge" title="Reasoning effort">{a.reasoning_effort}</span></>}</div>
        </div>
        <button className="link" onClick={onClose}>✕</button>
      </div>
      <div className="panel-tabs">
        {(['overview', 'browser', 'context', 'transcript'] as const).map((v) => <button key={v} className={view === v ? 'active' : ''} onClick={() => setView(v)}>{v}</button>)}
      </div>
      {view === 'overview' && (
        <div className="panel-body">
          <p><b>State:</b> {a.state}{live?.status ? ` · ${live.status}` : ''} <span className="actions">
            {a.state === 'paused' ? <button className="small" onClick={() => api.post(`/agents/${a.id}/resume`)}>Resume</button>
              : a.state !== 'retired' && a.state !== 'terminated' ? <button className="small" onClick={() => api.post(`/agents/${a.id}/pause`)}>Pause</button> : null}
          </span></p>
          <p><b>Manager:</b> {detail.manager ? <button className="link" onClick={() => onSelect(detail.manager!.id)}>{detail.manager.name}</button> : 'none (root manager; talks to the user)'}</p>
          {detail.reports.length > 0 && <p><b>Reports:</b> {detail.reports.map((r) => <button key={r.id} className="link" onClick={() => onSelect(r.id)}>{r.name}</button>)}</p>}
          <p><b>Model:</b> <span className="mono">{a.model_id}</span> {a.reasoning_effort ? <span className="badge effort-badge" title="Reasoning effort">{a.reasoning_effort}</span> : <span className="small muted"> (no reasoning effort)</span>} · compaction {a.compaction_mode}</p>
          <p><b>Spending:</b> direct {dollars(detail.spend.direct_nanos)} (cash {dollars(detail.spend.cash_direct_nanos)}) · delegated {dollars(detail.spend.delegated_nanos)}</p>
          <div><b>Owned objectives</b></div>
          {detail.objectives.length === 0 ? <div className="small muted">none</div> : detail.objectives.map((o) => <div key={o.id} className="small"><span className={`state-pill ${o.state}`}>{o.state}</span> {o.title}</div>)}
          {a.instructions && <><div><b>Instructions</b></div><div className="small pre">{a.instructions}</div></>}
          {detail.identity && <><div><b>Identity file</b> <span className="small muted">revision {detail.identity.revision} of {detail.identity_revisions}, agent-owned</span></div><div className="identity"><Markdown text={detail.identity.content} /></div></>}
          <div><b>Sessions</b></div>
          {detail.sessions.map((s) => (
            <div key={s.id} className="small">
              <button className="link" onClick={() => { setSessionId(s.id); setView('transcript') }}>{s.kind}</button> {s.state} · {s.turn_count} turns · {ago(s.created_at)}
              {s.kind === 'consultation' && s.purpose && <div className="muted">{(() => { try { return JSON.parse(s.purpose).question } catch { return '' } })()}</div>}
            </div>
          ))}
        </div>
      )}
      {view === 'browser' && <SessionBrowser agentId={a.id} agentName={a.name} sessionId={sessionId} />}
      {view === 'context' && sessionId && <ContextView sessionId={sessionId} tick={tick} />}
      {view === 'transcript' && sessionId && <SessionTranscript sessionId={sessionId} tick={tick} />}
    </aside>
  )
}

function ContextView({ sessionId, tick }: { sessionId: string; tick: number }) {
  const [info, setInfo] = useState<ContextInfo>()
  useEffect(() => { api.get<ContextInfo>(`/sessions/${sessionId}/context`).then(setInfo).catch(console.error) }, [sessionId, tick])
  if (!info) return null
  const used = (info.estimated_tokens ?? 0) + info.tool_tokens
  return (
    <div className="panel-body">
      <p><b>Compaction mode:</b> {info.compaction_mode} · turn {info.session.turn_count}</p>
      <p><b>Context:</b> ~{used.toLocaleString()} of {info.usable_budget.toLocaleString()} usable tokens{info.estimated_tokens === undefined ? ' (session not loaded)' : ''}</p>
      <div className="bar"><div style={{ width: `${Math.min(100, (100 * used) / Math.max(1, info.usable_budget))}%` }} /></div>
      <p><b>Tools:</b> {info.tool_tokens.toLocaleString()} tokens of {info.tool_budget.toLocaleString()} budget ({info.usable_budget ? ((100 * info.tool_tokens) / info.usable_budget).toFixed(1) : 0}% of context)</p>
      <div className="tool-list">{info.loaded_tools.sort((a, b) => b.tokens - a.tokens).map((t) => <span key={t.name} className="badge" title={t.version}>{t.name}{t.pinned ? '*' : ''} {t.tokens}</span>)}</div>
      <p><b>Summary:</b> {info.view.summary_item_id ? `covers items through #${info.view.cutoff_seq}` : 'none (full transcript visible)'}</p>
      <div><b>Recent compactions</b></div>
      {info.compactions.length === 0 ? <div className="small muted">none</div> : info.compactions.map((c) => <div key={c.id} className="small">{c.mode} #{c.from_seq}–#{c.through_seq}: {c.source_tokens} → {c.summary_tokens ?? '?'} tokens · {c.state} · {ago(c.created_at)}{c.error ? ` · ${c.error}` : ''}</div>)}
      <div><b>Elided results</b> <span className="small muted">({info.elided.length}; raw results remain retrievable)</span></div>
      {info.elided.slice(-30).map((e) => <div key={e.invocation_id} className="small mono">{e.invocation_id} {e.tool_name} {e.chars} chars {e.description ?? ''}</div>)}
      {info.objects && info.objects.length > 0 && <><div><b>Live objects</b></div>{info.objects.map((o) => <div key={o.handle} className="small mono">{o.handle} {o.type_name} — {o.summary}</div>)}</>}
    </div>
  )
}
