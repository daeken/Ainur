import { useEffect, useState } from 'react'
import { api, type ModelInfo, type Project } from './api'
import { BudgetFields, budgetPayload, initialBudgetChoice } from './BudgetFields'

export function NewProject({ onDone }: { onDone: (id?: string) => void }) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [workspace, setWorkspace] = useState('')
  const [budget, setBudget] = useState(initialBudgetChoice)
  const [model, setModel] = useState('')
  const [organization, setOrganization] = useState<'engineering' | 'single'>('engineering')
  const [models, setModels] = useState<ModelInfo[]>([])
  const [error, setError] = useState<string>()
  useEffect(() => {
    Promise.all([api.get<ModelInfo[]>('/models'), api.get<{ engineering_model: string }>('/project-defaults')])
      .then(([m, defaults]) => { setModels(m.filter((x) => x.engineering_usable)); setModel(defaults.engineering_model) })
      .catch((e) => setError((e as Error).message))
  }, [])
  const submit = async () => {
    try {
      setError(undefined)
      const p = await api.post<Project>('/projects', { name, description, workspace_path: workspace || null, manager_model: model, organization, ...budgetPayload(budget) })
      onDone(p.id)
    } catch (e) {
      setError((e as Error).message)
    }
  }
  return (
    <form className="new-project-form" onSubmit={(e) => { e.preventDefault(); submit() }}>
      <h2>New project</h2>
      <label>Name<input value={name} onChange={(e) => setName(e.target.value)} required /></label>
      <label>Description (the root objective)<textarea value={description} onChange={(e) => setDescription(e.target.value)} rows={4} /></label>
      <label>Existing workspace directory<input value={workspace} onChange={(e) => setWorkspace(e.target.value)} placeholder="~/projects/something" required={organization === 'engineering'} /></label>
      <p>Select an existing directory for this project's files (for example, a disposable Git repository). Ainur stores its project state separately; no repository is imported or cloned.</p>
      <label>Organization<select value={organization} onChange={(e) => setOrganization(e.target.value as 'engineering' | 'single')}><option value="engineering">Engineering: lead, delivery manager, independent reviewer</option><option value="single">Single manager (manual team setup)</option></select></label>
      <BudgetFields value={budget} onChange={setBudget} />
      <label>Team model<select value={model} onChange={(e) => setModel(e.target.value)} required>{!models.some((m) => m.id === model) && <option value={model} disabled>{model || 'Loading models…'}</option>}{models.map((m) => <option key={m.id} value={m.id}>{m.display_name} ({m.billing})</option>)}</select></label>
      {model && !models.some((m) => m.id === model) && <p className="error">The configured team model is unavailable. Configure its provider or explicitly choose an available model.</p>}
      {error && <div className="error">{error}</div>}
      <div className="row"><button type="submit" disabled={!models.some((m) => m.id === model)}>Create</button><button type="button" className="link" onClick={() => onDone()}>Cancel</button></div>
    </form>
  )
}
