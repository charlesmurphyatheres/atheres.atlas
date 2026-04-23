import { useEffect, useState } from 'react'
import { getRoutes, getTrucks, confirmDeliveryManual, reorderRouteStop } from '../../services/apiService'
import type { Route, RouteStop, Truck } from '../../types'
import { format, addDays, subDays } from 'date-fns'

export default function LogisticsPanel() {
  const [date, setDate] = useState(format(new Date(), 'yyyy-MM-dd'))
  const [routes, setRoutes] = useState<Route[]>([])
  const [trucks, setTrucks] = useState<Truck[]>([])
  const [loading, setLoading] = useState(true)
  const [selectedRoute, setSelectedRoute] = useState<Route | null>(null)

  useEffect(() => {
    Promise.all([
      getRoutes(date),
      getTrucks(),
    ])
      .then(([r, t]) => { setRoutes(r); setTrucks(t) })
      .finally(() => setLoading(false))
  }, [date])

  function shiftDate(days: number) {
    const d = new Date(date)
    setDate(format(days > 0 ? addDays(d, days) : subDays(d, -days), 'yyyy-MM-dd'))
  }

  return (
    <div className="space-y-4">
      {/* Header */}
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Logistics</h1>
        <p className="text-sm text-gray-500 mt-0.5">
          Multi-truck delivery schedule. Routes are generated automatically when
          orders are marked ready to pickup.
        </p>
      </div>

      {/* Date navigation */}
      <div className="flex items-center gap-3 bg-white rounded-xl border border-gray-200 px-4 py-3">
        <button onClick={() => shiftDate(-1)} className="p-1.5 rounded hover:bg-gray-100 text-gray-500">
          ‹
        </button>
        <input
          type="date"
          value={date}
          onChange={(e) => setDate(e.target.value)}
          className="text-sm font-medium text-gray-800 border-none focus:outline-none"
        />
        <button onClick={() => shiftDate(1)} className="p-1.5 rounded hover:bg-gray-100 text-gray-500">
          ›
        </button>
        <span className="text-xs text-gray-400 ml-2">
          {routes.length} route{routes.length !== 1 ? 's' : ''} · {trucks.length} truck{trucks.length !== 1 ? 's' : ''}
        </span>
      </div>

      {loading ? (
        <div className="flex items-center justify-center h-64 text-gray-400">Loading…</div>
      ) : (
        <div className="grid lg:grid-cols-3 gap-4">
          {/* Route list (left column) */}
          <div className="space-y-3">
            <h2 className="text-sm font-semibold text-gray-600 uppercase tracking-wide">Routes</h2>
            {routes.length === 0 ? (
              <div className="bg-white rounded-xl border border-gray-200 p-6 text-center text-gray-400 text-sm">
                No routes for this date.
              </div>
            ) : (
              routes.map((route) => (
                <RouteCard
                  key={route.id}
                  route={route}

                  selected={selectedRoute?.id === route.id}
                  onSelect={() => setSelectedRoute(route)}
                />
              ))
            )}
          </div>

          {/* Stop detail (right columns) */}
          <div className="lg:col-span-2">
            {selectedRoute ? (
              <StopEditor
                route={selectedRoute}
                onUpdate={(updated) => {
                  setRoutes((prev) => prev.map((r) => r.id === updated.id ? updated : r))
                  setSelectedRoute(updated)
                }}
              />
            ) : (
              <div className="bg-white rounded-xl border border-gray-200 flex items-center justify-center h-64 text-gray-400 text-sm">
                Select a route to view and edit stops.
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}

// ---- Route Card ----

function RouteCard({ route, selected, onSelect }: {
  route: Route
  selected: boolean
  onSelect: () => void
}) {
  const confirmedCount = route.stops.filter((s) => s.confirmationStatus === 'Confirmed').length
  const pendingCount = route.stops.filter((s) => s.confirmationStatus === 'Pending' || s.confirmationStatus === 'SentEmail').length

  return (
    <button
      onClick={onSelect}
      className={`w-full text-left bg-white rounded-xl border p-4 transition-colors ${
        selected ? 'border-brand-400 ring-1 ring-brand-300' : 'border-gray-200 hover:border-gray-300'
      }`}
    >
      <div className="flex items-start justify-between">
        <div>
          <p className="font-semibold text-gray-900">{route.totalStops} stops</p>
          <p className="text-xs text-gray-500 mt-0.5">{route.totalDistanceMiles.toFixed(1)} mi · {route.totalDuration}</p>
        </div>
        {route.isOptimized && (
          <span className="text-xs bg-green-50 text-green-600 font-medium px-2 py-0.5 rounded-full">Optimized</span>
        )}
      </div>
      <div className="mt-3 flex gap-3 text-xs">
        <span className="text-green-600">{confirmedCount} confirmed</span>
        <span className="text-yellow-600">{pendingCount} pending</span>
      </div>
    </button>
  )
}

// ---- Stop Editor ----

function StopEditor({ route, onUpdate }: { route: Route; onUpdate: (r: Route) => void }) {
  const [stops, setStops] = useState<RouteStop[]>([...route.stops].sort((a, b) => a.sequence - b.sequence))
  const [saving, setSaving] = useState<string | null>(null)

  async function handleConfirm(stop: RouteStop) {
    setSaving(stop.orderId)
    try {
      await confirmDeliveryManual(stop.orderId)
      setStops((prev) =>
        prev.map((s) => s.orderId === stop.orderId ? { ...s, confirmationStatus: 'Confirmed', orderStatus: 'Confirmed' } : s)
      )
      onUpdate({ ...route, stops: stops.map((s) => s.orderId === stop.orderId ? { ...s, confirmationStatus: 'Confirmed', orderStatus: 'Confirmed' } : s) })
    } finally {
      setSaving(null)
    }
  }

  async function moveStop(index: number, direction: -1 | 1) {
    const newStops = [...stops]
    const swapIndex = index + direction
    if (swapIndex < 0 || swapIndex >= newStops.length) return

    const [a, b] = [newStops[index], newStops[swapIndex]]
    newStops[index] = { ...b, sequence: a.sequence }
    newStops[swapIndex] = { ...a, sequence: b.sequence }
    setStops(newStops)

    setSaving(a.orderId)
    try {
      await reorderRouteStop(route.id, a.orderId, b.sequence)
      onUpdate({ ...route, stops: newStops })
    } finally {
      setSaving(null)
    }
  }

  const statusColors: Record<string, string> = {
    Confirmed: 'bg-green-100 text-green-700',
    Rejected: 'bg-red-100 text-red-700',
    ConfirmationPending: 'bg-yellow-100 text-yellow-700',
    SentEmail: 'bg-blue-50 text-blue-600',
    Pending: 'bg-gray-50 text-gray-500',
    Expired: 'bg-red-50 text-red-400',
  }

  return (
    <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
      <div className="px-5 py-4 border-b border-gray-100 flex items-center justify-between">
        <div>
          <h2 className="font-semibold text-gray-900">{route.totalStops} Stops</h2>
          <p className="text-xs text-gray-500 mt-0.5">{route.startAddress} → {route.endAddress}</p>
        </div>
        <div className="text-xs text-gray-400">{route.id.slice(0, 8)}</div>
      </div>

      <div className="divide-y divide-gray-50">
        {stops.map((stop, i) => (
          <div key={stop.orderId} className="px-5 py-3.5 flex items-center gap-4">
            {/* Sequence number + reorder */}
            <div className="flex flex-col items-center gap-0.5 shrink-0">
              <button
                onClick={() => moveStop(i, -1)}
                disabled={i === 0 || saving === stop.orderId}
                className="text-gray-300 hover:text-gray-500 disabled:invisible leading-none text-xs"
              >▲</button>
              <span className="w-7 h-7 rounded-full bg-brand-100 text-brand-700 text-xs font-bold flex items-center justify-center">
                {stop.sequence}
              </span>
              <button
                onClick={() => moveStop(i, 1)}
                disabled={i === stops.length - 1 || saving === stop.orderId}
                className="text-gray-300 hover:text-gray-500 disabled:invisible leading-none text-xs"
              >▼</button>
            </div>

            {/* Store info */}
            <div className="flex-1 min-w-0">
              <p className="text-sm font-medium text-gray-900 truncate">{stop.storeName}</p>
              <p className="text-xs text-gray-500 truncate">{stop.address}</p>
              <div className="flex gap-3 mt-1 text-xs text-gray-400">
                {stop.estimatedArrival && (
                  <span>ETA {format(new Date(stop.estimatedArrival), 'HH:mm')}</span>
                )}
                <span>{stop.legDistanceMiles.toFixed(1)} mi</span>
              </div>
            </div>

            {/* Status + actions */}
            <div className="flex flex-col items-end gap-1.5 shrink-0">
              <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${statusColors[stop.confirmationStatus] ?? 'bg-gray-100 text-gray-500'}`}>
                {stop.confirmationStatus.replace(/([A-Z])/g, ' $1').trim()}
              </span>
              {stop.confirmationStatus !== 'Confirmed' && (
                <button
                  onClick={() => handleConfirm(stop)}
                  disabled={saving === stop.orderId}
                  className="text-xs text-brand-600 hover:text-brand-800 disabled:opacity-50"
                >
                  {saving === stop.orderId ? 'Confirming…' : 'Confirm'}
                </button>
              )}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
