import { useEffect, useMemo, useState } from 'react'
import { bulkUpdateOrderStatus, getOrders, updateOrderStatus } from '../services/apiService'
import type { Order, OrderStatus } from '../types'
import { format } from 'date-fns'
import { useSortedRows } from '../hooks/useSortedRows'
import { SortHeader } from './ui/SortHeader'
import BulkStatusBar, { ALL_STATUSES } from './ui/BulkStatusBar'
import { summarizeBulkStatusResult } from './ui/bulkStatusSummary'

const STATUS_OPTIONS: { value: OrderStatus | ''; label: string }[] = [
  { value: '', label: 'All Statuses' },
  { value: 'Ordered', label: 'Ordered' },
  { value: 'Scheduled', label: 'Scheduled' },
  { value: 'RouteOptimized', label: 'Route Optimized' },
  { value: 'RouteOmitted', label: 'Route Omitted' },
  { value: 'ConfirmationPending', label: 'Awaiting Confirmation' },
  { value: 'Confirmed', label: 'Confirmed' },
  { value: 'Rejected', label: 'Rejected' },
  { value: 'OutForDelivery', label: 'Out for Delivery' },
  { value: 'Delivered', label: 'Delivered' },
  { value: 'Archived', label: 'Archived' },
]

export default function OrderList() {
  const [orders, setOrders] = useState<Order[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [statusFilter, setStatusFilter] = useState<OrderStatus | ''>('')
  const [loading, setLoading] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const pageSize = 20
  const { sorted: sortedOrders, sortKey, sortDir, toggle } = useSortedRows(orders)

  useEffect(() => {
    setLoading(true)
    setSelected(new Set())
    getOrders({ status: statusFilter || undefined, page, pageSize })
      .then((r) => { setOrders(r.items); setTotal(r.total) })
      .finally(() => setLoading(false))
  }, [statusFilter, page])

  const totalPages = Math.ceil(total / pageSize)
  const allSelected = useMemo(
    () => sortedOrders.length > 0 && sortedOrders.every((o) => selected.has(o.id)),
    [sortedOrders, selected],
  )

  function toggleOne(id: string) {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  function toggleAll() {
    setSelected(allSelected ? new Set() : new Set(sortedOrders.map((o) => o.id)))
  }

  // Optimistic per-row status change — flip the cached row, then PATCH. On
  // failure we revert and surface the error so the operator can retry.
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
    // Optimistic update for fast feedback; revert any rows the server
    // reported as failed.
    setOrders((prev) => prev.map((o) => (selected.has(o.id) ? { ...o, status: next } : o)))
    try {
      const result = await bulkUpdateOrderStatus(ids, target)

      // Reconcile rewrites: rows the server rewrote to RouteOmitted have
      // ok=true but rewrittenAsStale=true, so the client cache must reflect
      // RouteOmitted instead of the requested status.
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
      // Whole-call failure — revert everything.
      setOrders((prev) => prev.map((o) => before.has(o.id)
        ? { ...o, status: before.get(o.id) as OrderStatus }
        : o))
      alert('Bulk status update failed.')
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-gray-900">Orders</h1>
        <div className="flex items-center gap-2">
          <select
            value={statusFilter}
            onChange={(e) => { setStatusFilter(e.target.value as OrderStatus | ''); setPage(1) }}
            className="border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand-500"
          >
            {STATUS_OPTIONS.map((o) => (
              <option key={o.value} value={o.value}>{o.label}</option>
            ))}
          </select>
          <span className="text-sm text-gray-500">{total} orders</span>
        </div>
      </div>

      <BulkStatusBar
        selectedCount={selected.size}
        totalCount={sortedOrders.length}
        onClearSelection={() => setSelected(new Set())}
        onApply={handleBulkApply}
      />

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
              <tr>
                <th className="px-3 py-3 text-left font-medium w-10">
                  <input
                    type="checkbox"
                    checked={allSelected}
                    onChange={toggleAll}
                    className="rounded border-gray-300 text-brand-500 focus:ring-brand-400 cursor-pointer"
                    title="Select all on this page"
                  />
                </th>
                <SortHeader label="Store"             sortKey="storeName"            activeKey={sortKey} dir={sortDir} onClick={() => toggle('storeName')} />
                <SortHeader label="Address"           sortKey="city"                 activeKey={sortKey} dir={sortDir} onClick={() => toggle('city')} />
                <SortHeader label="District"          sortKey="district"             activeKey={sortKey} dir={sortDir} onClick={() => toggle('district')} />
                <SortHeader label="Zone"              sortKey="zone"                 activeKey={sortKey} dir={sortDir} onClick={() => toggle('zone')} />
                <SortHeader label="Order Date"        sortKey="orderDate"            activeKey={sortKey} dir={sortDir} onClick={() => toggle('orderDate')} />
                <SortHeader label="Expected Delivery" sortKey="expectedDeliveryDate" activeKey={sortKey} dir={sortDir} onClick={() => toggle('expectedDeliveryDate')} />
                <SortHeader label="Confirmation"      sortKey="confirmationDeadline" activeKey={sortKey} dir={sortDir} onClick={() => toggle('confirmationDeadline')} />
                <SortHeader label="Status"            sortKey="status"               activeKey={sortKey} dir={sortDir} onClick={() => toggle('status')} />
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {loading ? (
                <tr>
                  <td colSpan={9} className="px-4 py-8 text-center text-gray-400">Loading...</td>
                </tr>
              ) : sortedOrders.length === 0 ? (
                <tr>
                  <td colSpan={9} className="px-4 py-8 text-center text-gray-400">No orders found.</td>
                </tr>
              ) : (
                sortedOrders.map((o) => (
                  <tr key={o.id} className={`hover:bg-gray-50 ${selected.has(o.id) ? 'bg-brand-50/40' : ''}`}>
                    <td className="px-3 py-3">
                      <input
                        type="checkbox"
                        checked={selected.has(o.id)}
                        onChange={() => toggleOne(o.id)}
                        className="rounded border-gray-300 text-brand-500 focus:ring-brand-400 cursor-pointer"
                      />
                    </td>
                    <td className="px-4 py-3 font-medium text-gray-900 whitespace-nowrap">{o.storeName}</td>
                    <td className="px-4 py-3 text-gray-600 whitespace-nowrap">{o.city}, {o.state}</td>
                    <td className="px-4 py-3 text-gray-500">{o.district}</td>
                    <td className="px-4 py-3 text-gray-500">{o.zone}</td>
                    <td className="px-4 py-3 text-gray-500 whitespace-nowrap">
                      {format(new Date(o.orderDate), 'MMM dd, yyyy')}
                    </td>
                    <td className="px-4 py-3 text-gray-600 whitespace-nowrap">
                      {o.expectedDeliveryDate
                        ? format(new Date(o.expectedDeliveryDate), 'MMM dd HH:mm')
                        : '—'}
                    </td>
                    <td className="px-4 py-3 text-gray-500 whitespace-nowrap">
                      {o.confirmationDeadline
                        ? `by ${format(new Date(o.confirmationDeadline), 'HH:mm')}`
                        : '—'}
                    </td>
                    <td className="px-4 py-3 whitespace-nowrap">
                      <StatusSelect
                        value={o.status}
                        onChange={(next) => handleRowStatusChange(o.id, next)}
                      />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>

        {/* Pagination */}
        {totalPages > 1 && (
          <div className="flex items-center justify-between px-4 py-3 border-t border-gray-100">
            <button
              onClick={() => setPage((p) => Math.max(1, p - 1))}
              disabled={page === 1}
              className="px-3 py-1 text-sm border border-gray-300 rounded-md disabled:opacity-40 hover:bg-gray-50"
            >
              Previous
            </button>
            <span className="text-sm text-gray-600">Page {page} of {totalPages}</span>
            <button
              onClick={() => setPage((p) => Math.min(totalPages, p + 1))}
              disabled={page === totalPages}
              className="px-3 py-1 text-sm border border-gray-300 rounded-md disabled:opacity-40 hover:bg-gray-50"
            >
              Next
            </button>
          </div>
        )}
      </div>
    </div>
  )
}

const STATUS_BADGE_STYLES: Record<string, string> = {
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

function StatusSelect({ value, onChange }: { value: OrderStatus; onChange: (v: OrderStatus) => void }) {
  const tone = STATUS_BADGE_STYLES[value] ?? 'bg-gray-50 text-gray-600 border-gray-200'
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
