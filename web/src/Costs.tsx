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
  const finiteEffectiveRemaining = s.effective_remaining_nanos ?? (s.budget_nanos > 0 ? s.budget_nanos - s.effective_nanos - s.reserved_effective_nanos : null)
  const cashHeadroom = s.cash_remaining_status === 'known' && s.cash_remaining_nanos != null
    ? `remaining ${dollars(s.cash_remaining_nanos)}`
    : s.cash_remaining_status === 'unknown_cost' ? 'remaining unknown (unpriced cash)'
    : s.cash_ceiling_nanos != null ? 'remaining unreported (cash ceiling in effect)'
    : 'no cash headroom limit'
  return (
    <div className="costs">
      <div className="cards">
        <div className="card"><div className="muted small">Effective reference valuation (not cash)</div><div className="big">{dollars(s.effective_nanos)}</div><div className="small muted">{s.no_effective_limit || s.budget_nanos === 0 ? 'No effective limit' : `limit ${dollars(s.effective_limit_nanos ?? s.budget_nanos)} · remaining ${dollars(finiteEffectiveRemaining)}`} · {dollars(s.reserved_effective_nanos)} reserved</div></div>
        <div className="card"><div className="muted small">Known cash expense (actual or estimated)</div><div className="big">{dollars(s.cash_known_nanos)}</div><div className="small muted">{s.cash_unknown_count} settled charges with unknown cash · {s.reserved_cash_unknown_count ?? 0} unknown in flight</div><div className="small muted">{s.cash_ceiling_nanos != null ? `cash ceiling ${dollars(s.cash_ceiling_nanos)}` : 'No cash ceiling'} · {cashHeadroom} · {dollars(s.reserved_cash_nanos)} cash reserved</div></div>
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
        <tbody>{data.by_category.map((c) => <tr key={c.category}><td>{c.category}</td><td>{c.count}</td><td>{dollars(c.effective_nanos)}</td><td>{dollars(c.cash_nanos)} known{c.unknown_cash > 0 ? ` + ${c.unknown_cash} unknown` : ''}</td></tr>)}</tbody>
      </table>
      <h3>Recent charges</h3>
      <table>
        <thead><tr><th>When</th><th>Agent</th><th>Category</th><th>Effective</th><th>Cash</th><th>Basis</th><th>Valuation</th></tr></thead>
        <tbody>{data.recent.map((e) => (
          <tr key={e.id}><td>{ago(e.created_at)}</td><td>{name(e.agent_id)}</td><td>{e.category}</td><td>{dollars(e.effective_nanos)}</td><td>{e.cash_nanos == null ? 'unknown' : dollars(e.cash_nanos)}</td><td>{e.cash_basis}</td><td className="small muted">{e.valuation}</td></tr>
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
      <div className="small muted">Prices are USD per million tokens from the seeded catalog (unverified estimates). Effective charges are reference valuations, not cash payments; they include configured premiums. Subscription use still has an effective value. Cash charges may be estimated, and a cash ceiling is an admission control based on estimates, not a bank-balance guarantee: pending calls can settle above estimates. Attributions above remain visible by agent, category and event.</div>
    </div>
  )
}
