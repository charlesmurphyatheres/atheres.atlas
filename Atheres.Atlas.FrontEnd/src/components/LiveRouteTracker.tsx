import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { GoogleMap, useJsApiLoader, Marker, Polyline, InfoWindow } from '@react-google-maps/api'
import { getRoutes } from '../services/apiService'
import type { Route, RouteStop } from '../types'
import { format } from 'date-fns'

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
  const [now, setNow] = useState(new Date())
  const [liveMode, setLiveMode] = useState(true)
  const mapRef = useRef<google.maps.Map | null>(null)

  const { isLoaded } = useJsApiLoader({
    id: 'google-map-script',
    googleMapsApiKey: GOOGLE_MAPS_KEY,
  })

  useEffect(() => {
    setLoading(true)
    getRoutes(selectedDate)
      .then((r) => { setRoutes(r); setSelectedRoute(null) })
      .finally(() => setLoading(false))
  }, [selectedDate])

  // Tick every 10 seconds for live simulation
  useEffect(() => {
    if (!liveMode) return
    const interval = setInterval(() => setNow(new Date()), 10000)
    return () => clearInterval(interval)
  }, [liveMode])

  const isToday = selectedDate === format(new Date(), 'yyyy-MM-dd')

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

  // Fit bounds to all stops
  const onMapLoad = useCallback((map: google.maps.Map) => {
    mapRef.current = map
    fitBounds(map, routes)
  }, [routes])

  useEffect(() => {
    if (mapRef.current && routes.length > 0) fitBounds(mapRef.current, routes)
  }, [routes])

  const visibleRoutes = selectedRoute ? routes.filter((r) => r.id === selectedRoute) : routes

  return (
    <div className="flex flex-col h-[calc(100vh-8rem)]">
      {/* Header bar */}
      <div className="flex items-center justify-between px-1 pb-3 shrink-0">
        <div className="flex items-center gap-3">
          <h1 className="text-xl font-bold text-gray-900">Live Routes</h1>
          <input type="date" value={selectedDate} onChange={(e) => setSelectedDate(e.target.value)}
            className="text-sm border border-gray-300 rounded-lg px-2 py-1 focus:outline-none focus:ring-2 focus:ring-brand-400" />
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
          {routes.length} route{routes.length !== 1 ? 's' : ''} · {routes.reduce((s, r) => s + r.totalStops, 0)} stops
        </span>
      </div>

      <div className="flex flex-1 gap-3 min-h-0">
        {/* Sidebar */}
        <div className="w-72 shrink-0 flex flex-col gap-2 overflow-y-auto">
          <button onClick={() => setSelectedRoute(null)}
            className={`text-left text-xs px-3 py-2 rounded-lg transition-colors ${
              !selectedRoute ? 'bg-brand-50 text-brand-700 font-medium' : 'text-gray-500 hover:bg-gray-50'
            }`}>
            All Routes
          </button>
          {routes.map((route, i) => {
            const color = ROUTE_COLORS[i % ROUTE_COLORS.length]
            const truckPos = truckPositions.find((t: any) => t?.routeId === route.id)
            const completedStops = truckPos ? truckPos.currentStopIndex : 0
            return (
              <button key={route.id} onClick={() => setSelectedRoute(route.id === selectedRoute ? null : route.id)}
                className={`text-left p-3 rounded-lg border transition-colors ${
                  selectedRoute === route.id ? 'border-gray-300 bg-white shadow-sm' : 'border-gray-100 hover:border-gray-200 bg-white'
                }`}>
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
                </div>
                <div className="text-xs text-gray-500 mt-1">
                  {route.totalStops} stops · {route.totalDistanceMiles.toFixed(1)} mi · {route.totalDuration}
                </div>
                <div className="flex flex-wrap gap-1 mt-2">
                  {route.warehousePickup && (
                    <span
                      title={`Pickup at ${route.warehousePickup.name}`}
                      className="text-[10px] px-1.5 py-0.5 rounded bg-amber-100 text-amber-700 font-medium"
                    >
                      W
                    </span>
                  )}
                  {route.stops.slice().sort((a, b) => a.sequence - b.sequence).slice(0, 6).map((s) => (
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
              </button>
            )
          })}
          {routes.length === 0 && !loading && (
            <p className="text-sm text-gray-400 p-3">No routes for this date.</p>
          )}
        </div>

        {/* Map */}
        <div className="flex-1 rounded-xl border border-gray-200 overflow-hidden">
          {isLoaded ? (
            <GoogleMap
              mapContainerStyle={{ width: '100%', height: '100%' }}
              center={{ lat: 40.0, lng: -89.0 }}
              zoom={7}
              onLoad={onMapLoad}
              options={{ streetViewControl: false, mapTypeControl: false }}
            >
              {/* Polylines */}
              {visibleRoutes.map((route) => {
                const globalIdx = routes.indexOf(route)
                const color = ROUTE_COLORS[globalIdx % ROUTE_COLORS.length]
                if (!route.overviewPolyline) return null
                return (
                  <Polyline
                    key={route.id}
                    path={decodePolyline(route.overviewPolyline)}
                    options={{
                      strokeColor: color,
                      strokeOpacity: selectedRoute && selectedRoute !== route.id ? 0.2 : 0.8,
                      strokeWeight: selectedRoute === route.id ? 5 : 3,
                    }}
                  />
                )
              })}

              {/* Hub markers (square, slate) — the depot the route starts
                  and returns to. Latitude/longitude are persisted on the
                  route so the same coords drive the marker as draw the
                  polyline endpoints. */}
              {visibleRoutes.flatMap((route) => {
                if (!route.startLatitude || !route.startLongitude) return []
                return [(
                  <Marker
                    key={`hub-${route.id}`}
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
              {visibleRoutes.flatMap((route) => {
                if (!route.warehousePickup
                    || route.warehousePickup.latitude == null
                    || route.warehousePickup.longitude == null) return []
                return [(
                  <Marker
                    key={`wh-${route.id}`}
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
              {visibleRoutes.flatMap((route) => {
                const globalIdx = routes.indexOf(route)
                const color = ROUTE_COLORS[globalIdx % ROUTE_COLORS.length]
                return route.stops.map((stop) => (
                  <Marker
                    key={stop.orderId}
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

              {/* Truck markers (simulated position) */}
              {isToday && liveMode && truckPositions.map((truck: any) => {
                if (!truck) return null
                const visible = !selectedRoute || selectedRoute === truck.routeId
                if (!visible) return null
                const color = ROUTE_COLORS[truck.routeIndex % ROUTE_COLORS.length]
                return (
                  <Marker
                    key={`truck-${truck.routeId}`}
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
  for (const route of routes) {
    // Include the hub itself so its marker is always visible — otherwise
    // a route whose deliveries cluster far from the depot can leave the
    // hub off-screen at the initial zoom.
    if (route.startLatitude && route.startLongitude) {
      bounds.extend({ lat: route.startLatitude, lng: route.startLongitude })
      hasPoints = true
    }
    if (route.warehousePickup?.latitude != null && route.warehousePickup?.longitude != null) {
      bounds.extend({ lat: route.warehousePickup.latitude, lng: route.warehousePickup.longitude })
      hasPoints = true
    }
    for (const stop of route.stops) {
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
