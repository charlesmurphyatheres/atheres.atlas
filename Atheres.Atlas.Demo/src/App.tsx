import { useEffect, useState } from 'react'
import { getCompanies, getWarehouses, getStores, ingestOrder, markAllReady, resetAllOrders } from './api'
import type { Warehouse, Store } from './api'
import { format, addDays, subDays } from 'date-fns'

const PRODUCTS = [
  { sku: 'THC-GUMMY-100', name: 'THC Gummies 100mg', category: 'Edibles' },
  { sku: 'CBD-TINCTURE-30', name: 'CBD Tincture 30ml', category: 'Tinctures' },
  { sku: 'PRE-ROLL-5PK', name: 'Pre-Roll 5-Pack', category: 'Flower' },
  { sku: 'VAPE-CART-1G', name: 'Vape Cartridge 1g', category: 'Vapes' },
  { sku: 'LIVE-RESIN-05', name: 'Live Resin 0.5g', category: 'Concentrates' },
  { sku: 'TOPICAL-BALM', name: 'CBD Topical Balm', category: 'Topicals' },
  { sku: 'EDIBLE-CHOC-50', name: 'Chocolate Bar 50mg', category: 'Edibles' },
  { sku: 'FLOWER-3G-IND', name: 'Indica Flower 3.5g', category: 'Flower' },
  { sku: 'FLOWER-3G-SAT', name: 'Sativa Flower 3.5g', category: 'Flower' },
  { sku: 'DISP-VAPE-300', name: 'Disposable Vape 300mg', category: 'Vapes' },
]

function randomItems() {
  const count = Math.floor(Math.random() * 4) + 1
  const shuffled = [...PRODUCTS].sort(() => Math.random() - 0.5)
  return shuffled.slice(0, count).map((p) => ({ ...p, quantity: Math.floor(Math.random() * 48) + 1 }))
}

export default function App() {
  const [companies, setCompanies] = useState<{ id: string; name: string }[]>([])
  const [warehouses, setWarehouses] = useState<Warehouse[]>([])
  const [stores, setStores] = useState<Store[]>([])

  const [company, setCompany] = useState('')
  const [warehouse, setWarehouse] = useState('')
  const [store, setStore] = useState('')
  const [count, setCount] = useState(5)
  const [date, setDate] = useState(format(new Date(), 'yyyy-MM-dd'))

  const [busy, setBusy] = useState(false)
  const [msg, setMsg] = useState('')
  const [err, setErr] = useState('')

  useEffect(() => { getCompanies().then(setCompanies).catch(() => {}) }, [])

  useEffect(() => {
    if (!company) { setWarehouses([]); setStores([]); return }
    Promise.all([getWarehouses(), getStores()])
      .then(([wh, st]) => {
        const f = wh.filter((w) => w.companyId === company)
        setWarehouses(f)
        setWarehouse(f[0]?.id ?? '')
        setStores(st.filter((s) => s.licenseNumber))
        setStore('')
      })
      .catch(() => {})
  }, [company])

  async function generate() {
    if (!company || !warehouse) return
    const wh = warehouses.find((w) => w.id === warehouse)!
    const pool = store ? stores.filter((s) => s.id === store) : stores
    if (pool.length === 0) return

    setBusy(true); setMsg(''); setErr('')
    let created = 0
    try {
      for (let i = 0; i < count; i++) {
        const s = pool[Math.floor(Math.random() * pool.length)]
        await ingestOrder({
          companySlug: company,
          warehouseLicenseNumber: wh.licenseNumber,
          storeLicenseNumber: s.licenseNumber,
          orderDate: `${date}T00:00:00`,
          items: randomItems(),
        })
        created++
      }
      setMsg(`${created} orders created`)
    } catch { setErr(`Failed after ${created}`) }
    finally { setBusy(false) }
  }

  async function reset() {
    if (!confirm('Delete ALL orders, routes, and products?')) return
    setBusy(true); setMsg(''); setErr('')
    try { await resetAllOrders(); setMsg('All data wiped') }
    catch { setErr('Reset failed') }
    finally { setBusy(false) }
  }

  async function markReady() {
    if (!company) return
    setBusy(true); setMsg(''); setErr('')
    try {
      const result = await markAllReady(company, date)
      if (result.totalOrders === 0) {
        setMsg('No orders ready to pickup.')
      } else {
        setMsg(`Queued ${result.routesQueued} route${result.routesQueued === 1 ? '' : 's'} for ${result.totalOrders} orders.`)
      }
    } catch (e: any) {
      setErr(e?.response?.data?.error ?? 'Mark-ready failed.')
    } finally { setBusy(false) }
  }

  return (
    <div className="min-h-screen bg-gray-50 flex flex-col">
      {/* Header */}
      <div className="bg-brand-500 text-white px-4 py-3 text-center font-bold text-lg">
        Atlas Simulator
      </div>

      <div className="flex-1 px-4 py-4 space-y-3 max-w-lg mx-auto w-full">
        {/* Company */}
        <Select label="Company" value={company} onChange={setCompany}
          options={companies.map((c) => ({ value: c.id, label: c.name }))} placeholder="Select..." />

        {/* Warehouse */}
        <Select label="Warehouse" value={warehouse} onChange={setWarehouse} disabled={!company}
          options={warehouses.filter((w) => w.licenseNumber).map((w) => ({ value: w.id, label: w.businessName }))} placeholder="Select..." />

        {/* Store */}
        <Select label="Store" value={store} onChange={setStore} disabled={!company}
          options={[{ value: '', label: 'Random' }, ...stores.map((s) => ({ value: s.id, label: `${s.name} - ${s.city}` }))]} />

        {/* Count */}
        <div>
          <label className="block text-xs font-semibold text-gray-500 uppercase tracking-wide mb-1">Orders</label>
          <div className="flex items-center gap-0">
            <button onClick={() => setCount((c) => Math.max(1, c - 1))}
              className="h-11 w-12 rounded-l-lg bg-white border border-gray-300 text-xl font-bold text-gray-500 active:bg-gray-100">-</button>
            <input type="number" value={count} min={1} max={500}
              onChange={(e) => setCount(Math.max(1, parseInt(e.target.value) || 1))}
              className="h-11 w-16 text-center border-y border-gray-300 text-lg font-semibold focus:outline-none" />
            <button onClick={() => setCount((c) => Math.min(500, c + 1))}
              className="h-11 w-12 rounded-r-lg bg-white border border-gray-300 text-xl font-bold text-gray-500 active:bg-gray-100">+</button>
            <div className="flex gap-1 ml-3">
              {[5, 10, 25, 50].map((n) => (
                <button key={n} onClick={() => setCount(n)}
                  className={`h-8 px-2.5 rounded-full text-xs font-semibold ${count === n ? 'bg-brand-500 text-white' : 'bg-gray-200 text-gray-600 active:bg-gray-300'}`}>{n}</button>
              ))}
            </div>
          </div>
        </div>

        {/* Date */}
        <div>
          <label className="block text-xs font-semibold text-gray-500 uppercase tracking-wide mb-1">Date</label>
          <div className="flex items-center gap-0">
            <button onClick={() => setDate(format(subDays(new Date(date), 1), 'yyyy-MM-dd'))}
              className="h-11 w-12 rounded-l-lg bg-white border border-gray-300 text-xl text-gray-400 active:bg-gray-100">&#8249;</button>
            <input type="date" value={date} onChange={(e) => setDate(e.target.value)}
              className="h-11 flex-1 text-center border-y border-gray-300 text-sm font-medium focus:outline-none" />
            <button onClick={() => setDate(format(addDays(new Date(date), 1), 'yyyy-MM-dd'))}
              className="h-11 w-12 rounded-r-lg bg-white border border-gray-300 text-xl text-gray-400 active:bg-gray-100">&#8250;</button>
          </div>
        </div>

        {/* Generate */}
        <button onClick={generate} disabled={busy || !company || !warehouse}
          className="w-full h-14 bg-brand-500 text-white text-base font-bold rounded-xl active:bg-brand-700 disabled:opacity-40 transition-colors">
          {busy ? 'Working...' : `Generate ${count} Order${count !== 1 ? 's' : ''}`}
        </button>

        {/* Mark all ready to pickup — also kicks off routing */}
        <button onClick={markReady} disabled={busy || !company}
          className="w-full h-14 bg-emerald-600 text-white text-base font-bold rounded-xl active:bg-emerald-800 disabled:opacity-40 transition-colors">
          {busy ? 'Working...' : 'Mark All Ready to Pickup'}
        </button>
        <p className="text-center text-[11px] text-gray-400 -mt-1">Flips every Ordered order for this date to Scheduled and generates routes.</p>

        {/* Feedback */}
        {msg && <p className="text-center text-sm font-medium text-green-600">{msg}</p>}
        {err && <p className="text-center text-sm font-medium text-red-600">{err}</p>}

        {/* Spacer */}
        <div className="pt-6" />

        {/* Reset */}
        <button onClick={reset} disabled={busy}
          className="w-full h-14 bg-red-600 text-white text-base font-bold rounded-xl active:bg-red-800 disabled:opacity-40 transition-colors">
          {busy ? 'Working...' : 'Reset All Orders'}
        </button>
        <p className="text-center text-[11px] text-gray-400">Deletes all orders, routes, and products</p>
      </div>
    </div>
  )
}

function Select({ label, value, onChange, options, placeholder, disabled }: {
  label: string
  value: string
  onChange: (v: string) => void
  options: { value: string; label: string }[]
  placeholder?: string
  disabled?: boolean
}) {
  return (
    <div>
      <label className="block text-xs font-semibold text-gray-500 uppercase tracking-wide mb-1">{label}</label>
      <select value={value} onChange={(e) => onChange(e.target.value)} disabled={disabled}
        className="w-full h-11 px-3 text-sm bg-white border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400 disabled:bg-gray-100 disabled:text-gray-400">
        {placeholder && <option value="">{placeholder}</option>}
        {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    </div>
  )
}
