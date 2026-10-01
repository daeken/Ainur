import { useCallback, useEffect, useRef, useState } from 'react'
import { api, dollars, subscribe, type Agent, type Project } from './api'
import { Conversation } from './Conversation'
import { OrgChart } from './OrgChart'
import { Objectives } from './Objectives'
import { Costs } from './Costs'
import { Activity } from './Activity'
import { AgentPanel } from './AgentPanel'
import { NewProject } from './NewProject'
import { Knowledge } from './Knowledge'

type Tab = 'conversation' | 'organization' | 'objectives' | 'costs' | 'activity' | 'knowledge'

export function App() {
  const [projects, setProjects] = useState<Project[]>([])
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
            <span className="muted small">{p.agents} agents · {dollars(p.costs.effective_nanos)}</span>
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
              {tab === 'conversation' && <Conversation project={project} agents={agents} tick={tick} deltas={deltas} />}
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
  const [budget, setBudget] = useState((project.effective_budget_nanos / 1e9).toString())
  const pct = c.budget_nanos > 0 ? Math.min(100, (100 * (c.effective_nanos + c.reserved_effective_nanos)) / c.budget_nanos) : 0
  return (
    <div className="budget-card">
      <div className="budget-row"><span>Effective</span><b>{dollars(c.effective_nanos)}</b><span className="muted">of {dollars(c.budget_nanos)}</span></div>
      <div className="bar"><div style={{ width: `${pct}%` }} /></div>
      <div className="budget-row small"><span>Cash</span><b>{c.cash_nanos !== undefined && c.cash_nanos !== null ? dollars(c.cash_nanos) : `${dollars(c.cash_known_nanos)} + ${c.cash_unknown_count} unknown`}</b>
        {c.cash_ceiling_nanos ? <span className="muted">ceiling {dollars(c.cash_ceiling_nanos)}</span> : null}</div>
      {c.reserved_effective_nanos > 0 && <div className="small muted">{dollars(c.reserved_effective_nanos)} reserved in flight</div>}
      {editing ? (
        <form className="inline-form" onSubmit={async (e) => { e.preventDefault(); await api.patch(`/projects/${project.id}`, { budget_dollars: Number(budget) }); setEditing(false); onChanged() }}>
          <input value={budget} onChange={(e) => setBudget(e.target.value)} size={6} /> <button>Save</button>
        </form>
      ) : <button className="link small" onClick={() => setEditing(true)}>Change budget</button>}
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
