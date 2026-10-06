import { useCallback, useEffect, useState } from 'react'
import { api, type Project } from './api'

type Book = { id: string; project_id: string; title: string; created_at: number }
type Cell = { id: string; position: number; language: string; revision: number; source: string }
type Run = { id: string; revision: number; ordinal: number; status: string; output: string; error: string; actor: string; kernel: string; started_at: number; ended_at?: number }
type CellView = { cell: Cell; runs: Run[]; stale: boolean }
type View = { workbook: Book; cells: CellView[]; execution_model: string }

/** A deliberately explicit one-shot workflow; reload only reads, never executes. */
export function Workbooks({ project }: { project: Project }) {
  const base = `/projects/${project.id}/workbooks`
  const [books, setBooks] = useState<Book[]>([])
  const [bookId, setBookId] = useState<string>()
  const [view, setView] = useState<View>()
  const [title, setTitle] = useState('')
  const [newSource, setNewSource] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const load = useCallback(async (id?: string) => {
    setBooks(await api.get<Book[]>(base))
    setView(id ? await api.get<View>(`${base}/${id}`) : undefined)
  }, [base])
  useEffect(() => { setBookId(undefined); setView(undefined); load().catch((e) => setError(String(e))) }, [load])
  async function act(task: () => Promise<unknown>) {
    setBusy(true); setError('')
    try { await task(); await load(bookId) } catch (e) { setError(String(e)); await load(bookId).catch(() => {}) }
    finally { setBusy(false) }
  }
  return <div className="workbook">
    <h2>Workbooks</h2>
    <p className="muted">PowerShell only. Every Run starts a fresh runspace inside the server (not a separate process or sandbox): variables do not persist between cells. Runs may change files or external systems; only press Run when you intend those effects. Reload never runs code. Interrupted or unknown outcomes must be inspected before any manual retry.</p>
    {error && <p role="alert" className="workbook-error">{error}</p>}
    <div className="workbook-row">
      <select aria-label="Workbook" value={bookId ?? ''} onChange={(e) => { const id = e.target.value || undefined; setBookId(id); load(id).catch((x) => setError(String(x))) }}>
        <option value="">Choose a workbook</option>{books.map((b) => <option key={b.id} value={b.id}>{b.title}</option>)}
      </select>
      <input aria-label="New workbook title" placeholder="New workbook title" value={title} onChange={(e) => setTitle(e.target.value)} />
      <button disabled={busy || !title.trim()} onClick={async () => {
        setBusy(true); setError('')
        try { const b = await api.post<Book>(base, { title }); setTitle(''); setBookId(b.id); await load(b.id) }
        catch (e) { setError(String(e)) }
        finally { setBusy(false) }
      }}>Create</button>
    </div>
    {view && <>
      <p className="muted">{view.execution_model} · Workbook {view.workbook.id}</p>
      {view.cells.map((entry) => <CellEditor key={entry.cell.id} entry={entry} disabled={busy} onEdit={(source) => act(() => api.put(`${base}/${bookId}/cells/${entry.cell.id}`, { source, expected_revision: entry.cell.revision }))} onRun={(revision) => act(() => api.post(`${base}/${bookId}/cells/${entry.cell.id}/runs`, { revision }))} />)}
      <div className="workbook-cell">
        <h3>New PowerShell cell</h3><textarea aria-label="New cell source" rows={5} value={newSource} onChange={(e) => setNewSource(e.target.value)} />
        <button disabled={busy} onClick={() => act(async () => { await api.post(`${base}/${bookId}/cells`, { language: 'powershell', source: newSource }); setNewSource('') })}>Add cell</button>
      </div>
    </>}
  </div>
}

function CellEditor({ entry, disabled, onEdit, onRun }: { entry: CellView; disabled: boolean; onEdit: (source: string) => Promise<void>; onRun: (revision: number) => Promise<void> }) {
  const { cell, runs, stale } = entry
  const [source, setSource] = useState(cell.source)
  const active = runs.some((run) => run.status === 'running' || run.status === 'queued')
  useEffect(() => { setSource(cell.source) }, [cell.source, cell.revision])
  return <article className="workbook-cell">
    <h3>Cell {cell.position} · revision {cell.revision} {stale && <span className="workbook-stale">· latest result is stale</span>}</h3>
    <textarea aria-label={`Cell ${cell.position} source`} rows={6} value={source} onChange={(e) => setSource(e.target.value)} />
    <div className="workbook-row"><button disabled={disabled || source === cell.source} onClick={() => onEdit(source)}>Save revision</button>
      <button disabled={disabled || active || source !== cell.source} title="Runs the saved revision; side effects may happen again" onClick={() => { if (window.confirm(`Execute saved revision ${cell.revision} of cell ${cell.position}? This may repeat side effects.`)) onRun(cell.revision) }}>Run revision {cell.revision}</button>
      {active && <span className="muted">Execution is active; wait and reload to inspect its outcome.</span>}
      {source !== cell.source && <span className="muted">Save your changes before running.</span>}</div>
    {runs.map((run) => <details key={run.id} open={run === runs[0]}><summary>Run #{run.ordinal} · revision {run.revision} · {run.status} {run.revision !== cell.revision ? '(stale)' : ''} · {run.actor} · {new Date(run.started_at).toLocaleString()}</summary>
      <p className="muted">{run.kernel} · {run.id} {run.ended_at ? `· ended ${new Date(run.ended_at).toLocaleString()}` : run.status === 'running' || run.status === 'queued' ? '· active in this server' : '· end unknown'}</p>
      <strong>Output</strong><pre>{run.output || '(empty)'}</pre><strong>Errors</strong><pre>{run.error || '(empty)'}</pre>
      {run.status === 'unknown' && <p role="alert">Execution outcome unknown; check external effects before explicitly rerunning.</p>}
    </details>)}
  </article>
}
