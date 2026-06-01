import { useState } from 'react'

export const ALL_STATUSES = [
  'Ordered',
  'Scheduled',
  'RouteOptimized',
  'ConfirmationPending',
  'Confirmed',
  'Rejected',
  'OutForDelivery',
  'Delivered',
  'Archived',
  'Cancelled',
  'RouteOmitted',
] as const

/**
 * Compact bar shown above an order grid when one or more rows are selected.
 * Renders a status dropdown + "Apply" button that calls back into the parent
 * with the chosen status; the parent owns the network call (so it can pick
 * between optimistic update / refetch on its own).
 */
export default function BulkStatusBar({
  selectedCount,
  totalCount,
  onClearSelection,
  onApply,
  disabled = false,
}: {
  selectedCount: number
  totalCount: number
  onClearSelection: () => void
  onApply: (status: string) => Promise<void> | void
  disabled?: boolean
}) {
  const [target, setTarget] = useState<string>('')
  const [busy, setBusy]     = useState(false)

  if (selectedCount === 0) return null

  async function apply() {
    if (!target) return
    setBusy(true)
    try {
      await onApply(target)
      setTarget('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="bg-brand-50 border border-brand-200 rounded-xl px-4 py-2 flex flex-wrap items-center gap-3">
      <span className="text-sm text-brand-800">
        <strong>{selectedCount}</strong> of {totalCount} selected
      </span>
      <button
        onClick={onClearSelection}
        className="text-xs text-brand-700 hover:underline"
      >
        Clear
      </button>
      <span className="text-gray-300">·</span>
      <label className="text-xs text-gray-600">Set status to</label>
      <select
        value={target}
        onChange={(e) => setTarget(e.target.value)}
        disabled={disabled || busy}
        className="text-sm border border-gray-300 rounded-md px-2 py-1 focus:outline-none focus:ring-2 focus:ring-brand-400"
      >
        <option value="">— choose —</option>
        {ALL_STATUSES.map((s) => (
          <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>
        ))}
      </select>
      <button
        onClick={apply}
        disabled={disabled || busy || !target}
        className="px-3 py-1 text-xs font-medium bg-brand-500 text-white rounded-md hover:bg-brand-600 disabled:opacity-50"
      >
        {busy ? 'Applying…' : `Apply to ${selectedCount}`}
      </button>
    </div>
  )
}
