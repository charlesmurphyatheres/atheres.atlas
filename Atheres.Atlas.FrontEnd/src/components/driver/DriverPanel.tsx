import { useEffect, useState } from 'react'
import { getRoutes } from '../../services/apiService'
import type { Route, RouteStop } from '../../types'
import { format } from 'date-fns'
import { useAuth } from '../../contexts/AuthContext'

export default function DriverPanel() {
  const { user } = useAuth()
  const [route, setRoute] = useState<Route | null>(null)
  const [loading, setLoading] = useState(true)
  const today = format(new Date(), 'yyyy-MM-dd')

  useEffect(() => {
    getRoutes(today)
      .then((routes) => {
        // Find the route assigned to this driver's truck, or first available
        setRoute(routes[0] ?? null)
      })
      .finally(() => setLoading(false))
  }, [today])

  if (loading) {
    return (
      <div className="flex items-center justify-center h-64 text-gray-400">
        <div className="w-6 h-6 border-2 border-brand-500 border-t-transparent rounded-full animate-spin mr-2" />
        Loading your route…
      </div>
    )
  }

  return (
    <div className="space-y-4 max-w-lg mx-auto">
      {/* Driver header */}
      <div className="bg-brand-500 rounded-2xl p-5 text-white">
        <p className="text-sm opacity-80">Good day, {user?.fullName?.split(' ')[0] ?? 'Driver'}</p>
        <h1 className="text-xl font-bold mt-0.5">Today's Route</h1>
        <p className="text-sm opacity-70 mt-1">{format(new Date(), 'EEEE, MMMM d')}</p>
      </div>

      {!route ? (
        <div className="bg-white rounded-xl border border-gray-200 p-8 text-center">
          <p className="text-4xl mb-3">🚛</p>
          <p className="font-medium text-gray-700">No route assigned for today</p>
          <p className="text-sm text-gray-400 mt-1">Check back later or contact your scheduler.</p>
        </div>
      ) : (
        <>
          {/* Route summary */}
          <div className="bg-white rounded-xl border border-gray-200 p-4">
            <div className="grid grid-cols-3 gap-4 text-center">
              <div>
                <p className="text-2xl font-bold text-gray-900">{route.totalStops}</p>
                <p className="text-xs text-gray-500 mt-0.5">Stops</p>
              </div>
              <div>
                <p className="text-2xl font-bold text-gray-900">{route.totalDistanceMiles.toFixed(0)}</p>
                <p className="text-xs text-gray-500 mt-0.5">Miles</p>
              </div>
              <div>
                <p className="text-2xl font-bold text-gray-900">
                  {route.stops.filter((s) => s.confirmationStatus === 'Confirmed').length}
                </p>
                <p className="text-xs text-gray-500 mt-0.5">Confirmed</p>
              </div>
            </div>
          </div>

          {/* Stop list */}
          <div className="space-y-2">
            {[...route.stops].sort((a, b) => a.sequence - b.sequence).map((stop) => (
              <StopCard key={stop.orderId} stop={stop} />
            ))}
          </div>

          {/* Depot info */}
          <div className="bg-gray-50 rounded-xl border border-gray-200 p-4">
            <p className="text-xs font-medium text-gray-500 uppercase tracking-wide mb-2">Route</p>
            <div className="flex items-start gap-2 text-sm text-gray-700">
              <span className="text-green-500 mt-0.5">●</span>
              <span className="text-xs">{route.startAddress}</span>
            </div>
            <div className="ml-2 border-l-2 border-dashed border-gray-300 h-3 my-0.5" />
            <div className="flex items-start gap-2 text-sm text-gray-700">
              <span className="text-red-400 mt-0.5">●</span>
              <span className="text-xs">{route.endAddress}</span>
            </div>
          </div>
        </>
      )}
    </div>
  )
}

function StopCard({ stop }: { stop: RouteStop }) {
  const isConfirmed = stop.confirmationStatus === 'Confirmed'
  const isDelivered = stop.orderStatus === 'Delivered'

  return (
    <div className={`bg-white rounded-xl border p-4 ${
      isDelivered ? 'border-gray-100 opacity-60' : isConfirmed ? 'border-green-200' : 'border-gray-200'
    }`}>
      <div className="flex items-start gap-3">
        {/* Sequence indicator */}
        <div className={`w-8 h-8 rounded-full flex items-center justify-center text-sm font-bold shrink-0 ${
          isDelivered ? 'bg-gray-100 text-gray-400' :
          isConfirmed ? 'bg-green-100 text-green-700' :
          'bg-brand-100 text-brand-700'
        }`}>
          {isDelivered ? '✓' : stop.sequence}
        </div>

        {/* Stop details */}
        <div className="flex-1 min-w-0">
          <div className="flex items-start justify-between gap-2">
            <p className="font-semibold text-gray-900 text-sm leading-tight">{stop.storeName}</p>
            <ConfirmBadge status={stop.confirmationStatus} />
          </div>
          <p className="text-xs text-gray-500 mt-0.5 truncate">{stop.address}</p>

          <div className="flex gap-4 mt-2 text-xs text-gray-400">
            {stop.estimatedArrival && (
              <span className="flex items-center gap-1">
                <span>🕐</span>
                ETA {format(new Date(stop.estimatedArrival), 'h:mm a')}
              </span>
            )}
            {stop.legDistanceMiles > 0 && (
              <span className="flex items-center gap-1">
                <span>📍</span>
                {stop.legDistanceMiles.toFixed(1)} mi
              </span>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}

function ConfirmBadge({ status }: { status: string }) {
  const map: Record<string, { label: string; cls: string }> = {
    Confirmed: { label: 'Confirmed', cls: 'bg-green-100 text-green-700' },
    ConfirmationPending: { label: 'Pending', cls: 'bg-yellow-100 text-yellow-700' },
    SentEmail: { label: 'Email Sent', cls: 'bg-blue-50 text-blue-600' },
    Rejected: { label: 'Rejected', cls: 'bg-red-50 text-red-500' },
    Pending: { label: 'Awaiting', cls: 'bg-gray-100 text-gray-500' },
    Expired: { label: 'Expired', cls: 'bg-red-50 text-red-400' },
  }
  const badge = map[status] ?? { label: status, cls: 'bg-gray-100 text-gray-500' }
  return (
    <span className={`px-2 py-0.5 rounded-full text-xs font-medium shrink-0 ${badge.cls}`}>
      {badge.label}
    </span>
  )
}
