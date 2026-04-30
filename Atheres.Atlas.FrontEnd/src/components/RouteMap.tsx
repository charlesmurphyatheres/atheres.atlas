import { useEffect, useState } from 'react'
import { GoogleMap, useJsApiLoader, Marker, InfoWindow } from '@react-google-maps/api'
import { getRoutes } from '../services/apiService'
import type { Route, RouteStop } from '../types'
import { format } from 'date-fns'
import { useSortedRows } from '../hooks/useSortedRows'
import { SortHeader } from './ui/SortHeader'

const GOOGLE_MAPS_KEY = import.meta.env.VITE_GOOGLE_MAPS_KEY ?? ''

const mapContainerStyle = { width: '100%', height: '500px' }
const defaultCenter = { lat: 39.7392, lng: -104.9903 } // Denver, CO

export default function RouteMap() {
  const [selectedDate, setSelectedDate] = useState(format(new Date(), 'yyyy-MM-dd'))
  const [routes, setRoutes] = useState<Route[]>([])
  const [selectedStop, setSelectedStop] = useState<RouteStop | null>(null)
  const [loading, setLoading] = useState(false)

  const { isLoaded } = useJsApiLoader({
    id: 'google-map-script',
    googleMapsApiKey: GOOGLE_MAPS_KEY,
  })

  useEffect(() => {
    setLoading(true)
    getRoutes(selectedDate)
      .then(setRoutes)
      .finally(() => setLoading(false))
  }, [selectedDate])

  const allStops = routes.flatMap((r) => r.stops)

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold text-gray-900">Route Map</h1>
        <input
          type="date"
          value={selectedDate}
          onChange={(e) => setSelectedDate(e.target.value)}
          className="border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand-500"
        />
      </div>

      {/* Route summary cards */}
      {routes.length > 0 && (
        <div className="grid gap-3 md:grid-cols-2 lg:grid-cols-3">
          {routes.map((r) => {
            const sameDepot =
              r.startAddress === r.endAddress &&
              r.startLatitude === r.endLatitude &&
              r.startLongitude === r.endLongitude
            return (
              <div key={r.id} className="bg-white rounded-lg border border-gray-200 p-4 text-sm">
                <div className="font-medium text-gray-800">{r.totalStops} stops</div>
                <div className="text-gray-500 mt-1">
                  {r.totalDistanceMiles.toFixed(1)} mi · {r.totalDuration}
                </div>
                <div className="mt-2 space-y-1 text-xs">
                  <div className="flex items-start gap-1.5 text-gray-600">
                    <span className="inline-block w-4 h-4 rounded bg-emerald-500 text-white text-[9px] font-bold flex-shrink-0 flex items-center justify-center mt-0.5">S</span>
                    <span className="truncate">{r.startAddress}</span>
                  </div>
                  <div className="flex items-start gap-1.5 text-gray-600">
                    <span className="inline-block w-4 h-4 rounded bg-rose-500 text-white text-[9px] font-bold flex-shrink-0 flex items-center justify-center mt-0.5">E</span>
                    <span className="truncate">
                      {sameDepot ? <em className="not-italic text-gray-400">returns to start</em> : r.endAddress}
                    </span>
                  </div>
                </div>
              </div>
            )
          })}
        </div>
      )}

      {/* Map */}
      <div className="rounded-xl border border-gray-200 overflow-hidden">
        {isLoaded ? (
          <GoogleMap mapContainerStyle={mapContainerStyle} center={defaultCenter} zoom={11}>
            {/* Depot markers: green "S" for the start of each route, red "E"
                for its end (same coords when the route returns to the home hub).
                Square shape distinguishes them from the round numbered stop pins. */}
            {routes.flatMap((r) => {
              const sameDepot =
                r.startLatitude === r.endLatitude && r.startLongitude === r.endLongitude
              const markers = [
                <Marker
                  key={`${r.id}-start`}
                  position={{ lat: r.startLatitude, lng: r.startLongitude }}
                  label={{ text: 'S', color: 'white', fontWeight: 'bold', fontSize: '12px' }}
                  icon={{
                    path: 'M -12,-12 12,-12 12,12 -12,12 z',
                    fillColor: '#10b981',
                    fillOpacity: 1,
                    strokeColor: '#047857',
                    strokeWeight: 2,
                    scale: 1,
                  }}
                  title={`Route ${r.id.slice(0, 8)} · Start: ${r.startAddress}`}
                  zIndex={1000}
                />,
              ]
              if (!sameDepot) {
                markers.push(
                  <Marker
                    key={`${r.id}-end`}
                    position={{ lat: r.endLatitude, lng: r.endLongitude }}
                    label={{ text: 'E', color: 'white', fontWeight: 'bold', fontSize: '12px' }}
                    icon={{
                      path: 'M -12,-12 12,-12 12,12 -12,12 z',
                      fillColor: '#f43f5e',
                      fillOpacity: 1,
                      strokeColor: '#9f1239',
                      strokeWeight: 2,
                      scale: 1,
                    }}
                    title={`Route ${r.id.slice(0, 8)} · End: ${r.endAddress}`}
                    zIndex={1000}
                  />
                )
              }
              return markers
            })}

            {allStops.map((stop) => (
              <Marker
                key={stop.orderId}
                position={{ lat: stop.latitude, lng: stop.longitude }}
                label={{ text: String(stop.sequence), color: 'white', fontWeight: 'bold' }}
                onClick={() => setSelectedStop(stop)}
              />
            ))}

            {selectedStop && (
              <InfoWindow
                position={{ lat: selectedStop.latitude, lng: selectedStop.longitude }}
                onCloseClick={() => setSelectedStop(null)}
              >
                <div className="text-sm space-y-1 min-w-[180px]">
                  <p className="font-semibold">{selectedStop.storeName}</p>
                  <p className="text-gray-500">{selectedStop.address}</p>
                  {selectedStop.estimatedArrival && (
                    <p className="text-blue-600">
                      ETA: {format(new Date(selectedStop.estimatedArrival), 'h:mm a')}
                    </p>
                  )}
                  <p>
                    <ConfirmBadge status={selectedStop.confirmationStatus} />
                  </p>
                </div>
              </InfoWindow>
            )}
          </GoogleMap>
        ) : (
          <div className="flex items-center justify-center h-64 bg-gray-100 text-gray-400">
            {GOOGLE_MAPS_KEY ? 'Loading map...' : 'Set VITE_GOOGLE_MAPS_KEY to display the map.'}
          </div>
        )}
      </div>

      {/* Stop list */}
      {allStops.length > 0 && <StopsTable stops={allStops} onSelect={setSelectedStop} />}

      {!loading && routes.length === 0 && (
        <div className="text-center py-12 text-gray-400">No routes found for {selectedDate}.</div>
      )}
    </div>
  )
}

function StopsTable({ stops, onSelect }: { stops: RouteStop[]; onSelect: (s: RouteStop) => void }) {
  const { sorted, sortKey, sortDir, toggle } = useSortedRows(stops, {
    initial: { key: 'sequence', dir: 'asc' },
    accessors: { legDistance: (s) => s.legDistanceMiles },
  })
  return (
    <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
      <table className="w-full text-sm">
        <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
          <tr>
            <SortHeader label="#"            sortKey="sequence"           activeKey={sortKey} dir={sortDir} onClick={() => toggle('sequence')} />
            <SortHeader label="Store"        sortKey="storeName"          activeKey={sortKey} dir={sortDir} onClick={() => toggle('storeName')} />
            <SortHeader label="Address"      sortKey="address"            activeKey={sortKey} dir={sortDir} onClick={() => toggle('address')} />
            <SortHeader label="ETA"          sortKey="estimatedArrival"   activeKey={sortKey} dir={sortDir} onClick={() => toggle('estimatedArrival')} />
            <SortHeader label="Leg Distance" sortKey="legDistance"        activeKey={sortKey} dir={sortDir} onClick={() => toggle('legDistance')} />
            <SortHeader label="Confirmation" sortKey="confirmationStatus" activeKey={sortKey} dir={sortDir} onClick={() => toggle('confirmationStatus')} />
          </tr>
        </thead>
        <tbody className="divide-y divide-gray-100">
          {sorted.map((stop) => (
            <tr
              key={stop.orderId}
              className="hover:bg-gray-50 cursor-pointer"
              onClick={() => onSelect(stop)}
            >
              <td className="px-4 py-3 font-bold text-brand-500">{stop.sequence}</td>
              <td className="px-4 py-3 font-medium text-gray-900">{stop.storeName}</td>
              <td className="px-4 py-3 text-gray-500 truncate max-w-[200px]">{stop.address}</td>
              <td className="px-4 py-3 text-gray-600">
                {stop.estimatedArrival ? format(new Date(stop.estimatedArrival), 'h:mm a') : '—'}
              </td>
              <td className="px-4 py-3 text-gray-500">{stop.legDistanceMiles.toFixed(1)} mi</td>
              <td className="px-4 py-3">
                <ConfirmBadge status={stop.confirmationStatus} />
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function ConfirmBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    Confirmed: 'bg-green-100 text-green-700',
    SentEmail: 'bg-blue-100 text-blue-600',
    Rejected: 'bg-red-100 text-red-600',
    Pending: 'bg-gray-100 text-gray-500',
    Expired: 'bg-red-100 text-red-600',
    Failed: 'bg-red-100 text-red-600',
  }
  const labels: Record<string, string> = {
    SentEmail: 'Email Sent',
    Confirmed: 'Confirmed',
    Rejected: 'Rejected',
    Pending: 'Pending',
    Expired: 'Expired',
    Failed: 'Failed',
  }
  return (
    <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] ?? 'bg-gray-100 text-gray-500'}`}>
      {labels[status] ?? status}
    </span>
  )
}
