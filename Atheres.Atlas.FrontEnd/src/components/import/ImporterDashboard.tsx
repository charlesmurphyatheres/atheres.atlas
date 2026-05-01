import { useMemo, useEffect, useState } from 'react'
import { format } from 'date-fns'
import { useAuth } from '../../contexts/AuthContext'
import { bulkUpdateOrderStatus, getOrders, updateOrderStatus } from '../../services/apiService'
import type { Order, OrderStatus } from '../../types'
import OrderImportPanel from './OrderImportPanel'
import ManualOrderEntry from './ManualOrderEntry'
import BulkStatusBar, { ALL_STATUSES } from '../ui/BulkStatusBar'
import { summarizeBulkStatusResult } from '../ui/bulkStatusSummary'

type Tab = 'past' | 'import' | 'enter'

/**
 * Landing page for the OrderImporter role. Two views:
 *   1. Past Orders — every order in the user's company (server-scoped via
 *      the JWT companyId claim).
 *   2. Import Orders — the existing CSV import workflow, scoped on the
 *      backend to the importer's pinned warehouse.
 */
export default function ImporterDashboard() {
  const { user } = useAuth()
  const [tab, setTab] = useState<Tab>('past')

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Order Data</h1>
        <p className="text-sm text-gray-500 mt-0.5">
          {user?.companyName ? <span><strong>{user.companyName}</strong></span> : null}
          {user?.warehouseName ? <span> · Warehouse: <strong>{user.warehouseName}</strong></span> : null}
        </p>
      </div>

      <div className="flex gap-1 border-b border-gray-200">
        {(['past', 'import', 'enter'] as Tab[]).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm font-medium border-b-2 -mb-px transition-colors ${
              tab === t
                ? 'border-brand-500 text-brand-600'
                : 'border-transparent text-gray-500 hover:text-gray-700'
            }`}
          >
            {t === 'past' ? 'Past Orders' : t === 'import' ? 'Import Orders' : 'Enter Orders'}
          </button>
        ))}
      </div>

      {tab === 'past'   && <PastOrders />}
      {tab === 'import' && <OrderImportPanel />}
      {tab === 'enter'  && <ManualOrderEntry />}
    </div>
  )
}

// ---- Past Orders -----------------------------------------------------------

function PastOrders() {
  const [orders, setOrders] = useState<Order[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError]     = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())

  useEffect(() => {
    setLoading(true)
    getOrders({ pageSize: 200 })
      .then((res) => { setOrders(res.items); setSelected(new Set()) })
      .catch(() => setError('Failed to load orders.'))
      .finally(() => setLoading(false))
  }, [])

  const allSelected = useMemo(
    () => orders.length > 0 && orders.every((o) => selected.has(o.id)),
    [orders, selected],
  )

  function toggleOne(id: string) {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  function toggleAll() {
    setSelected(allSelected ? new Set() : new Set(orders.map((o) => o.id)))
  }

  // Optimistic per-row status change. Reverts on failure so a transient
  // backend hiccup doesn't leave the grid showing stale state.
  async function handleRowStatusChange(orderId: string, next: OrderStatus) {
    const previous = orders.find((o) => o.id === orderId)?.status
    if (!previous || previous === next) return
    setOrders((prev) => prev.map((o) => (o.id === orderId ? { ...o, status: next } : o)))
    try {
      await updateOrderStatus(orderId, next)
    } catch {
      setOrders((prev) => prev.map((o) => (o.id === orderId ? { ...o, status: previous } : o)))
      alert('Failed to update order status.')
    }
  }

  async function handleBulkApply(target: string) {
    const ids = Array.from(selected)
    if (ids.length === 0 || !target) return
    const next = target as OrderStatus
    const before = new Map(orders.map((o) => [o.id, o.status]))
    setOrders((prev) => prev.map((o) => (selected.has(o.id) ? { ...o, status: next } : o)))
    try {
      const result = await bulkUpdateOrderStatus(ids, target)

      const rewrittenStale = new Set(
        result.results.filter((r) => r.ok && r.rewrittenAsStale).map((r) => r.orderId),
      )
      if (rewrittenStale.size > 0) {
        setOrders((prev) => prev.map((o) =>
          rewrittenStale.has(o.id) ? { ...o, status: 'RouteOmitted' as OrderStatus } : o,
        ))
      }

      const failed = result.results.filter((r) => !r.ok).map((r) => r.orderId)
      if (failed.length > 0) {
        setOrders((prev) => prev.map((o) => failed.includes(o.id) && before.has(o.id)
          ? { ...o, status: before.get(o.id) as OrderStatus }
          : o))
        alert(`${failed.length} order${failed.length === 1 ? '' : 's'} could not be updated.`)
      }

      const summary = summarizeBulkStatusResult(result, target)
      if (summary) alert(summary)

      setSelected(new Set())
    } catch {
      setOrders((prev) => prev.map((o) => before.has(o.id)
        ? { ...o, status: before.get(o.id) as OrderStatus }
        : o))
      alert('Bulk status update failed.')
    }
  }

  if (loading) return <div className="flex items-center justify-center h-40 text-gray-400">Loading…</div>
  if (error)   return <div className="bg-red-50 border border-red-200 rounded-xl p-3 text-sm text-red-700">{error}</div>

  return (
    <div className="space-y-3">
      <BulkStatusBar
        selectedCount={selected.size}
        totalCount={orders.length}
        onClearSelection={() => setSelected(new Set())}
        onApply={handleBulkApply}
      />

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="px-5 py-3 border-b border-gray-100 flex items-center justify-between">
          <h2 className="text-sm font-semibold text-gray-800">Past Orders</h2>
          <p className="text-xs text-gray-500">{orders.length} order{orders.length === 1 ? '' : 's'}</p>
        </div>
        <div className="overflow-x-auto max-h-[36rem]">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide sticky top-0">
              <tr>
                <th className="px-3 py-2 text-left font-medium w-10">
                  <input
                    type="checkbox"
                    checked={allSelected}
                    onChange={toggleAll}
                    className="rounded border-gray-300 text-brand-500 focus:ring-brand-400 cursor-pointer"
                    title="Select all"
                  />
                </th>
                <th className="px-4 py-2 text-left font-medium">Order Date</th>
                <th className="px-4 py-2 text-left font-medium">Store</th>
                <th className="px-4 py-2 text-left font-medium">License #</th>
                <th className="px-4 py-2 text-left font-medium">City</th>
                <th className="px-4 py-2 text-left font-medium">Status</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {orders.map((o) => (
                <tr key={o.id} className={`hover:bg-gray-50 ${selected.has(o.id) ? 'bg-brand-50/40' : ''}`}>
                  <td className="px-3 py-2">
                    <input
                      type="checkbox"
                      checked={selected.has(o.id)}
                      onChange={() => toggleOne(o.id)}
                      className="rounded border-gray-300 text-brand-500 focus:ring-brand-400 cursor-pointer"
                    />
                  </td>
                  <td className="px-4 py-2 text-gray-500 whitespace-nowrap">{format(new Date(o.orderDate), 'MMM dd, yyyy')}</td>
                  <td className="px-4 py-2 font-medium text-gray-900">{o.storeName}</td>
                  <td className="px-4 py-2 text-gray-500 font-mono text-xs">{o.storeLicenseNumber || o.licenseNumber || '—'}</td>
                  <td className="px-4 py-2 text-gray-600">{o.city}</td>
                  <td className="px-4 py-2 whitespace-nowrap">
                    <RowStatusSelect
                      value={o.status}
                      onChange={(next) => handleRowStatusChange(o.id, next)}
                    />
                  </td>
                </tr>
              ))}
              {orders.length === 0 && (
                <tr><td colSpan={6} className="px-4 py-8 text-center text-gray-400">No orders yet. Use the Import Orders tab to upload a CSV.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  )
}

const STATUS_TONE: Record<string, string> = {
  Ordered: 'bg-indigo-50 text-indigo-700 border-indigo-200',
  Scheduled: 'bg-blue-50 text-blue-700 border-blue-200',
  RouteOptimized: 'bg-cyan-50 text-cyan-700 border-cyan-200',
  RouteOmitted: 'bg-stone-100 text-stone-600 border-stone-300',
  ConfirmationPending: 'bg-yellow-50 text-yellow-700 border-yellow-200',
  Confirmed: 'bg-green-50 text-green-700 border-green-200',
  Rejected: 'bg-red-50 text-red-700 border-red-200',
  OutForDelivery: 'bg-orange-50 text-orange-700 border-orange-200',
  Delivered: 'bg-gray-50 text-gray-600 border-gray-200',
  Archived: 'bg-gray-50 text-gray-400 border-gray-200',
  Cancelled: 'bg-red-50 text-red-500 border-red-200',
}

function RowStatusSelect({ value, onChange }: { value: OrderStatus; onChange: (v: OrderStatus) => void }) {
  const tone = STATUS_TONE[value] ?? 'bg-gray-50 text-gray-600 border-gray-200'
  return (
    <select
      value={value}
      onChange={(e) => {
        const next = e.target.value as OrderStatus
        if (next !== value) onChange(next)
      }}
      className={`text-xs font-medium px-2 py-1 rounded-md border focus:outline-none focus:ring-2 focus:ring-brand-300 cursor-pointer ${tone}`}
    >
      {ALL_STATUSES.map((s) => (
        <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>
      ))}
    </select>
  )
}
