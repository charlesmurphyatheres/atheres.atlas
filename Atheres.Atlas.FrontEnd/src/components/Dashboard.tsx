import { useEffect, useState } from 'react'
import { getOrders, getRoutes } from '../services/apiService'
import type { Notification, Order, Route } from '../types'
import { format } from 'date-fns'

interface Props {
  notifications: Notification[]
}

interface Stats {
  total: number
  ordered: number
  scheduled: number
  confirmed: number
}

export default function Dashboard({ notifications }: Props) {
  const [stats, setStats] = useState<Stats>({ total: 0, ordered: 0, scheduled: 0, confirmed: 0 })
  const [todayRoutes, setTodayRoutes] = useState<Route[]>([])
  const [recentOrders, setRecentOrders] = useState<Order[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    async function load() {
      try {
        const [ordersResult, routes] = await Promise.all([
          getOrders({ pageSize: 10 }),
          getRoutes(format(new Date(), 'yyyy-MM-dd')),
        ])
        setRecentOrders(ordersResult.items)
        setTodayRoutes(routes)
        setStats({
          total: ordersResult.total,
          ordered: ordersResult.items.filter((o) => o.status === 'Ordered').length,
          scheduled: ordersResult.items.filter((o) => o.status === 'Scheduled' || o.status === 'RouteOptimized').length,
          confirmed: ordersResult.items.filter((o) => o.status === 'Confirmed').length,
        })
      } finally {
        setLoading(false)
      }
    }
    load()
  }, [])

  if (loading) return <div className="flex items-center justify-center h-64 text-gray-400">Loading...</div>

  return (
    <div className="space-y-6">
      <h1 className="text-2xl font-bold text-gray-900">Dashboard</h1>

      {/* Stats */}
      <div className="grid grid-cols-2 lg:grid-cols-4 gap-4">
        {[
          { label: 'Total Orders', value: stats.total, color: 'bg-blue-50 text-blue-700' },
          { label: 'Ordered', value: stats.ordered, color: 'bg-indigo-50 text-indigo-700' },
          { label: 'Scheduled', value: stats.scheduled, color: 'bg-yellow-50 text-yellow-700' },
          { label: 'Confirmed', value: stats.confirmed, color: 'bg-green-50 text-green-700' },
        ].map((stat) => (
          <div key={stat.label} className={`rounded-xl p-5 ${stat.color}`}>
            <p className="text-sm font-medium opacity-75">{stat.label}</p>
            <p className="text-3xl font-bold mt-1">{stat.value}</p>
          </div>
        ))}
      </div>

      <div className="grid lg:grid-cols-2 gap-6">
        {/* Today's routes */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <h2 className="font-semibold text-gray-800 mb-3">Today's Routes</h2>
          {todayRoutes.length === 0 ? (
            <p className="text-sm text-gray-400">No routes for today.</p>
          ) : (
            <ul className="space-y-2">
              {todayRoutes.map((r) => (
                <li key={r.id} className="flex justify-between text-sm">
                  <span className="text-gray-700">{r.totalStops} stops</span>
                  <span className="text-gray-500">{r.totalDistanceMiles.toFixed(1)} mi · {r.totalDuration}</span>
                </li>
              ))}
            </ul>
          )}
        </div>

        {/* Recent activity */}
        <div className="bg-white rounded-xl border border-gray-200 p-5">
          <h2 className="font-semibold text-gray-800 mb-3">Recent Activity</h2>
          {notifications.length === 0 ? (
            <p className="text-sm text-gray-400">No recent activity.</p>
          ) : (
            <ul className="space-y-2">
              {notifications.slice(0, 6).map((n) => (
                <li key={n.id} className="flex gap-2 text-sm">
                  <span className="text-gray-400 shrink-0">{format(new Date(n.occurredAt), 'HH:mm')}</span>
                  <span className="text-gray-700">{n.title}: {n.body}</span>
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>

      {/* Recent orders table */}
      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="px-5 py-4 border-b border-gray-100">
          <h2 className="font-semibold text-gray-800">Recent Orders</h2>
        </div>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
              <tr>
                {['Store', 'City', 'District/Zone', 'Expected Delivery', 'Status'].map((h) => (
                  <th key={h} className="px-4 py-3 text-left font-medium">{h}</th>
                ))}
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {recentOrders.map((o) => (
                <tr key={o.id} className="hover:bg-gray-50">
                  <td className="px-4 py-3 font-medium text-gray-900">{o.storeName}</td>
                  <td className="px-4 py-3 text-gray-600">{o.city}, {o.state}</td>
                  <td className="px-4 py-3 text-gray-500">{o.district} / {o.zone}</td>
                  <td className="px-4 py-3 text-gray-600">
                    {o.expectedDeliveryDate
                      ? format(new Date(o.expectedDeliveryDate), 'MMM dd HH:mm')
                      : '—'}
                  </td>
                  <td className="px-4 py-3">
                    <StatusBadge status={o.status} />
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
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
    <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] ?? 'bg-gray-100 text-gray-500'}`}>
      {status.replace(/([A-Z])/g, ' $1').trim()}
    </span>
  )
}
