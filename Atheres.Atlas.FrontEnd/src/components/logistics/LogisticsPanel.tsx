import { useEffect, useState } from 'react'
import { getRoutes, getTrucks, confirmDeliveryManual, reorderRouteStop, printRouteItinerary } from '../../services/apiService'
import type { Route, RouteStop, Truck, WarehousePickup } from '../../types'
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
    // Parse the y-m-d string as a LOCAL date — `new Date('2026-05-26')`
    // parses as UTC midnight, which lands on the previous calendar day
    // in any timezone west of UTC, so addDays(1) just shifts back to
    // today's local string and the arrows appear dead. Constructing
    // via `new Date(y, m-1, d)` anchors to local-zone midnight, so
    // addDays + format(yyyy-MM-dd) actually advances one calendar day.
    const [y, m, d] = date.split('-').map(Number)
    const local = new Date(y, m - 1, d)
    const shifted = days >= 0 ? addDays(local, days) : subDays(local, -days)
    setDate(format(shifted, 'yyyy-MM-dd'))
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
              // key={selectedRoute.id} forces a fresh StopEditor instance on
              // route switch. The editor's useState initializer derives its
              // local `stops` list from `route.stops` once at mount, so
              // without the key change React would keep showing the
              // previously-selected route's stops when the user clicks a
              // different route in the sidebar.
              <StopEditor
                key={selectedRoute.id}
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

  // Pickup routes have zero RouteStops by design — label them by role
  // instead of by an empty stop count. Other route types keep the
  // existing "N stops" wording.
  const isPickup = route.routeType === 'Pickup'
  const primary  = isPickup ? 'Pickup van' : `${route.totalStops} stops`
  const timeLine = (() => {
    if (isPickup && route.scheduledDepartTime && route.hubArrivalTime) {
      return `${format(new Date(route.scheduledDepartTime), 'HH:mm')} → ${format(new Date(route.hubArrivalTime), 'HH:mm')}`
    }
    if (route.scheduledDepartTime) {
      return `Depart ${format(new Date(route.scheduledDepartTime), 'HH:mm')}`
    }
    return null
  })()

  return (
    // role="button" + tabIndex so the whole card is clickable for
    // selection AND we can nest a real <button> inside (the Print icon)
    // without producing invalid nested-button HTML.
    <div
      role="button"
      tabIndex={0}
      onClick={onSelect}
      onKeyDown={(e) => {
        if (e.key === 'Enter' || e.key === ' ') {
          e.preventDefault()
          onSelect()
        }
      }}
      className={`w-full text-left bg-white rounded-xl border p-4 transition-colors cursor-pointer ${
        selected ? 'border-brand-400 ring-1 ring-brand-300' : 'border-gray-200 hover:border-gray-300'
      }`}
    >
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="font-semibold text-gray-900">{primary}</p>
          <p className="text-xs text-gray-500 mt-0.5">{route.totalDistanceMiles.toFixed(1)} mi · {route.totalDuration}</p>
          {timeLine && (
            <p className="text-xs font-medium text-gray-700 mt-1">{timeLine}</p>
          )}
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {/* Print → server-side iText PDF itinerary. stopPropagation so
              clicking the icon doesn't also select the route. */}
          <button
            type="button"
            onClick={(e) => { e.stopPropagation(); printRouteItinerary(route.id).catch(() => alert('Failed to generate route itinerary PDF.')) }}
            className="text-gray-500 hover:text-brand-600"
            title="Print this route's itinerary (PDF)"
            aria-label="Print itinerary"
          >
            🖨
          </button>
          {route.isOptimized && (
            <span className="text-xs bg-green-50 text-green-600 font-medium px-2 py-0.5 rounded-full">Optimized</span>
          )}
        </div>
      </div>
      {!isPickup && (
        <div className="mt-3 flex gap-3 text-xs">
          <span className="text-green-600">{confirmedCount} confirmed</span>
          <span className="text-yellow-600">{pendingCount} pending</span>
        </div>
      )}
    </div>
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

  // Routes always begin and end at the van's home hub. Render those as
  // depot bookends so the trip arc (depot → stops → depot) is unmistakable.
  const sameDepot =
    route.startAddress === route.endAddress &&
    route.startLatitude === route.endLatitude &&
    route.startLongitude === route.endLongitude

  const isPickup = route.routeType === 'Pickup'
  // For Pickup routes the body has no stops, just the round trip rows.
  // Compute the warehouse-load midpoint from depart+return millis so the
  // operator sees three real timestamps instead of two endpoints and a
  // mystery middle row. Same 50/50 estimate the AdminPanel cards use —
  // round-trip outbound and inbound are typically symmetric. If
  // ScheduledDepartTime or HubArrivalTime is null we just hide the time.
  let pickupMidIso: string | null = null
  if (isPickup && route.scheduledDepartTime && route.hubArrivalTime) {
    const d = new Date(route.scheduledDepartTime).getTime()
    const a = new Date(route.hubArrivalTime).getTime()
    if (Number.isFinite(d) && Number.isFinite(a) && a > d) {
      pickupMidIso = new Date((d + a) / 2).toISOString()
    }
  }

  // Header time strip + count vary by route type. Pickup has 0 stops by
  // design so "0 Stops" reads as broken — replace with a clearer label
  // and the depart→return window.
  const headerCountLabel = isPickup
    ? 'Warehouse pickup run'
    : `${route.totalStops} Stops`
  const headerTimeStrip = (() => {
    if (isPickup && route.scheduledDepartTime && route.hubArrivalTime) {
      return `Depart ${format(new Date(route.scheduledDepartTime), 'HH:mm')} → Return ${format(new Date(route.hubArrivalTime), 'HH:mm')}`
    }
    if (route.scheduledDepartTime) {
      return `Depart ${format(new Date(route.scheduledDepartTime), 'HH:mm')}`
    }
    return null
  })()

  return (
    <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
      <div className="px-5 py-4 border-b border-gray-100 flex items-center justify-between">
        <div>
          <h2 className="font-semibold text-gray-900">{headerCountLabel}</h2>
          <p className="text-xs text-gray-500 mt-0.5">{route.startAddress} → {route.endAddress}</p>
          {headerTimeStrip && (
            <p className="text-xs font-medium text-gray-700 mt-1">{headerTimeStrip}</p>
          )}
        </div>
        <div className="text-xs text-gray-400">{route.id.slice(0, 8)}</div>
      </div>

      <div className="divide-y divide-gray-50">
        {/* Start (depot origin) — depart time when known (always known for
            new-flow routes; null for Legacy). */}
        <DepotRow
          kind="start"
          address={route.startAddress}
          timeIso={route.scheduledDepartTime}
          timeLabel="leave"
        />

        {/* Warehouse pickup — pinned first physical stop after the depot.
            Always before any delivery; the route was built specifically
            so the driver picks up products here before any drop-off.
            For Pickup-only routes the warehouse load time is the round
            trip's midpoint, not pickup.estimatedArrival (which is null
            because Pickup has no first delivery stop to back-calc from). */}
        {route.warehousePickup && (
          <WarehousePickupRow
            pickup={route.warehousePickup}
            overrideTimeIso={isPickup ? pickupMidIso : undefined}
            timeLabel={isPickup ? 'load (est.)' : undefined}
          />
        )}

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

        {/* End (depot return). Pickup routes carry an explicit
            HubArrivalTime (the round-trip return). Delivery routes don't
            yet persist a hub-return ETA, so the cell stays blank for
            them — adding it would require extending the optimizer to
            stamp final-leg-completes alongside per-stop ETAs. */}
        <DepotRow
          kind="end"
          address={route.endAddress}
          subtitle={sameDepot ? 'Return to start hub' : undefined}
          timeIso={isPickup ? route.hubArrivalTime : null}
          timeLabel={isPickup ? 'arrive' : undefined}
        />
      </div>
    </div>
  )
}

// ---- Depot Row (route start / end bookend) ----

function DepotRow({ kind, address, subtitle, timeIso, timeLabel }: {
  kind: 'start' | 'end'
  address: string
  subtitle?: string
  /** Optional ISO timestamp for this depot row — depart for start, arrive for end. */
  timeIso?: string | null
  /** Caption next to the time, e.g. "leave" / "arrive". */
  timeLabel?: string
}) {
  const isStart = kind === 'start'
  const label = isStart ? 'Start' : 'End'
  // Filled square (vs. round numbered chip on stops) so depot reads at a glance.
  const chipColor = isStart ? 'bg-emerald-500' : 'bg-rose-500'
  const labelColor = isStart ? 'text-emerald-700' : 'text-rose-700'
  const bgTint = isStart ? 'bg-emerald-50/40' : 'bg-rose-50/40'

  return (
    <div className={`px-5 py-3.5 flex items-center gap-4 ${bgTint}`}>
      <div className="flex flex-col items-center gap-0.5 shrink-0 w-7">
        <span className={`w-7 h-7 rounded-md ${chipColor} text-white text-[10px] font-bold flex items-center justify-center uppercase tracking-wide`}>
          {isStart ? 'S' : 'E'}
        </span>
      </div>
      <div className="flex-1 min-w-0">
        <p className={`text-sm font-semibold ${labelColor}`}>
          {label} · Depot
        </p>
        <p className="text-xs text-gray-600 truncate">{address}</p>
        {subtitle && <p className="text-[11px] text-gray-400 mt-0.5">{subtitle}</p>}
      </div>
      {timeIso && (
        <div className="text-right shrink-0">
          <p className="text-xs font-medium text-gray-700">{format(new Date(timeIso), 'HH:mm')}</p>
          {timeLabel && <p className="text-[10px] text-gray-400">{timeLabel}</p>}
        </div>
      )}
    </div>
  )
}

// ---- Warehouse Pickup Row -----------------------------------------------
//
// Pinned between the depot start row and the delivery sequence. Visually
// distinct from both: amber-tinted background, "W" chip, "Pickup" label.
// The driver always visits this stop first to load product before any of
// the customer deliveries.

function WarehousePickupRow({ pickup, overrideTimeIso, timeLabel }: {
  pickup: WarehousePickup
  /** When set, replaces pickup.estimatedArrival — used for Pickup-only routes
   *  where the warehouse load is the midpoint of the round trip, not the
   *  back-calculated first-stop minus leg-0 that drives the legacy field. */
  overrideTimeIso?: string | null
  timeLabel?: string
}) {
  const timeIso = overrideTimeIso ?? pickup.estimatedArrival
  return (
    <div className="px-5 py-3.5 flex items-center gap-4 bg-amber-50/40">
      <div className="flex flex-col items-center gap-0.5 shrink-0 w-7">
        <span className="w-7 h-7 rounded-md bg-amber-500 text-white text-[10px] font-bold flex items-center justify-center uppercase tracking-wide">
          W
        </span>
      </div>
      <div className="flex-1 min-w-0">
        <p className="text-sm font-semibold text-amber-700">
          Pickup · {pickup.name}
        </p>
        <p className="text-xs text-gray-600 truncate">{pickup.address}</p>
      </div>
      {timeIso && (
        <div className="text-right shrink-0">
          <p className="text-xs font-medium text-gray-700">{format(new Date(timeIso), 'HH:mm')}</p>
          <p className="text-[10px] text-gray-400">{timeLabel ?? 'ETA'}</p>
        </div>
      )}
    </div>
  )
}
