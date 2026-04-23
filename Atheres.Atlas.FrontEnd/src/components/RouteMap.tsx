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
          {routes.map((r) => (
            <div key={r.id} className="bg-white rounded-lg border border-gray-200 p-4 text-sm">
              <div className="font-medium text-gray-800">{r.totalStops} stops</div>
              <div className="text-gray-500 mt-1">
                {r.totalDistanceMiles.toFixed(1)} mi · {r.totalDuration}
              </div>
              <div className="text-gray-400 text-xs mt-1 truncate">{r.startAddress}</div>
            </div>
          ))}
        </div>
      )}

      {/* Map */}
      <div className="rounded-xl border border-gray-200 overflow-hidden">
        {isLoaded ? (
          <GoogleMap mapContainerStyle={mapContainerStyle} center={defaultCenter} zoom={11}>
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
