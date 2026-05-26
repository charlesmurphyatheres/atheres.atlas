import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { GoogleMap, useJsApiLoader, MarkerF, PolylineF, InfoWindow } from '@react-google-maps/api'
import { getRoutes } from '../services/apiService'
import type { Route, RouteStop } from '../types'
import { format, addDays, subDays } from 'date-fns'

const GOOGLE_MAPS_KEY = import.meta.env.VITE_GOOGLE_MAPS_KEY ?? ''

// Distinct colors for up to 10 routes
const ROUTE_COLORS = [
  '#1a56db', '#dc2626', '#16a34a', '#ea580c', '#7c3aed',
  '#0891b2', '#db2777', '#65a30d', '#c026d3', '#ca8a04',
]

function decodePolyline(encoded: string): { lat: number; lng: number }[] {
  const points: { lat: number; lng: number }[] = []
  let index = 0, lat = 0, lng = 0
  while (index < encoded.length) {
    let b, shift = 0, result = 0
    do { b = encoded.charCodeAt(index++) - 63; result |= (b & 0x1f) << shift; shift += 5 } while (b >= 0x20)
    lat += (result & 1) ? ~(result >> 1) : (result >> 1)
    shift = 0; result = 0
    do { b = encoded.charCodeAt(index++) - 63; result |= (b & 0x1f) << shift; shift += 5 } while (b >= 0x20)
    lng += (result & 1) ? ~(result >> 1) : (result >> 1)
    points.push({ lat: lat / 1e5, lng: lng / 1e5 })
  }
  return points
}

function interpolatePosition(
  stops: RouteStop[],
  startTime: Date,
  now: Date
): { lat: number; lng: number; currentStopIndex: number } | null {
  if (stops.length === 0) return null
  const sorted = [...stops].sort((a, b) => a.sequence - b.sequence)

  // Find which leg the truck is on based on ETA times
  for (let i = 0; i < sorted.length; i++) {
    const stopEta = sorted[i].estimatedArrival ? new Date(sorted[i].estimatedArrival!) : null
    if (!stopEta) continue

    if (now < stopEta) {
      // Truck is between previous stop (or start) and this stop
      const prevEta = i > 0 && sorted[i - 1].estimatedArrival
        ? new Date(sorted[i - 1].estimatedArrival!)
        : startTime

      const totalLeg = stopEta.getTime() - prevEta.getTime()
      const elapsed = now.getTime() - prevEta.getTime()
      const progress = totalLeg > 0 ? Math.max(0, Math.min(1, elapsed / totalLeg)) : 0

      const fromLat = i > 0 ? sorted[i - 1].latitude : sorted[0].latitude
      const fromLng = i > 0 ? sorted[i - 1].longitude : sorted[0].longitude
      const toLat = sorted[i].latitude
      const toLng = sorted[i].longitude

      return {
        lat: fromLat + (toLat - fromLat) * progress,
        lng: fromLng + (toLng - fromLng) * progress,
        currentStopIndex: i,
      }
    }
  }

  // Past all stops — truck at last stop
  const last = sorted[sorted.length - 1]
  return { lat: last.latitude, lng: last.longitude, currentStopIndex: sorted.length - 1 }
}

export default function LiveRouteTracker() {
  const [selectedDate, setSelectedDate] = useState(format(new Date(), 'yyyy-MM-dd'))
  const [routes, setRoutes] = useState<Route[]>([])
  const [loading, setLoading] = useState(true)
  const [selectedRoute, setSelectedRoute] = useState<string | null>(null)
  const [selectedStop, setSelectedStop] = useState<RouteStop | null>(null)
  // Per-route map-visibility filter. Empty set = all routes visible (the
  // default after a fresh fetch). Decoupled from selectedRoute so the
  // operator can pick which routes the map renders without losing the
  // expand/collapse state of the sidebar detail.
  const [hiddenRouteIds, setHiddenRouteIds] = useState<Set<string>>(new Set())
  const [now, setNow] = useState(new Date())
  const [liveMode, setLiveMode] = useState(true)
  const mapRef = useRef<google.maps.Map | null>(null)

  // Imperative overlay tracking. The @react-google-maps/api Polyline/Marker
  // components don't reliably propagate `visible` prop changes to the
  // underlying SDK objects, so toggling React state alone could leave
  // lines drawn on the map. Each Polyline/Marker registers itself via
  // onLoad and de-registers via onUnmount; the syncOverlayVisibility
  // effect below then explicitly detaches every overlay from the map
  // before re-attaching the visible ones — the "remove all then redraw"
  // pattern, applied on every hiddenRouteIds change.
  const polylinesRef = useRef<Map<string, google.maps.Polyline>>(new Map())
  const markersRef   = useRef<Map<string, google.maps.Marker[]>>(new Map())

  const registerPolyline = useCallback((routeId: string) => ({
    onLoad: (p: google.maps.Polyline) => { polylinesRef.current.set(routeId, p) },
    onUnmount: () => { polylinesRef.current.delete(routeId) },
  }), [])

  const registerMarker = useCallback((routeId: string) => ({
    onLoad: (m: google.maps.Marker) => {
      const arr = markersRef.current.get(routeId) ?? []
      arr.push(m)
      markersRef.current.set(routeId, arr)
    },
    onUnmount: (m: google.maps.Marker) => {
      const arr = markersRef.current.get(routeId)
      if (!arr) return
      const next = arr.filter((x) => x !== m)
      if (next.length === 0) markersRef.current.delete(routeId)
      else                   markersRef.current.set(routeId, next)
    },
  }), [])

  const { isLoaded } = useJsApiLoader({
    id: 'google-map-script',
    googleMapsApiKey: GOOGLE_MAPS_KEY,
  })

  useEffect(() => {
    // Clear synchronously BEFORE the async fetch runs so the map wipes the
    // previous day's polylines + markers the moment the date changes —
    // otherwise the prior routes stay drawn until the fetch resolves,
    // which is what was making it look like "old routes still on the map."
    setLoading(true)
    setRoutes([])
    setSelectedRoute(null)
    setSelectedStop(null)
    setHiddenRouteIds(new Set())

    // Stale-fetch guard: rapid arrow-clicks can let an earlier fetch's
    // slower response land after a newer one and clobber it. Track whether
    // this effect run is still the latest; if a later run started in the
    // meantime (cancelled flips true), drop the response on the floor.
    let cancelled = false
    getRoutes(selectedDate)
      .then((r) => {
        if (!cancelled) setRoutes(r)
      })
      .finally(() => {
        if (!cancelled) setLoading(false)
      })
    return () => { cancelled = true }
  }, [selectedDate])

  // Tick every 10 seconds for live simulation
  useEffect(() => {
    if (!liveMode) return
    const interval = setInterval(() => setNow(new Date()), 10000)
    return () => clearInterval(interval)
  }, [liveMode])

  const isToday = selectedDate === format(new Date(), 'yyyy-MM-dd')

  // Step the date by N days while staying in local time. Parsing a y-m-d
  // string via `new Date(str)` lands at UTC midnight, which in any
  // timezone west of UTC rolls back to the previous calendar day — the
  // arrow then appears dead. Constructing via the (y, m-1, d) ctor anchors
  // to LOCAL midnight so addDays + format(yyyy-MM-dd) actually moves a day.
  // Same pattern used in LogisticsPanel.
  function shiftDate(days: number) {
    const [y, m, d] = selectedDate.split('-').map(Number)
    const local = new Date(y, m - 1, d)
    const shifted = days >= 0 ? addDays(local, days) : subDays(local, -days)
    setSelectedDate(format(shifted, 'yyyy-MM-dd'))
  }

  // Compute truck positions
  const truckPositions = useMemo(() => {
    if (!isToday || !liveMode) return []
    return routes.map((route, i) => {
      const sorted = [...route.stops].sort((a, b) => a.sequence - b.sequence)
      const firstEta = sorted[0]?.estimatedArrival ? new Date(sorted[0].estimatedArrival) : null
      if (!firstEta) return null
      // Assume truck departs 30 min before first stop ETA
      const startTime = new Date(firstEta.getTime() - 30 * 60 * 1000)
      const pos = interpolatePosition(sorted, startTime, now)
      if (!pos) return null
      return { routeIndex: i, routeId: route.id, ...pos }
    }).filter(Boolean)
  }, [routes, now, isToday, liveMode])

  // Visibility is now driven by per-route checkboxes (hiddenRouteIds). The
  // sidebar's selectedRoute drives expand/collapse only and no longer
  // filters the map. Show All / Hide All buttons bulk-edit the set.
  const visibleRoutes = useMemo(
    () => routes.filter((r) => !hiddenRouteIds.has(r.id)),
    [routes, hiddenRouteIds],
  )

  const toggleRouteVisibility = useCallback((routeId: string) => {
    setHiddenRouteIds((prev) => {
      const next = new Set(prev)
      if (next.has(routeId)) next.delete(routeId)
      else next.add(routeId)
      return next
    })
  }, [])

  const showAllRoutes = useCallback(() => setHiddenRouteIds(new Set()), [])
  const hideAllRoutes = useCallback(
    () => setHiddenRouteIds(new Set(routes.map((r) => r.id))),
    [routes],
  )

  const hiddenCount = hiddenRouteIds.size

  // Fit bounds to whatever the sidebar currently filters to, not the full
  // route set — clicking a route in the sidebar should re-zoom the map to
  // that route. Re-runs on selectedRoute change as well as data changes.
  const onMapLoad = useCallback((map: google.maps.Map) => {
    mapRef.current = map
    fitBounds(map, visibleRoutes)
  }, [visibleRoutes])

  useEffect(() => {
    if (mapRef.current && visibleRoutes.length > 0) fitBounds(mapRef.current, visibleRoutes)
  }, [visibleRoutes])

  // Imperative "remove all then redraw visible" sync. Runs whenever the
  // visibility set or the route list changes. We always detach every
  // tracked overlay first (setMap(null)) so there's no possibility of an
  // orphan line — then re-attach only the ones whose route isn't hidden.
  useEffect(() => {
    const map = mapRef.current
    if (!map) return
    polylinesRef.current.forEach((p) => p.setMap(null))
    markersRef.current.forEach((arr) => arr.forEach((m) => m.setMap(null)))
    for (const route of routes) {
      if (hiddenRouteIds.has(route.id)) continue
      polylinesRef.current.get(route.id)?.setMap(map)
      markersRef.current.get(route.id)?.forEach((m) => m.setMap(map))
    }
  }, [hiddenRouteIds, routes])

  return (
    <div className="flex flex-col h-[calc(100vh-8rem)]">
      {/* Header bar */}
      <div className="flex items-center justify-between px-1 pb-3 shrink-0">
        <div className="flex items-center gap-3">
          <h1 className="text-xl font-bold text-gray-900">Live Routes</h1>
          <div className="flex items-center gap-1 border border-gray-300 rounded-lg px-1 py-0.5">
            <button
              onClick={() => shiftDate(-1)}
              aria-label="Previous day"
              className="px-1.5 py-0.5 rounded text-gray-500 hover:bg-gray-100"
            >
              ‹
            </button>
            <input
              type="date"
              value={selectedDate}
              onChange={(e) => setSelectedDate(e.target.value)}
              className="text-sm border-none bg-transparent focus:outline-none px-1"
            />
            <button
              onClick={() => shiftDate(1)}
              aria-label="Next day"
              className="px-1.5 py-0.5 rounded text-gray-500 hover:bg-gray-100"
            >
              ›
            </button>
          </div>
          {isToday && (
            <button onClick={() => setLiveMode(!liveMode)}
              className={`text-xs px-2 py-1 rounded-full font-medium ${
                liveMode ? 'bg-green-100 text-green-700' : 'bg-gray-100 text-gray-500'
              }`}>
              {liveMode ? 'LIVE' : 'Paused'}
            </button>
          )}
        </div>
        <span className="text-xs text-gray-400">
          {hiddenCount > 0
            ? `${visibleRoutes.length} of ${routes.length} routes shown`
            : `${routes.length} route${routes.length !== 1 ? 's' : ''}`}
          {' · '}
          {visibleRoutes.reduce((s, r) => s + r.totalStops, 0)} stops
        </span>
      </div>

      <div className="flex flex-1 gap-3 min-h-0">
        {/* Sidebar */}
        <div className="w-72 shrink-0 flex flex-col gap-2 overflow-y-auto">
          {/* Bulk visibility controls. Disabled when there's nothing to
              affect — "Show all" when nothing is hidden, "Hide all" when
              everything already is — so the operator gets immediate
              feedback on what's actionable. */}
          <div className="flex gap-2 px-1">
            <button
              onClick={showAllRoutes}
              disabled={hiddenCount === 0 || routes.length === 0}
              className="flex-1 text-xs px-2 py-1.5 rounded-lg border border-gray-200 text-gray-600 hover:bg-gray-50 disabled:opacity-40 disabled:hover:bg-transparent"
            >
              Show all
            </button>
            <button
              onClick={hideAllRoutes}
              disabled={hiddenCount === routes.length || routes.length === 0}
              className="flex-1 text-xs px-2 py-1.5 rounded-lg border border-gray-200 text-gray-600 hover:bg-gray-50 disabled:opacity-40 disabled:hover:bg-transparent"
            >
              Hide all
            </button>
          </div>
          {routes.map((route, i) => {
            const color = ROUTE_COLORS[i % ROUTE_COLORS.length]
            const truckPos = truckPositions.find((t: any) => t?.routeId === route.id)
            const completedStops = truckPos ? truckPos.currentStopIndex : 0
            const isExpanded = selectedRoute === route.id
            const sortedStops = route.stops.slice().sort((a, b) => a.sequence - b.sequence)
            // Pickup routes legitimately carry 0 delivery stops; show a clearer
            // label than "0 stops" so the operator knows it isn't broken.
            const isPickup = route.routeType === 'Pickup'
            // Routes always begin and end at the same hub today; only render
            // a distinct End row if the addresses actually diverge.
            const sameDepot =
              route.startAddress === route.endAddress &&
              route.startLatitude === route.endLatitude &&
              route.startLongitude === route.endLongitude
            const isVisible = !hiddenRouteIds.has(route.id)
            return (
              <div key={route.id}
                className={`rounded-lg border transition-colors flex ${
                  isExpanded ? 'border-gray-300 bg-white shadow-sm' : 'border-gray-100 hover:border-gray-200 bg-white'
                } ${isVisible ? '' : 'opacity-60'}`}>
                {/* Visibility checkbox — hard on/off filter for the map.
                    Sits alongside the expand button rather than inside it
                    so the two interactive areas don't nest (invalid HTML
                    + accessibility-hostile). Top-aligned so it lines up
                    with the route name, not the centre of the (variable
                    height) expanded card. */}
                <label
                  className="flex items-start pl-3 pr-1 pt-3.5 cursor-pointer shrink-0"
                  title={isVisible ? 'Hide this route on the map' : 'Show this route on the map'}
                >
                  <input
                    type="checkbox"
                    checked={isVisible}
                    onChange={() => toggleRouteVisibility(route.id)}
                    className="h-3.5 w-3.5"
                    aria-label={`Show route ${i + 1} on map`}
                  />
                </label>
                <div className="flex-1 min-w-0">
                {/* Header — click toggles expand. Independent of the
                    visibility checkbox; the operator can expand a hidden
                    route to read its detail without forcing the map to
                    re-render it. */}
                <button
                  type="button"
                  onClick={() => setSelectedRoute(isExpanded ? null : route.id)}
                  className="w-full text-left p-3"
                  aria-expanded={isExpanded}
                >
                  <div className="flex items-center gap-2">
                    <span className="w-3 h-3 rounded-full shrink-0" style={{ backgroundColor: color }} />
                    <span className="text-sm font-medium text-gray-900 truncate">
                      Route {i + 1}
                    </span>
                    {isToday && liveMode && truckPos && (
                      <span className="ml-auto text-[10px] bg-green-100 text-green-700 px-1.5 py-0.5 rounded-full">
                        {completedStops}/{route.totalStops}
                      </span>
                    )}
                    <span className={`${isToday && liveMode && truckPos ? 'ml-1' : 'ml-auto'} text-gray-300 text-xs leading-none transition-transform ${isExpanded ? 'rotate-90' : ''}`}>
                      ▸
                    </span>
                  </div>
                  <div className="text-xs text-gray-500 mt-1">
                    {isPickup ? 'Pickup run' : `${route.totalStops} stops`} · {route.totalDistanceMiles.toFixed(1)} mi · {route.totalDuration}
                  </div>
                  {!isExpanded && (
                    <div className="flex flex-wrap gap-1 mt-2">
                      {route.warehousePickup && (
                        <span
                          title={`Pickup at ${route.warehousePickup.name}`}
                          className="text-[10px] px-1.5 py-0.5 rounded bg-amber-100 text-amber-700 font-medium"
                        >
                          W
                        </span>
                      )}
                      {sortedStops.slice(0, 6).map((s) => (
                        <span key={s.orderId}
                          className={`text-[10px] px-1.5 py-0.5 rounded ${
                            s.confirmationStatus === 'Confirmed' ? 'bg-green-50 text-green-600' :
                            (s.confirmationStatus as string) === 'Rejected' ? 'bg-red-50 text-red-500' :
                            'bg-gray-50 text-gray-400'
                          }`}>
                          {s.sequence}
                        </span>
                      ))}
                      {route.totalStops > 6 && <span className="text-[10px] text-gray-300">+{route.totalStops - 6}</span>}
                    </div>
                  )}
                </button>

                {/* Expanded detail — mirrors the Logistics StopEditor shape
                    (depot → warehouse → stops → depot) but compacted for the
                    w-72 sidebar and read-only (no reorder / manual confirm).
                    Clicking a stop row opens the same InfoWindow used by the
                    map markers so the right-hand map stays in sync. */}
                {isExpanded && (
                  <div className="border-t border-gray-100 divide-y divide-gray-50">
                    <MiniDepotRow
                      kind="start"
                      address={route.startAddress}
                      timeIso={route.scheduledDepartTime ?? null}
                      timeLabel="leave"
                    />
                    {route.warehousePickup && (
                      <MiniWarehousePickupRow
                        name={route.warehousePickup.name}
                        address={route.warehousePickup.address}
                        timeIso={route.warehousePickup.estimatedArrival ?? null}
                      />
                    )}
                    {sortedStops.map((stop) => (
                      <MiniStopRow key={stop.orderId} stop={stop} onClick={() => setSelectedStop(stop)} />
                    ))}
                    {sortedStops.length === 0 && !route.warehousePickup && (
                      <p className="px-3 py-2 text-[11px] text-gray-400 italic">No stops on this route.</p>
                    )}
                    <MiniDepotRow
                      kind="end"
                      address={route.endAddress}
                      subtitle={sameDepot ? 'Return to start hub' : undefined}
                      timeIso={isPickup ? (route.hubArrivalTime ?? null) : null}
                      timeLabel={isPickup ? 'arrive' : undefined}
                    />
                  </div>
                )}
                </div>
              </div>
            )
          })}
          {routes.length === 0 && !loading && (
            <p className="text-sm text-gray-400 p-3">No routes for this date.</p>
          )}
        </div>

        {/* Map */}
        <div className="flex-1 rounded-xl border border-gray-200 overflow-hidden">
          {isLoaded ? (
            // key={selectedDate} forces a full remount of the map (and
            // every Polyline / Marker child) on date change. Without it,
            // @react-google-maps/api ≤ 2.19 sometimes leaves orphaned
            // overlays on the underlying google.maps.Map when the React
            // children unmount — visually that reads as "old day's routes
            // still on the map." Remounting tears down the DOM-node + SDK
            // state entirely, so the next day starts clean.
            <GoogleMap
              key={selectedDate}
              mapContainerStyle={{ width: '100%', height: '100%' }}
              center={{ lat: 40.0, lng: -89.0 }}
              zoom={7}
              onLoad={onMapLoad}
              onUnmount={() => { mapRef.current = null }}
              options={{ streetViewControl: false, mapTypeControl: false }}
            >
              {/* Polylines + markers iterate over the FULL routes set, not
                  visibleRoutes. Per-route visibility is applied via the SDK's
                  `visible` flag (PolylineOptions.visible / MarkerProps.visible)
                  instead of unmounting React elements — the @react-google-maps
                  /api 2.19.x cleanup bug leaves orphan overlays on the
                  underlying map when children unmount, so toggling a checkbox
                  used to leave the line drawn. Keeping every overlay mounted
                  and flipping its visibility avoids the bug entirely. */}

              {/* Polylines + markers iterate over every route (not just the
                  visible set). React keeps every overlay mounted; the
                  syncOverlayVisibility useEffect above is what actually
                  attaches / detaches them from the map when the operator
                  toggles a checkbox. Each overlay calls register*() on
                  load so the imperative sync can find them by route ID. */}

              {/* Polylines */}
              {routes.map((route, i) => {
                const color = ROUTE_COLORS[i % ROUTE_COLORS.length]
                if (!route.overviewPolyline) return null
                return (
                  <PolylineF
                    key={route.id}
                    {...registerPolyline(route.id)}
                    path={decodePolyline(route.overviewPolyline)}
                    options={{
                      strokeColor: color,
                      strokeOpacity: 0.8,
                      // The expanded route is drawn thicker so the operator
                      // can find it on a busy map.
                      strokeWeight: selectedRoute === route.id ? 5 : 3,
                    }}
                  />
                )
              })}

              {/* Hub markers (square, slate) — the depot the route starts
                  and returns to. Latitude/longitude are persisted on the
                  route so the same coords drive the marker as draw the
                  polyline endpoints. */}
              {routes.flatMap((route) => {
                if (!route.startLatitude || !route.startLongitude) return []
                return [(
                  <MarkerF
                    key={`hub-${route.id}`}
                    {...registerMarker(route.id)}
                    position={{ lat: route.startLatitude, lng: route.startLongitude }}
                    label={{ text: 'H', color: '#fff', fontWeight: 'bold', fontSize: '11px' }}
                    title={`Hub: ${route.startAddress}`}
                    icon={{
                      path: 'M -10 -10 L 10 -10 L 10 10 L -10 10 Z',  // square
                      fillColor: '#1f2937', // slate-800 — distinct from warehouse amber + delivery hue
                      fillOpacity: 1,
                      strokeColor: '#fff',
                      strokeWeight: 2,
                      scale: 1,
                    }}
                    zIndex={500}
                  />
                )]
              })}

              {/* Warehouse pickup markers (square, amber) — appear before any
                  stop on each route, signalling "first physical stop / pickup". */}
              {routes.flatMap((route) => {
                if (!route.warehousePickup
                    || route.warehousePickup.latitude == null
                    || route.warehousePickup.longitude == null) return []
                return [(
                  <MarkerF
                    key={`wh-${route.id}`}
                    {...registerMarker(route.id)}
                    position={{ lat: route.warehousePickup.latitude, lng: route.warehousePickup.longitude }}
                    label={{ text: 'W', color: '#fff', fontWeight: 'bold', fontSize: '11px' }}
                    title={`Pickup: ${route.warehousePickup.name}`}
                    icon={{
                      path: 'M -10 -10 L 10 -10 L 10 10 L -10 10 Z',  // square
                      fillColor: '#d97706', // amber-600
                      fillOpacity: 1,
                      strokeColor: '#fff',
                      strokeWeight: 2,
                      scale: 1,
                    }}
                  />
                )]
              })}

              {/* Stop markers */}
              {routes.flatMap((route, i) => {
                const color = ROUTE_COLORS[i % ROUTE_COLORS.length]
                return route.stops.map((stop) => (
                  <MarkerF
                    key={stop.orderId}
                    {...registerMarker(route.id)}
                    position={{ lat: stop.latitude, lng: stop.longitude }}
                    label={{ text: String(stop.sequence), color: '#fff', fontWeight: 'bold', fontSize: '11px' }}
                    icon={{
                      path: google.maps.SymbolPath.CIRCLE,
                      fillColor: color,
                      fillOpacity: 1,
                      strokeColor: '#fff',
                      strokeWeight: 2,
                      scale: 14,
                    }}
                    onClick={() => setSelectedStop(stop)}
                  />
                ))
              })}

              {/* Truck markers (simulated position). Registered under the
                  truck's routeId so the same detach-all-then-reattach pass
                  handles them — hiding a route also pulls its truck. */}
              {isToday && liveMode && truckPositions.map((truck: any) => {
                if (!truck) return null
                const color = ROUTE_COLORS[truck.routeIndex % ROUTE_COLORS.length]
                return (
                  <MarkerF
                    key={`truck-${truck.routeId}`}
                    {...registerMarker(truck.routeId)}
                    position={{ lat: truck.lat, lng: truck.lng }}
                    icon={{
                      path: 'M 0,-8 L 6,8 L -6,8 Z',
                      fillColor: color,
                      fillOpacity: 1,
                      strokeColor: '#fff',
                      strokeWeight: 2,
                      scale: 1.5,
                      anchor: new google.maps.Point(0, 0),
                    }}
                    zIndex={1000}
                    title={`Route ${truck.routeIndex + 1} - Stop ${truck.currentStopIndex + 1}`}
                  />
                )
              })}

              {/* Info window */}
              {selectedStop && (
                <InfoWindow
                  position={{ lat: selectedStop.latitude, lng: selectedStop.longitude }}
                  onCloseClick={() => setSelectedStop(null)}
                >
                  <div className="text-sm space-y-1 min-w-[200px]">
                    <p className="font-semibold">{selectedStop.storeName}</p>
                    <p className="text-gray-500 text-xs">{selectedStop.address}</p>
                    {selectedStop.estimatedArrival && (
                      <p className="text-blue-600 font-medium">
                        ETA: {format(new Date(selectedStop.estimatedArrival), 'h:mm a')}
                      </p>
                    )}
                    <p className="text-xs">
                      {selectedStop.legDistanceMiles.toFixed(1)} mi leg
                    </p>
                    <ConfirmBadge status={selectedStop.confirmationStatus} />
                  </div>
                </InfoWindow>
              )}
            </GoogleMap>
          ) : (
            <div className="flex items-center justify-center h-full bg-gray-50 text-gray-400">
              {GOOGLE_MAPS_KEY ? 'Loading map...' : 'Set VITE_GOOGLE_MAPS_KEY to display the map.'}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

function fitBounds(map: google.maps.Map, routes: Route[]) {
  const bounds = new google.maps.LatLngBounds()
  let hasPoints = false

  // Defensive: an un-geocoded entity persists as (0, 0) — extending the
  // bounds with that point either drags the camera to the Atlantic or, if
  // ALL points are 0/0, collapses the bounds so fitBounds silently no-ops
  // and the map stays at its initial Illinois centroid. Treat exactly
  // 0/0 as "no real coord" everywhere.
  const isReal = (lat: number | null | undefined, lng: number | null | undefined): boolean =>
    lat != null && lng != null && !(lat === 0 && lng === 0)

  for (const route of routes) {
    // Include the hub itself so its marker is always visible — otherwise
    // a route whose deliveries cluster far from the depot can leave the
    // hub off-screen at the initial zoom.
    if (isReal(route.startLatitude, route.startLongitude)) {
      bounds.extend({ lat: route.startLatitude, lng: route.startLongitude })
      hasPoints = true
    }
    if (isReal(route.warehousePickup?.latitude, route.warehousePickup?.longitude)) {
      bounds.extend({ lat: route.warehousePickup!.latitude!, lng: route.warehousePickup!.longitude! })
      hasPoints = true
    }
    for (const stop of route.stops) {
      if (!isReal(stop.latitude, stop.longitude)) continue
      bounds.extend({ lat: stop.latitude, lng: stop.longitude })
      hasPoints = true
    }
  }
  if (hasPoints) map.fitBounds(bounds, 50)
}

function ConfirmBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    Confirmed: 'bg-green-100 text-green-700',
    SentEmail: 'bg-blue-100 text-blue-600',
    Rejected: 'bg-red-100 text-red-600',
    Pending: 'bg-gray-100 text-gray-500',
  }
  return (
    <span className={`inline-block px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] ?? 'bg-gray-100 text-gray-500'}`}>
      {status}
    </span>
  )
}

// ---- Sidebar expand rows ----
// Compact versions of the Logistics StopEditor rows, sized for the w-72
// route sidebar in this tracker. Read-only: no reorder buttons, no manual
// confirm — those belong in Logistics where the operator is editing.

function MiniDepotRow({ kind, address, subtitle, timeIso, timeLabel }: {
  kind: 'start' | 'end'
  address: string
  subtitle?: string
  /** ISO timestamp — depart for the start row, arrive for the end row. */
  timeIso?: string | null
  timeLabel?: string
}) {
  const isStart = kind === 'start'
  const chip   = isStart ? 'bg-emerald-500' : 'bg-rose-500'
  const label  = isStart ? 'text-emerald-700' : 'text-rose-700'
  const tint   = isStart ? 'bg-emerald-50/40' : 'bg-rose-50/40'
  return (
    <div className={`px-3 py-2 flex items-start gap-2 ${tint}`}>
      <span className={`w-5 h-5 rounded ${chip} text-white text-[9px] font-bold flex items-center justify-center shrink-0`}>
        {isStart ? 'S' : 'E'}
      </span>
      <div className="flex-1 min-w-0">
        <p className={`text-[11px] font-semibold ${label}`}>
          {isStart ? 'Start · Depot' : 'End · Depot'}
        </p>
        <p className="text-[11px] text-gray-600 truncate">{address}</p>
        {subtitle && <p className="text-[10px] text-gray-400">{subtitle}</p>}
      </div>
      {timeIso && (
        <div className="text-right shrink-0">
          <p className="text-[11px] font-medium text-gray-700">{format(new Date(timeIso), 'HH:mm')}</p>
          {timeLabel && <p className="text-[9px] text-gray-400">{timeLabel}</p>}
        </div>
      )}
    </div>
  )
}

function MiniWarehousePickupRow({ name, address, timeIso }: {
  name: string
  address: string
  timeIso?: string | null
}) {
  return (
    <div className="px-3 py-2 flex items-start gap-2 bg-amber-50/40">
      <span className="w-5 h-5 rounded bg-amber-500 text-white text-[9px] font-bold flex items-center justify-center shrink-0">
        W
      </span>
      <div className="flex-1 min-w-0">
        <p className="text-[11px] font-semibold text-amber-700 truncate">Pickup · {name}</p>
        <p className="text-[11px] text-gray-600 truncate">{address}</p>
      </div>
      {timeIso && (
        <div className="text-right shrink-0">
          <p className="text-[11px] font-medium text-gray-700">{format(new Date(timeIso), 'HH:mm')}</p>
          <p className="text-[9px] text-gray-400">ETA</p>
        </div>
      )}
    </div>
  )
}

function MiniStopRow({ stop, onClick }: { stop: RouteStop; onClick: () => void }) {
  const statusColors: Record<string, string> = {
    Confirmed:           'bg-green-100 text-green-700',
    Rejected:            'bg-red-100 text-red-700',
    ConfirmationPending: 'bg-yellow-100 text-yellow-700',
    SentEmail:           'bg-blue-50 text-blue-600',
    Pending:             'bg-gray-50 text-gray-500',
    Expired:             'bg-red-50 text-red-400',
  }
  const status = stop.confirmationStatus as string
  return (
    <button
      type="button"
      onClick={(e) => { e.stopPropagation(); onClick() }}
      className="w-full text-left px-3 py-2 flex items-start gap-2 hover:bg-gray-50"
    >
      <span className="w-5 h-5 rounded-full bg-brand-100 text-brand-700 text-[10px] font-bold flex items-center justify-center shrink-0">
        {stop.sequence}
      </span>
      <div className="flex-1 min-w-0">
        <p className="text-[11px] font-medium text-gray-900 truncate">{stop.storeName}</p>
        <p className="text-[11px] text-gray-500 truncate">{stop.address}</p>
        <div className="flex flex-wrap items-center gap-x-2 mt-1 text-[10px]">
          {stop.estimatedArrival && (
            <span className="text-gray-400">ETA {format(new Date(stop.estimatedArrival), 'HH:mm')}</span>
          )}
          <span className="text-gray-400">{stop.legDistanceMiles.toFixed(1)} mi</span>
          <span className={`px-1.5 py-0.5 rounded-full font-medium ${statusColors[status] ?? 'bg-gray-100 text-gray-500'}`}>
            {status.replace(/([A-Z])/g, ' $1').trim()}
          </span>
        </div>
      </div>
    </button>
  )
}
