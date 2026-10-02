import { useCallback, useEffect, useRef, useState } from 'react'
import { api, dollars, subscribe, type Agent, type Project } from './api'
import { Conversation, type StoredDraft } from './Conversation'
import { OrgChart } from './OrgChart'
import { Objectives } from './Objectives'
import { Costs } from './Costs'
import { Activity } from './Activity'
import { AgentPanel } from './AgentPanel'
import { NewProject } from './NewProject'
import { Knowledge } from './Knowledge'
import { BudgetFields, budgetPayload, initialBudgetChoice } from './BudgetFields'

type Tab = 'conversation' | 'organization' | 'objectives' | 'costs' | 'activity' | 'knowledge'

export function App() {
  const [projects, setProjects] = useState<Project[]>([])
  // In-memory project/root/session drafts outlive the conditional Conversation view,
  // but intentionally do not survive a page reload or expose File bytes to localStorage.
  const conversationDrafts = useRef(new Map<string, StoredDraft>())
  const [projectId, setProjectId] = useState<string | undefined>(() => localStorage.getItem('ainur.project') ?? undefined)
  const [tab, setTab] = useState<Tab>('conversation')
  const [agents, setAgents] = useState<Agent[]>([])
  const [selectedAgent, setSelectedAgent] = useState<string | undefined>()
  const [tick, setTick] = useState(0)
  const [creating, setCreating] = useState(false)
  const [deltas, setDeltas] = useState<Record<string, string>>({})
  const refreshTimer = useRef<number | undefined>(undefined)

  const project = projects.find((p) => p.id === projectId)

  const loadProjects = useCallback(async () => {
    const list = await api.get<Project[]>('/projects')
    setProjects(list)
    if (!projectId && list.length > 0) setProjectId(list[list.length - 1].id)
  }, [projectId])

  useEffect(() => { loadProjects().catch(console.error) }, [loadProjects, tick])

  useEffect(() => {
    if (!projectId) return
    localStorage.setItem('ainur.project', projectId)
    api.get<Agent[]>(`/projects/${projectId}/agents`).then(setAgents).catch(console.error)
  }, [projectId, tick])

  // Live updates: coalesce journal events into a refresh tick; stream model output per session.
  useEffect(() => {
    if (!projectId) return
    return subscribe(projectId, () => {
      window.clearTimeout(refreshTimer.current)
      refreshTimer.current = window.setTimeout(() => setTick((t) => t + 1), 250)
    }, (d) => {
      if (d.kind !== 'content' && d.kind !== 'reasoning') return
      setDeltas((prev) => ({ ...prev, [d.session_id]: ((prev[d.session_id] ?? '') + d.text).slice(-600) }))
    })
  }, [projectId])

  // Clear streamed text for sessions that went idle.
  useEffect(() => {
    setDeltas((prev) => {
      const next: Record<string, string> = {}
      for (const a of agents) if (a.primary_session_id && prev[a.primary_session_id] && a.status && a.status !== 'idle') next[a.primary_session_id] = prev[a.primary_session_id]
      return next
    })
  }, [agents])

  const costs = project?.costs
  return (
    <div className="app">
      <aside className="sidebar">
        <div className="brand">Ainur</div>
        <div className="sidebar-section">Projects (Arda)</div>
        {projects.map((p) => (
          <button key={p.id} className={`project-item ${p.id === projectId ? 'active' : ''}`} onClick={() => { setProjectId(p.id); setSelectedAgent(undefined) }}>
            <span className="project-name">{p.name}</span>
            <span className="muted small">{p.agents} agents · {dollars(p.costs.effective_nanos)} effective</span>
          </button>
        ))}
        <button className="new-project" onClick={() => setCreating(true)}>+ New project</button>
        <RuntimeControls />
      </aside>

      <main className="main">
        {creating && <NewProject onDone={(id) => { setCreating(false); if (id) { setProjectId(id); setTick((t) => t + 1) } }} />}
        {!creating && !project && <div className="empty">Create a project to start a team.</div>}
        {!creating && project && (
          <>
            <header className="project-header">
              <div>
                <h1>{project.name}</h1>
                <div className="muted">{project.description}</div>
                {project.workspace_path && <div className="muted small mono">{project.workspace_path}</div>}
              </div>
              {costs && <BudgetCard project={project} onChanged={() => setTick((t) => t + 1)} />}
            </header>
            <nav className="tabs">
              {(['conversation', 'organization', 'objectives', 'costs', 'activity', 'knowledge'] as Tab[]).map((t) => (
                <button key={t} className={tab === t ? 'active' : ''} onClick={() => setTab(t)}>{t[0].toUpperCase() + t.slice(1)}</button>
              ))}
            </nav>
            <section className="content">
              {tab === 'conversation' && <Conversation project={project} agents={agents} tick={tick} deltas={deltas} drafts={conversationDrafts.current} />}
              {tab === 'organization' && <OrgChart agents={agents} deltas={deltas} selected={selectedAgent} onSelect={setSelectedAgent} />}
              {tab === 'objectives' && <Objectives project={project} agents={agents} tick={tick} onSelectAgent={(id) => { setSelectedAgent(id); }} />}
              {tab === 'costs' && <Costs project={project} agents={agents} tick={tick} />}
              {tab === 'activity' && <Activity project={project} agents={agents} tick={tick} />}
              {tab === 'knowledge' && <Knowledge project={project} tick={tick} />}
            </section>
          </>
        )}
      </main>

      {selectedAgent && project && (
        <AgentPanel agentId={selectedAgent} agents={agents} tick={tick} onClose={() => setSelectedAgent(undefined)} onSelect={setSelectedAgent} />
      )}
    </div>
  )
}

function BudgetCard({ project, onChanged }: { project: Project; onChanged: () => void }) {
  const c = project.costs
  const [editing, setEditing] = useState(false)
  const [choice, setChoice] = useState(() => initialBudgetChoice(project))
  const [error, setError] = useState<string>()
  const capped = c.cash_ceiling_nanos != null
  const unlimited = project.no_effective_limit || project.effective_budget_nanos === 0
  const pct = !unlimited && c.budget_nanos > 0 ? Math.max(0, Math.min(100, (100 * (c.effective_nanos + c.reserved_effective_nanos)) / c.budget_nanos)) : null
  // Older backend JSON serializers omit computed properties on CostSummary. Derive the same
  // finite headroom from the authoritative raw nanos until all releases emit the nullable field.
  const effectiveRemaining = c.effective_remaining_nanos ?? (!unlimited ? c.budget_nanos - c.effective_nanos - c.reserved_effective_nanos : null)
  const openEdit = () => { setChoice(initialBudgetChoice(project)); setError(undefined); setEditing(true) }
  const save = async (e: React.FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    try {
      setError(undefined)
      const body = budgetPayload(choice, project)
      if (Object.keys(body).length > 0) await api.patch(`/projects/${project.id}`, body)
      setEditing(false)
      onChanged()
    } catch (err) { setError((err as Error).message) }
  }
  return (
    <div className="budget-card">
      <div className="budget-row"><span>Effective reference valuation</span><b>{dollars(c.effective_nanos)}</b></div>
      <div className="small muted">{unlimited ? 'No effective limit' : `Limit ${dollars(c.effective_limit_nanos ?? c.budget_nanos)} · remaining ${dollars(effectiveRemaining)}`}</div>
      {pct !== null && <div className="bar" role="progressbar" aria-label="Effective reference-cost limit used" aria-valuenow={pct} aria-valuemin={0} aria-valuemax={100}><div style={{ width: `${pct}%` }} /></div>}
      <div className="small muted">{dollars(c.reserved_effective_nanos)} effective reserved in flight</div>
      <div className="budget-row"><span>Cash (known)</span><b>{dollars(c.cash_known_nanos)}</b></div>
      <div className="small muted">{c.cash_unknown_count} settled charges with unknown cash · {c.reserved_cash_unknown_count ?? 0} in-flight requests with unknown cash</div>
      <div className="small muted">{capped ? `Cash ceiling ${dollars(c.cash_ceiling_nanos)}` : 'No cash ceiling'} · {capped && c.cash_remaining_status === 'known' ? `remaining ${dollars(c.cash_remaining_nanos)}` : capped ? 'remaining unknown (unpriced cash)' : 'no cash headroom limit'} · {dollars(c.reserved_cash_nanos)} cash reserved</div>
      <div className="small muted">Cash charges may be actual or estimated; the ceiling controls admission from estimates, not guaranteed bank balance. Pending calls can settle above estimate.</div>
      {editing ? (
        <form className="inline-form budget-edit" onSubmit={save}>
          <BudgetFields value={choice} onChange={setChoice} />
          {error && <div className="error">{error}</div>}
          <div className="row"><button type="submit">Save cost controls</button><button type="button" className="link" onClick={() => setEditing(false)}>Cancel</button></div>
        </form>
      ) : <button className="link small" onClick={openEdit}>Change cost controls</button>}
    </div>
  )
}

function RuntimeControls() {
  const [health, setHealth] = useState<any>()
  const [version, setVersion] = useState<any>()
  useEffect(() => {
    const load = () => {
      fetch('/api/v1/health').then((r) => r.json()).then(setHealth).catch(() => setHealth(undefined))
      fetch('/api/v1/version').then((r) => r.json()).then(setVersion).catch(() => setVersion(undefined))
    }
    load()
    const t = window.setInterval(load, 5000)
    return () => window.clearInterval(t)
  }, [])
  return (
    <div className="runtime">
      <div className="sidebar-section">Runtime</div>
      {health ? (
        <div className="small">
          <div>{health.ready ? '● ready' : '○ not ready'} · generation {version?.generation ?? health.generation}</div>
          {version && <div className="muted">release {version.release}</div>}
          <div className="muted">{health.hosts} live sessions{health.draining ? ' · draining' : ''}</div>
          {health.in_flight?.length > 0 && <div className="muted">{health.in_flight.length} tool call(s) in flight</div>}
          <div className="runtime-buttons">
            {health.draining
              ? <button className="small" onClick={() => api.post('/control/undrain')}>Resume dispatch</button>
              : <button className="small" onClick={() => api.post('/control/drain', { timeout_seconds: 30 })}>Drain</button>}
          </div>
        </div>
      ) : <div className="small muted">unreachable</div>}
    </div>
  )
}
