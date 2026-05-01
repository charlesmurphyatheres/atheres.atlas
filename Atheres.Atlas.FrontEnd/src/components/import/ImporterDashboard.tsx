import { useEffect, useState } from 'react'
import { format } from 'date-fns'
import { useAuth } from '../../contexts/AuthContext'
import { getOrders } from '../../services/apiService'
import type { Order } from '../../types'
import OrderImportPanel from './OrderImportPanel'

type Tab = 'past' | 'import'

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
        {(['past', 'import'] as Tab[]).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm font-medium border-b-2 -mb-px transition-colors ${
              tab === t
                ? 'border-brand-500 text-brand-600'
                : 'border-transparent text-gray-500 hover:text-gray-700'
            }`}
          >
            {t === 'past' ? 'Past Orders' : 'Import Orders'}
          </button>
        ))}
      </div>

      {tab === 'past'   && <PastOrders />}
      {tab === 'import' && <OrderImportPanel />}
    </div>
  )
}

// ---- Past Orders -----------------------------------------------------------

function PastOrders() {
  const [orders, setOrders] = useState<Order[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError]     = useState('')

  useEffect(() => {
    setLoading(true)
    getOrders({ pageSize: 200 })
      .then((res) => setOrders(res.items))
      .catch(() => setError('Failed to load orders.'))
      .finally(() => setLoading(false))
  }, [])

  if (loading) return <div className="flex items-center justify-center h-40 text-gray-400">Loading…</div>
  if (error)   return <div className="bg-red-50 border border-red-200 rounded-xl p-3 text-sm text-red-700">{error}</div>

  return (
    <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
      <div className="px-5 py-3 border-b border-gray-100 flex items-center justify-between">
        <h2 className="text-sm font-semibold text-gray-800">Past Orders</h2>
        <p className="text-xs text-gray-500">{orders.length} order{orders.length === 1 ? '' : 's'}</p>
      </div>
      <div className="overflow-x-auto max-h-[36rem]">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide sticky top-0">
            <tr>
              <th className="px-4 py-2 text-left font-medium">Order Date</th>
              <th className="px-4 py-2 text-left font-medium">Store</th>
              <th className="px-4 py-2 text-left font-medium">License #</th>
              <th className="px-4 py-2 text-left font-medium">City</th>
              <th className="px-4 py-2 text-left font-medium">Status</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {orders.map((o) => (
              <tr key={o.id} className="hover:bg-gray-50">
                <td className="px-4 py-2 text-gray-500 whitespace-nowrap">{format(new Date(o.orderDate), 'MMM dd, yyyy')}</td>
                <td className="px-4 py-2 font-medium text-gray-900">{o.storeName}</td>
                <td className="px-4 py-2 text-gray-500 font-mono text-xs">{o.storeLicenseNumber ?? '—'}</td>
                <td className="px-4 py-2 text-gray-600">{o.city}</td>
                <td className="px-4 py-2">
                  <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-700">
                    {o.status.replace(/([A-Z])/g, ' $1').trim()}
                  </span>
                </td>
              </tr>
            ))}
            {orders.length === 0 && (
              <tr><td colSpan={5} className="px-4 py-8 text-center text-gray-400">No orders yet. Use the Import Orders tab to upload a CSV.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}
