import { useEffect, useState } from 'react'
import { api, type ModelInfo, type Project } from './api'

export function NewProject({ onDone }: { onDone: (id?: string) => void }) {
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
  const [workspace, setWorkspace] = useState('')
  const [budget, setBudget] = useState('5')
  const [model, setModel] = useState('')
  const [models, setModels] = useState<ModelInfo[]>([])
  const [error, setError] = useState<string>()
  useEffect(() => { api.get<ModelInfo[]>('/models').then((m) => { setModels(m.filter((x) => x.usable)); setModel(m.find((x) => x.usable && x.id === 'deepseek-v4-pro')?.id ?? m.find((x) => x.usable)?.id ?? '') }).catch(console.error) }, [])
  const submit = async () => {
    try {
      const p = await api.post<Project>('/projects', { name, description, workspace_path: workspace || null, budget_dollars: Number(budget), manager_model: model || null })
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
      <label>Workspace directory<input value={workspace} onChange={(e) => setWorkspace(e.target.value)} placeholder="~/projects/something" /></label>
      <label>Effective budget (USD)<input value={budget} onChange={(e) => setBudget(e.target.value)} /></label>
      <label>Root manager model<select value={model} onChange={(e) => setModel(e.target.value)}>{models.map((m) => <option key={m.id} value={m.id}>{m.id}</option>)}</select></label>
      {error && <div className="error">{error}</div>}
      <div className="row"><button type="submit">Create</button><button type="button" className="link" onClick={() => onDone()}>Cancel</button></div>
    </form>
  )
}
