import { useEffect, useState } from 'react'
import { getOrders } from '../services/apiService'
import type { Order, OrderStatus } from '../types'
import { format } from 'date-fns'
import { useSortedRows } from '../hooks/useSortedRows'
import { SortHeader } from './ui/SortHeader'

const STATUS_OPTIONS: { value: OrderStatus | ''; label: string }[] = [
  { value: '', label: 'All Statuses' },
  { value: 'Ordered', label: 'Ordered' },
  { value: 'Scheduled', label: 'Scheduled' },
  { value: 'RouteOptimized', label: 'Route Optimized' },
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
  const pageSize = 20
  const { sorted: sortedOrders, sortKey, sortDir, toggle } = useSortedRows(orders)

  useEffect(() => {
    setLoading(true)
    getOrders({ status: statusFilter || undefined, page, pageSize })
      .then((r) => { setOrders(r.items); setTotal(r.total) })
      .finally(() => setLoading(false))
  }, [statusFilter, page])

  const totalPages = Math.ceil(total / pageSize)

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

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
              <tr>
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
                  <td colSpan={8} className="px-4 py-8 text-center text-gray-400">Loading...</td>
                </tr>
              ) : sortedOrders.length === 0 ? (
                <tr>
                  <td colSpan={8} className="px-4 py-8 text-center text-gray-400">No orders found.</td>
                </tr>
              ) : (
                sortedOrders.map((o) => (
                  <tr key={o.id} className="hover:bg-gray-50">
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
                    <td className="px-4 py-3">
                      <StatusBadge status={o.status} />
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

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    Ordered: 'bg-indigo-100 text-indigo-700',
    Scheduled: 'bg-blue-100 text-blue-700',
    RouteOptimized: 'bg-cyan-100 text-cyan-700',
    ConfirmationPending: 'bg-yellow-100 text-yellow-700',
    Confirmed: 'bg-green-100 text-green-700',
    Rejected: 'bg-red-100 text-red-700',
    OutForDelivery: 'bg-orange-100 text-orange-700',
    Delivered: 'bg-gray-100 text-gray-600',
    Archived: 'bg-gray-50 text-gray-400',
    Cancelled: 'bg-red-50 text-red-500',
  }
  return (
    <span className={`px-2 py-0.5 rounded-full text-xs font-medium whitespace-nowrap ${styles[status] ?? 'bg-gray-100 text-gray-500'}`}>
      {status.replace(/([A-Z])/g, ' $1').trim()}
    </span>
  )
}
