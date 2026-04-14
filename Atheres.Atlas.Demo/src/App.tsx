import { useEffect, useState } from 'react'
import { login, getCompanies, getWarehouses, getStores, ingestOrders, readyToPickup } from './api'
import type { Warehouse, Store } from './api'
import { format, addDays, eachDayOfInterval } from 'date-fns'

function randomItem<T>(arr: T[]): T {
  return arr[Math.floor(Math.random() * arr.length)]
}

export default function App() {
  const [loggedIn, setLoggedIn] = useState(!!localStorage.getItem('demo_access_token'))
  const [loginError, setLoginError] = useState('')

  async function handleLogin(e: React.FormEvent<HTMLFormElement>) {
    e.preventDefault()
    setLoginError('')
    const form = new FormData(e.currentTarget)
    try {
      const res = await login(form.get('email') as string, form.get('password') as string)
      localStorage.setItem('demo_access_token', res.accessToken)
      localStorage.setItem('demo_refresh_token', res.refreshToken)
      setLoggedIn(true)
    } catch {
      setLoginError('Invalid credentials.')
    }
  }

  function handleLogout() {
    localStorage.removeItem('demo_access_token')
    localStorage.removeItem('demo_refresh_token')
    setLoggedIn(false)
  }

  if (!loggedIn) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <form onSubmit={handleLogin} className="bg-white rounded-xl border border-gray-200 p-8 w-96 space-y-4">
          <div className="text-center">
            <h1 className="text-xl font-bold text-gray-900">Atlas Demo</h1>
            <p className="text-sm text-gray-500 mt-1">Sign in with SuperAdmin credentials</p>
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Email</label>
            <input name="email" type="email" required
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
          </div>
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-1">Password</label>
            <input name="password" type="password" required
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
          </div>
          {loginError && <p className="text-sm text-red-600">{loginError}</p>}
          <button type="submit"
            className="w-full px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600">
            Sign In
          </button>
        </form>
      </div>
    )
  }

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white border-b border-gray-200 shadow-sm">
        <div className="max-w-6xl mx-auto px-4 py-3 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <div className="w-7 h-7 bg-brand-500 rounded-lg flex items-center justify-center">
              <span className="text-white font-bold text-xs">A</span>
            </div>
            <span className="text-lg font-bold text-gray-900">Atlas Demo</span>
            <span className="text-xs bg-yellow-100 text-yellow-700 px-2 py-0.5 rounded-full font-medium">Demo Only</span>
          </div>
          <button onClick={handleLogout} className="text-xs text-gray-500 hover:text-gray-700 px-2 py-1 rounded hover:bg-gray-100">
            Sign out
          </button>
        </div>
      </header>
      <main className="max-w-6xl mx-auto px-4 py-6">
        <DemoPanel />
      </main>
    </div>
  )
}

function DemoPanel() {
  const [companies, setCompanies] = useState<{ id: string; name: string }[]>([])
  const [selectedCompany, setSelectedCompany] = useState('')
  const [warehouses, setWarehouses] = useState<Warehouse[]>([])
  const [selectedWarehouses, setSelectedWarehouses] = useState<Set<string>>(new Set())
  const [stores, setStores] = useState<Store[]>([])
  const [startDate, setStartDate] = useState(format(new Date(), 'yyyy-MM-dd'))
  const [endDate, setEndDate] = useState(format(addDays(new Date(), 5), 'yyyy-MM-dd'))
  const [ordersPerDay, setOrdersPerDay] = useState(10)
  const [loading, setLoading] = useState(false)
  const [result, setResult] = useState<{ accepted: number; days: number } | null>(null)
  const [error, setError] = useState('')

  // Ready to pickup state
  const [pickupWarehouse, setPickupWarehouse] = useState('')
  const [pickupDateTime, setPickupDateTime] = useState(format(new Date(), "yyyy-MM-dd'T'10:00"))
  const [pickupLoading, setPickupLoading] = useState(false)
  const [pickupResult, setPickupResult] = useState<{ batchId: string; orderCount: number; ordersQueuedForRouting: number } | null>(null)
  const [pickupError, setPickupError] = useState('')

  useEffect(() => {
    getCompanies().then(setCompanies).catch(() => {})
  }, [])

  useEffect(() => {
    if (!selectedCompany) { setWarehouses([]); setStores([]); return }
    Promise.all([getWarehouses(), getStores()])
      .then(([wh, st]) => {
        const filtered = wh.filter((w) => w.companyId === selectedCompany)
        setWarehouses(filtered)
        setSelectedWarehouses(new Set(filtered.map((w) => w.id)))
        setStores(st.filter((s) => s.licenseNumber))
      })
      .catch(() => {})
  }, [selectedCompany])

  function toggleWarehouse(id: string) {
    setSelectedWarehouses((prev) => {
      const next = new Set(prev)
      next.has(id) ? next.delete(id) : next.add(id)
      return next
    })
  }

  async function handleGenerate() {
    if (!selectedCompany) { setError('Select a company.'); return }
    if (selectedWarehouses.size === 0) { setError('Select at least one warehouse.'); return }
    if (stores.length === 0) { setError('No stores with license numbers found.'); return }
    setError(''); setResult(null); setLoading(true)
    try {
      const days = eachDayOfInterval({ start: new Date(startDate), end: new Date(endDate) })
      const warehouseList = warehouses.filter((w) => selectedWarehouses.has(w.id))
      let totalAccepted = 0
      for (const day of days) {
        const orders = []
        for (let i = 0; i < ordersPerDay; i++) {
          const wh = randomItem(warehouseList)
          const store = randomItem(stores)
          orders.push({
            companySlug: selectedCompany,
            warehouseLicenseNumber: wh.licenseNumber,
            storeLicenseNumber: store.licenseNumber,
            orderDate: format(day, "yyyy-MM-dd'T'00:00:00"),
            notes: `Demo: ${store.name} from ${wh.businessName}`,
          })
        }
        const res = await ingestOrders(orders)
        totalAccepted += res.accepted
      }
      setResult({ accepted: totalAccepted, days: days.length })
    } catch { setError('Failed to generate orders.') }
    finally { setLoading(false) }
  }

  async function handleReadyToPickup() {
    if (!pickupWarehouse) { setPickupError('Select a warehouse.'); return }
    setPickupError(''); setPickupResult(null); setPickupLoading(true)
    try {
      const wh = warehouses.find((w) => w.id === pickupWarehouse)
      if (!wh?.licenseNumber) { setPickupError('Warehouse has no license number.'); return }
      const res = await readyToPickup(selectedCompany, wh.licenseNumber, pickupDateTime)
      setPickupResult(res)
    } catch { setPickupError('Failed to trigger pickup.') }
    finally { setPickupLoading(false) }
  }

  return (
    <div className="space-y-8">
      {/* Company selection */}
      <div className="bg-white rounded-xl border border-gray-200 p-6">
        <label className="block text-sm font-medium text-gray-700 mb-1">Company</label>
        <select value={selectedCompany} onChange={(e) => setSelectedCompany(e.target.value)}
          className="w-full max-w-md px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400">
          <option value="">Select a company...</option>
          {companies.map((c) => <option key={c.id} value={c.id}>{c.name}</option>)}
        </select>
      </div>

      {selectedCompany && (
        <>
          {/* Section 1: Generate Orders */}
          <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-5">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">1. Generate Random Orders</h2>
              <p className="text-sm text-gray-500 mt-0.5">Creates orders with random store/warehouse pairings</p>
            </div>

            {warehouses.length > 0 && (
              <div>
                <div className="flex items-center justify-between mb-2">
                  <label className="text-sm font-medium text-gray-700">Warehouses</label>
                  <div className="flex gap-2">
                    <button onClick={() => setSelectedWarehouses(new Set(warehouses.map((w) => w.id)))}
                      className="text-xs text-brand-600 hover:underline">All</button>
                    <button onClick={() => setSelectedWarehouses(new Set())}
                      className="text-xs text-gray-400 hover:underline">None</button>
                  </div>
                </div>
                <div className="grid sm:grid-cols-2 lg:grid-cols-3 gap-2">
                  {warehouses.map((w) => (
                    <label key={w.id}
                      className={`flex items-start gap-2.5 p-3 rounded-lg border cursor-pointer transition-colors ${
                        selectedWarehouses.has(w.id) ? 'border-brand-400 bg-brand-50' : 'border-gray-200 hover:border-gray-300'
                      }`}>
                      <input type="checkbox" checked={selectedWarehouses.has(w.id)} onChange={() => toggleWarehouse(w.id)}
                        className="mt-0.5 rounded border-gray-300 text-brand-500 focus:ring-brand-400" />
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-gray-900 truncate">{w.businessName}</p>
                        {w.alternateName && <p className="text-xs text-gray-400 truncate">{w.alternateName}</p>}
                        {w.licenseNumber && <p className="text-xs text-gray-400">#{w.licenseNumber}</p>}
                      </div>
                    </label>
                  ))}
                </div>
              </div>
            )}

            <div className="grid sm:grid-cols-3 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Start Date</label>
                <input type="date" value={startDate} onChange={(e) => setStartDate(e.target.value)}
                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">End Date</label>
                <input type="date" value={endDate} onChange={(e) => setEndDate(e.target.value)}
                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Orders per day</label>
                <input type="number" min={1} max={100} value={ordersPerDay}
                  onChange={(e) => setOrdersPerDay(Math.max(1, Math.min(100, parseInt(e.target.value) || 1)))}
                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
              </div>
            </div>

            <div className="bg-gray-50 rounded-lg p-4 text-sm text-gray-600">
              Will generate <strong>{ordersPerDay}</strong> orders/day across <strong>{selectedWarehouses.size}</strong> warehouse{selectedWarehouses.size !== 1 ? 's' : ''} from <strong>{startDate}</strong> to <strong>{endDate}</strong>.
              <br /><span className="text-gray-400">{stores.length} stores available for pairing.</span>
            </div>

            <div className="flex items-center gap-3">
              <button onClick={handleGenerate} disabled={loading || !selectedCompany || selectedWarehouses.size === 0}
                className="px-5 py-2.5 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50 transition-colors">
                {loading ? 'Generating...' : 'Generate Orders'}
              </button>
              {error && <p className="text-sm text-red-600">{error}</p>}
              {result && <p className="text-sm text-green-600">Created {result.accepted} orders across {result.days} day{result.days !== 1 ? 's' : ''}.</p>}
            </div>
          </div>

          {/* Section 2: Trigger Ready to Pickup */}
          <div className="bg-white rounded-xl border border-gray-200 p-6 space-y-5">
            <div>
              <h2 className="text-lg font-semibold text-gray-900">2. Trigger Ready to Pickup</h2>
              <p className="text-sm text-gray-500 mt-0.5">Simulates a warehouse confirming orders are ready — creates a batch and triggers route optimization</p>
            </div>

            <div className="grid sm:grid-cols-2 gap-4">
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Warehouse</label>
                <select value={pickupWarehouse} onChange={(e) => setPickupWarehouse(e.target.value)}
                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400">
                  <option value="">Select warehouse...</option>
                  {warehouses.filter((w) => w.licenseNumber).map((w) => (
                    <option key={w.id} value={w.id}>{w.businessName} ({w.licenseNumber})</option>
                  ))}
                </select>
              </div>
              <div>
                <label className="block text-sm font-medium text-gray-700 mb-1">Pickup Date & Time</label>
                <input type="datetime-local" value={pickupDateTime} onChange={(e) => setPickupDateTime(e.target.value)}
                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
              </div>
            </div>

            <div className="flex items-center gap-3">
              <button onClick={handleReadyToPickup} disabled={pickupLoading || !pickupWarehouse}
                className="px-5 py-2.5 bg-green-600 text-white text-sm font-medium rounded-lg hover:bg-green-700 disabled:opacity-50 transition-colors">
                {pickupLoading ? 'Scheduling...' : 'Ready to Pickup'}
              </button>
              {pickupError && <p className="text-sm text-red-600">{pickupError}</p>}
              {pickupResult && (
                <p className="text-sm text-green-600">
                  Batch created: {pickupResult.orderCount} orders, {pickupResult.ordersQueuedForRouting} queued for routing.
                </p>
              )}
            </div>
          </div>
        </>
      )}
    </div>
  )
}
