export interface CostSummary {
  cash_nanos?: number
  cash_known_nanos: number
  cash_unknown_count: number
  effective_nanos: number
  reserved_effective_nanos: number
  reserved_cash_nanos: number
  budget_nanos: number
  cash_ceiling_nanos?: number
}

export interface Project {
  id: string
  name: string
  description: string
  workspace_path?: string
  root_agent_id?: string
  root_objective_id?: string
  state: string
  effective_budget_nanos: number
  cash_ceiling_nanos?: number
  created_at: number
  costs: CostSummary
  agents: number
}

export interface Agent {
  id: string
  name: string
  title: string
  role: 'manager' | 'specialist'
  lifetime: 'persistent' | 'ephemeral'
  manager_id?: string
  model_id: string
  reasoning_effort?: string
  state: string
  compaction_mode: string
  primary_session_id?: string
  created_at: number
  retired_at?: number
  status?: string
  direct_nanos: number
  delegated_nanos: number
  cash_direct_nanos?: number
  consultations?: { id: string; question?: string; checkpoint?: number }[]
  pause?: { id: string; scope: string; reason: string; release_condition: string; state: string; requester: string }
}

export interface Objective {
  id: string
  project_id: string
  parent_id?: string
  owner_id?: string
  delegated_by_id?: string
  title: string
  description: string
  completion_conditions: string
  state: string
  required: boolean
  evidence: string
  created_at: number
  updated_at: number
}

export interface ConversationEntry {
  id: string
  author: 'user' | 'manager' | 'system'
  agent_id?: string
  body: string
  created_at: number
}

export interface JournalEvent {
  id: number
  project_id?: string
  kind: string
  entity_type?: string
  entity_id?: string
  agent_id?: string
  payload: string
  created_at: number
}

export interface Session {
  id: string
  agent_id: string
  kind: string
  state: string
  model_id: string
  compaction_mode: string
  parent_session_id?: string
  checkpoint_seq?: number
  turn_count: number
  purpose?: string
  result?: string
  created_at: number
}

export interface SessionItem {
  id: string
  seq: number
  kind: string
  turn: number
  token_estimate: number
  created_at: number
  payload: any
}

export interface ModelInfo {
  id: string
  provider: string
  display_name: string
  input_per_million?: string
  cached_input_per_million?: string
  output_per_million?: string
  billing: string
  usable: boolean
  notes: string
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const res = await fetch(`/api/v1${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-Ainur': '1' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`
    try {
      const j = await res.json()
      if (j.error) message = j.error
    } catch { /* not json */ }
    throw new Error(message)
  }
  const text = await res.text()
  return (text ? JSON.parse(text) : undefined) as T
}

export const api = {
  get: <T,>(path: string) => request<T>('GET', path),
  post: <T,>(path: string, body?: unknown) => request<T>('POST', path, body ?? {}),
  patch: <T,>(path: string, body: unknown) => request<T>('PATCH', path, body),
}

export function dollars(nanos: number | undefined | null): string {
  if (nanos === undefined || nanos === null) return 'unknown'
  const d = nanos / 1e9
  if (Math.abs(d) >= 1) return `$${d.toFixed(2)}`
  if (Math.abs(d) >= 0.01) return `$${d.toFixed(4)}`
  return `$${d.toFixed(6)}`
}

export function ago(ms: number): string {
  const s = Math.max(0, (Date.now() - ms) / 1000)
  if (s < 60) return `${Math.floor(s)}s ago`
  if (s < 3600) return `${Math.floor(s / 60)}m ago`
  if (s < 86400) return `${Math.floor(s / 3600)}h ago`
  return new Date(ms).toLocaleDateString()
}

/** Subscribes to the server-sent event stream; returns an unsubscribe function. */
export function subscribe(projectId: string | undefined, onJournal: (e: JournalEvent) => void, onDelta: (d: { session_id: string; kind: string; text: string }) => void): () => void {
  const source = new EventSource(`/api/v1/events/stream${projectId ? `?project=${projectId}` : ''}`)
  source.addEventListener('journal', (e) => onJournal(JSON.parse((e as MessageEvent).data)))
  source.addEventListener('delta', (e) => onDelta(JSON.parse((e as MessageEvent).data)))
  return () => source.close()
}
