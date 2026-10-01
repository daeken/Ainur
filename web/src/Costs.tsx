import { useEffect, useState } from 'react'
import { api, ago, dollars, type Agent, type CostSummary, type ModelInfo, type Project } from './api'

interface CostData {
  summary: CostSummary
  by_agent: { agent_id: string; direct_nanos: number; delegated_nanos: number; cash_direct_nanos?: number }[]
  by_category: { category: string; effective_nanos: number; cash_nanos: number; unknown_cash: number; count: number }[]
  recent: { id: string; agent_id?: string; category: string; cash_nanos?: number; cash_basis: string; effective_nanos: number; valuation?: string; created_at: number }[]
}

export function Costs({ project, agents, tick }: { project: Project; agents: Agent[]; tick: number }) {
  const [data, setData] = useState<CostData>()
  const [models, setModels] = useState<ModelInfo[]>([])
  const [quotas, setQuotas] = useState<any[]>([])
  useEffect(() => { api.get<CostData>(`/projects/${project.id}/costs`).then(setData).catch(console.error) }, [project.id, tick])
  useEffect(() => { api.get<any[]>('/quotas').then(setQuotas).catch(console.error) }, [tick])
  useEffect(() => { api.get<ModelInfo[]>('/models').then(setModels).catch(console.error) }, [])
  if (!data) return null
  const name = (id?: string) => agents.find((a) => a.id === id)?.name ?? '—'
  const s = data.summary
  return (
    <div className="costs">
      <div className="cards">
        <div className="card"><div className="muted small">Effective budget charge</div><div className="big">{dollars(s.effective_nanos)}</div><div className="small muted">of {dollars(s.budget_nanos)} · {dollars(s.reserved_effective_nanos)} reserved</div></div>
        <div className="card"><div className="muted small">Cash expense (actual or estimated)</div><div className="big">{s.cash_nanos != null ? dollars(s.cash_nanos) : dollars(s.cash_known_nanos)}</div><div className="small muted">{s.cash_unknown_count > 0 ? `${s.cash_unknown_count} charges with unknown cash` : 'all charges priced'}{s.cash_ceiling_nanos ? ` · ceiling ${dollars(s.cash_ceiling_nanos)}` : ''}</div></div>
      </div>
      <h3>By agent</h3>
      <table>
        <thead><tr><th>Agent</th><th>Direct (effective)</th><th>Delegated</th><th>Direct cash</th></tr></thead>
        <tbody>{data.by_agent.sort((a, b) => b.direct_nanos + b.delegated_nanos - a.direct_nanos - a.delegated_nanos).map((r) => (
          <tr key={r.agent_id}><td>{name(r.agent_id)}</td><td>{dollars(r.direct_nanos)}</td><td>{dollars(r.delegated_nanos)}</td><td>{dollars(r.cash_direct_nanos)}</td></tr>
        ))}</tbody>
      </table>
      <h3>By category</h3>
      <table>
        <thead><tr><th>Category</th><th>Requests</th><th>Effective</th><th>Cash</th></tr></thead>
        <tbody>{data.by_category.map((c) => <tr key={c.category}><td>{c.category}</td><td>{c.count}</td><td>{dollars(c.effective_nanos)}</td><td>{dollars(c.cash_nanos)}{c.unknown_cash > 0 ? ` + ${c.unknown_cash} unknown` : ''}</td></tr>)}</tbody>
      </table>
      <h3>Recent charges</h3>
      <table>
        <thead><tr><th>When</th><th>Agent</th><th>Category</th><th>Effective</th><th>Cash</th><th>Basis</th><th>Valuation</th></tr></thead>
        <tbody>{data.recent.map((e) => (
          <tr key={e.id}><td>{ago(e.created_at)}</td><td>{name(e.agent_id)}</td><td>{e.category}</td><td>{dollars(e.effective_nanos)}</td><td>{dollars(e.cash_nanos)}</td><td>{e.cash_basis}</td><td className="small muted">{e.valuation}</td></tr>
        ))}</tbody>
      </table>
      {quotas.length > 0 && <>
        <h3>Subscription quota windows (account-wide, all projects)</h3>
        <table>
          <thead><tr><th>Window</th><th>Provider</th><th>Committed</th><th>Held</th><th>Capacity</th><th>Headroom</th><th>Window value</th><th>Resets</th></tr></thead>
          <tbody>{quotas.map((q) => <tr key={q.id}><td className="mono">{q.id}</td><td>{q.provider}</td><td>{q.committed.toLocaleString()} {q.unit}</td><td>{q.held.toLocaleString()}</td><td>{q.capacity.toLocaleString()}</td><td>{(q.headroom * 100).toFixed(0)}%</td><td>${q.window_value_dollars}</td><td>{new Date(q.resets_at).toLocaleString()}</td></tr>)}</tbody>
        </table>
      </>}
      <h3>Model catalog</h3>
      <table>
        <thead><tr><th>Model</th><th>Provider</th><th>Input</th><th>Cached</th><th>Output</th><th>Billing</th><th>Status</th></tr></thead>
        <tbody>{models.map((m) => (
          <tr key={m.id} className={m.usable ? '' : 'muted'}><td className="mono">{m.id}</td><td>{m.provider}</td><td>{m.input_per_million ?? '?'}</td><td>{m.cached_input_per_million ?? '?'}</td><td>{m.output_per_million ?? '?'}</td><td>{m.billing}</td><td>{m.usable ? 'usable' : 'no adapter yet'}</td></tr>
        ))}</tbody>
      </table>
      <div className="small muted">Prices are USD per million tokens from the FlatlineProxy catalog (unverified estimates). Effective charges include configured premiums; subscription use always carries a positive effective charge.</div>
    </div>
  )
}
