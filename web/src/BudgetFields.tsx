import type { Project } from './api'

export type EffectiveMode = 'unlimited' | 'finite'
export type CashMode = 'none' | 'capped'

export interface BudgetChoice {
  effectiveMode: EffectiveMode
  effectiveAmount: string
  cashMode: CashMode
  cashAmount: string
}

export function initialBudgetChoice(project?: Project): BudgetChoice {
  return {
    effectiveMode: !project || project.no_effective_limit || project.effective_budget_nanos === 0 ? 'unlimited' : 'finite',
    effectiveAmount: project?.effective_budget_nanos ? String(project.effective_budget_nanos / 1e9) : '5',
    cashMode: project?.cash_ceiling_nanos == null ? 'none' : 'capped',
    cashAmount: project?.cash_ceiling_nanos == null ? '0' : String(project.cash_ceiling_nanos / 1e9),
  }
}

function amount(text: string, positive: boolean): number {
  const n = Number(text)
  if (!text.trim() || !Number.isFinite(n) || n < 0 || (positive && n <= 0) || (positive && Math.round(n * 1e9) === 0)) {
    throw new Error(positive ? 'Enter a positive effective reference-cost limit.' : 'Enter a nonnegative cash ceiling (0 is allowed).')
  }
  return n
}

export function budgetPayload(choice: BudgetChoice, project?: Project): Record<string, boolean | number> {
  const body: Record<string, boolean | number> = {}
  const previous = project ? initialBudgetChoice(project) : undefined
  if (!project || choice.effectiveMode !== previous?.effectiveMode || (choice.effectiveMode === 'finite' && amount(choice.effectiveAmount, true) !== project.effective_budget_nanos / 1e9)) {
    if (choice.effectiveMode === 'unlimited') body.no_effective_limit = true
    else { body.no_effective_limit = false; body.budget_dollars = amount(choice.effectiveAmount, true) }
  }
  if (choice.cashMode === 'capped') {
    const n = amount(choice.cashAmount, false)
    if (!project || previous?.cashMode !== 'capped' || n !== project.cash_ceiling_nanos! / 1e9) body.cash_ceiling_dollars = n
  } else if (project && previous?.cashMode === 'capped') body.clear_cash_ceiling = true
  return body
}

export function BudgetFields({ value, onChange }: { value: BudgetChoice; onChange: (value: BudgetChoice) => void }) {
  const set = (patch: Partial<BudgetChoice>) => onChange({ ...value, ...patch })
  return (
    <fieldset className="budget-fields">
      <legend>Project cost controls</legend>
      <div className="small muted">Effective dollars are reference valuations for accounting, not cash payments.</div>
      <div className="budget-choice" role="group" aria-label="Effective reference-cost limit">
        <label><input type="radio" name="effective-mode" checked={value.effectiveMode === 'unlimited'} onChange={() => set({ effectiveMode: 'unlimited' })} /> No effective limit</label>
        <label><input type="radio" name="effective-mode" checked={value.effectiveMode === 'finite'} onChange={() => set({ effectiveMode: 'finite' })} /> Finite effective limit</label>
        {value.effectiveMode === 'finite' && <label>Effective reference-cost limit (USD)<input type="number" min="0.000000001" step="any" required value={value.effectiveAmount} onChange={(e) => set({ effectiveAmount: e.target.value })} /></label>}
      </div>
      <div className="budget-choice" role="group" aria-label="Cash admission ceiling">
        <label><input type="radio" name="cash-mode" checked={value.cashMode === 'none'} onChange={() => set({ cashMode: 'none' })} /> No cash ceiling</label>
        <label><input type="radio" name="cash-mode" checked={value.cashMode === 'capped'} onChange={() => set({ cashMode: 'capped' })} /> Set cash ceiling</label>
        {value.cashMode === 'capped' && <label>Cash ceiling (USD; 0 is a $0 ceiling)<input type="number" min="0" step="any" required value={value.cashAmount} onChange={(e) => set({ cashAmount: e.target.value })} /></label>}
      </div>
      <div className="small muted">A cash ceiling controls admission using estimates, not a guaranteed bank balance. Pending requests can settle above an estimate. Unknown-price cash requests cannot be admitted under a ceiling.</div>
    </fieldset>
  )
}
