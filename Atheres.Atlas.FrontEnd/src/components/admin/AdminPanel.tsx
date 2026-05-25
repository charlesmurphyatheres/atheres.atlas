import { useEffect, useMemo, useState } from 'react'
import { getUsers, registerUser, deactivateUser, getOrders, updateOrderStatus, bulkUpdateOrderStatus, getRoutes, getWarehouses, createWarehouse, updateWarehouse, deleteWarehouse, getHubs, createHub, updateHub, deleteHub, getTrucks, createTruck, updateTruck, deleteTruck, getStores, updateStore, getCompanies, deleteOrder, deleteRoute, getOptimizationAudits, getOptimizationAudit, printRouteItinerary, printOptimizationAudit, type StoreLite, type UpdateStorePayload, type OptimizationAuditSummary, type OptimizationAuditDetail } from '../../services/apiService'
import BulkStatusBar from '../ui/BulkStatusBar'
import { summarizeBulkStatusResult } from '../ui/bulkStatusSummary'
import { TRUCK_STATUSES, type TruckStatus } from '../../types'
import { useSortedRows } from '../../hooks/useSortedRows'
import { SortHeader } from '../ui/SortHeader'
import type { AppUser, Order, Route, Role, Warehouse, Hub, Truck } from '../../types'
import { format } from 'date-fns'
import { useAuth } from '../../contexts/AuthContext'
import { useCompanyContext } from '../../contexts/CompanyContext'

type Tab = 'users' | 'orders' | 'routes' | 'warehouses' | 'hubs' | 'vans' | 'stores' | 'audits'

/** Props every tab accepts so it can render a Company column when a
 *  SuperAdmin is viewing "All Companies". `companyName(id)` returns the
 *  display name for a row's companyId, or "—" if the company is unknown
 *  (e.g. global accounts with no companyId). When `showCompany` is false
 *  tabs render their original layout untouched. */
type CompanyDisplay = {
  showCompany: boolean
  companyName: (id?: string | null) => string
}

export default function AdminPanel() {
  const { user, isRole } = useAuth()
  const { activeCompanyId } = useCompanyContext()
  const [tab, setTab] = useState<Tab>('orders')

  // "All Companies" mode is only meaningful for SuperAdmins who have not
  // chosen a specific company in the company picker. Everyone else sees
  // exactly one company's data, so the extra column is just noise.
  const showCompany = isRole('SuperAdmin') && !activeCompanyId

  const [companies, setCompanies] = useState<{ id: string; name: string }[]>([])
  useEffect(() => {
    if (!showCompany) return
    getCompanies()
      .then((cs) => setCompanies(cs.map((c) => ({ id: c.id, name: c.name }))))
      .catch(() => setCompanies([]))
  }, [showCompany])

  const companyName = useMemo(() => {
    const map = new Map(companies.map((c) => [c.id, c.name]))
    return (id?: string | null) => (id ? (map.get(id) ?? '—') : '—')
  }, [companies])

  const display: CompanyDisplay = { showCompany, companyName }

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Admin Panel</h1>
          <p className="text-sm text-gray-500 mt-0.5">{user?.companyName ?? 'All Companies'}</p>
        </div>
      </div>

      <div className="flex gap-1 border-b border-gray-200">
        {(['orders', 'routes', 'hubs', 'vans', 'warehouses', 'stores', 'users', 'audits'] as Tab[]).map((t) => (
          <button
            key={t}
            onClick={() => setTab(t)}
            className={`px-4 py-2 text-sm font-medium capitalize border-b-2 -mb-px transition-colors ${
              tab === t
                ? 'border-brand-500 text-brand-600'
                : 'border-transparent text-gray-500 hover:text-gray-700'
            }`}
          >
            {t}
          </button>
        ))}
      </div>

      {tab === 'orders' && <OrdersTab {...display} />}
      {tab === 'routes' && <RoutesTab {...display} />}
      {tab === 'hubs' && <HubsTab {...display} />}
      {tab === 'vans' && <VansTab {...display} />}
      {tab === 'warehouses' && <WarehousesTab {...display} />}
      {tab === 'stores' && <StoresTab {...display} />}
      {tab === 'users' && <UsersTab {...display} />}
      {tab === 'audits' && <OptimizationAuditsTab />}
    </div>
  )
}

// ---- Orders Tab ----

function OrdersTab({ showCompany, companyName }: CompanyDisplay) {
  const { isRole } = useAuth()
  // Hard-deletes are gated to Admin/SuperAdmin on both the backend
  // (mgmt-orders-delete / mgmt-routes-delete in ManagementAgent.cs) and
  // the UI here. Logistics users see the rest of the admin panel but no
  // Delete button.
  const canDelete = isRole('Admin', 'SuperAdmin')
  const [orders, setOrders] = useState<Order[]>([])
  const [loading, setLoading] = useState(true)
  const [statusFilter, setStatusFilter] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const { sorted: sortedOrders, sortKey, sortDir, toggle } = useSortedRows(orders, {
    accessors: {
      address:      (o) => `${o.address ?? ''}, ${o.city ?? ''}`,
      districtZone: (o) => `${o.district ?? ''} / ${o.zone ?? ''}`,
      company:      (o) => companyName(o.companyId),
    },
  })

  useEffect(() => {
    load()
  }, [statusFilter])

  async function load() {
    setLoading(true)
    setSelected(new Set())
    try {
      const result = await getOrders({ status: statusFilter || undefined, pageSize: 50 })
      setOrders(result.items)
    } finally {
      setLoading(false)
    }
  }

  async function handleDelete(orderId: string) {
    if (!confirm('Delete this order? This cannot be undone.')) return
    // Optimistic remove; revert + alert if the server refuses.
    const snapshot = orders
    setOrders((prev) => prev.filter((o) => o.id !== orderId))
    try {
      await deleteOrder(orderId)
    } catch {
      setOrders(snapshot)
      alert('Failed to delete order.')
    }
  }

  // Optimistic per-row status change. Reverts on failure so a transient
  // backend error doesn't leave the grid out-of-sync with reality.
  async function handleStatusChange(orderId: string, newStatus: string) {
    const previous = orders.find((o) => o.id === orderId)?.status
    setOrders((prev) => prev.map((o) => o.id === orderId ? { ...o, status: newStatus as Order['status'] } : o))
    try {
      await updateOrderStatus(orderId, newStatus)
    } catch {
      if (previous) {
        setOrders((prev) => prev.map((o) => o.id === orderId ? { ...o, status: previous } : o))
      }
      alert('Failed to update order status.')
    }
  }

  function toggleOne(id: string) {
    setSelected((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id); else next.add(id)
      return next
    })
  }

  const allSelected = sortedOrders.length > 0 && sortedOrders.every((o) => selected.has(o.id))
  function toggleAll() {
    setSelected(allSelected ? new Set() : new Set(sortedOrders.map((o) => o.id)))
  }

  async function handleBulkApply(target: string) {
    const ids = Array.from(selected)
    if (ids.length === 0 || !target) return
    const next = target as Order['status']
    const before = new Map(orders.map((o) => [o.id, o.status]))
    setOrders((prev) => prev.map((o) => (selected.has(o.id) ? { ...o, status: next } : o)))
    try {
      const result = await bulkUpdateOrderStatus(ids, target)

      const rewrittenStale = new Set(
        result.results.filter((r) => r.ok && r.rewrittenAsStale).map((r) => r.orderId),
      )
      if (rewrittenStale.size > 0) {
        setOrders((prev) => prev.map((o) =>
          rewrittenStale.has(o.id) ? { ...o, status: 'RouteOmitted' as Order['status'] } : o,
        ))
      }

      const failed = result.results.filter((r) => !r.ok).map((r) => r.orderId)
      if (failed.length > 0) {
        setOrders((prev) => prev.map((o) => failed.includes(o.id) && before.has(o.id)
          ? { ...o, status: before.get(o.id) as Order['status'] }
          : o))
        alert(`${failed.length} order${failed.length === 1 ? '' : 's'} could not be updated.`)
      }

      const summary = summarizeBulkStatusResult(result, target)
      if (summary) alert(summary)

      setSelected(new Set())
    } catch {
      setOrders((prev) => prev.map((o) => before.has(o.id)
        ? { ...o, status: before.get(o.id) as Order['status'] }
        : o))
      alert('Bulk status update failed.')
    }
  }

  const statusOptions = ['Ordered', 'Scheduled', 'RouteOptimized', 'RouteOmitted', 'ConfirmationPending', 'Confirmed', 'Rejected', 'OutForDelivery', 'Delivered', 'Archived', 'Cancelled']

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <select
          value={statusFilter}
          onChange={(e) => setStatusFilter(e.target.value)}
          className="text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-brand-400"
        >
          <option value="">All statuses</option>
          {statusOptions.map((s) => <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>)}
        </select>
        <button onClick={load} className="text-sm text-brand-600 hover:underline">Refresh</button>
      </div>

      <BulkStatusBar
        selectedCount={selected.size}
        totalCount={sortedOrders.length}
        onClearSelection={() => setSelected(new Set())}
        onApply={handleBulkApply}
      />

      {loading ? (
        <div className="flex items-center justify-center h-40 text-gray-400">Loading…</div>
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
                <tr>
                  <th className="px-3 py-3 text-left font-medium w-10">
                    <input
                      type="checkbox"
                      checked={allSelected}
                      onChange={toggleAll}
                      className="rounded border-gray-300 text-brand-500 focus:ring-brand-400 cursor-pointer"
                      title="Select all on this page"
                    />
                  </th>
                  {showCompany && (
                    <SortHeader label="Company"     sortKey="company"              activeKey={sortKey} dir={sortDir} onClick={() => toggle('company')} />
                  )}
                  <SortHeader label="Store"         sortKey="storeName"            activeKey={sortKey} dir={sortDir} onClick={() => toggle('storeName')} />
                  <SortHeader label="Address"       sortKey="address"              activeKey={sortKey} dir={sortDir} onClick={() => toggle('address')} />
                  <SortHeader label="District/Zone" sortKey="districtZone"         activeKey={sortKey} dir={sortDir} onClick={() => toggle('districtZone')} />
                  <SortHeader label="Order Date"    sortKey="orderDate"            activeKey={sortKey} dir={sortDir} onClick={() => toggle('orderDate')} />
                  <SortHeader label="Expected"      sortKey="expectedDeliveryDate" activeKey={sortKey} dir={sortDir} onClick={() => toggle('expectedDeliveryDate')} />
                  <SortHeader label="Status"        sortKey="status"               activeKey={sortKey} dir={sortDir} onClick={() => toggle('status')} />
                  <th className="px-4 py-3 text-left font-medium">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {sortedOrders.map((o) => (
                  <tr key={o.id} className={`hover:bg-gray-50 ${selected.has(o.id) ? 'bg-brand-50/40' : ''}`}>
                    <td className="px-3 py-3">
                      <input
                        type="checkbox"
                        checked={selected.has(o.id)}
                        onChange={() => toggleOne(o.id)}
                        className="rounded border-gray-300 text-brand-500 focus:ring-brand-400 cursor-pointer"
                      />
                    </td>
                    {showCompany && (
                      <td className="px-4 py-3 text-gray-500 text-xs">{companyName(o.companyId)}</td>
                    )}
                    <td className="px-4 py-3 font-medium text-gray-900">{o.storeName}</td>
                    <td className="px-4 py-3 text-gray-600">{o.address}, {o.city}</td>
                    <td className="px-4 py-3 text-gray-500">{o.district} / {o.zone}</td>
                    <td className="px-4 py-3 text-gray-500">{format(new Date(o.orderDate), 'MMM dd')}</td>
                    <td className="px-4 py-3 text-gray-500">
                      {o.expectedDeliveryDate ? format(new Date(o.expectedDeliveryDate), 'MMM dd HH:mm') : '—'}
                    </td>
                    <td className="px-4 py-3">
                      <StatusBadge status={o.status} />
                    </td>
                    <td className="px-4 py-3">
                      <div className="flex items-center gap-2">
                        <select
                          value=""
                          onChange={(e) => e.target.value && handleStatusChange(o.id, e.target.value)}
                          className="text-xs border border-gray-300 rounded px-2 py-1 focus:outline-none focus:ring-1 focus:ring-brand-400"
                        >
                          <option value="">Set status…</option>
                          {statusOptions.map((s) => (
                            <option key={s} value={s}>{s.replace(/([A-Z])/g, ' $1').trim()}</option>
                          ))}
                        </select>
                        {canDelete && (
                          <button
                            onClick={() => handleDelete(o.id)}
                            className="text-xs text-red-500 hover:text-red-700"
                            title="Delete this order"
                          >
                            Delete
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
                {sortedOrders.length === 0 && (
                  <tr><td colSpan={showCompany ? 9 : 8} className="px-4 py-8 text-center text-gray-400">No orders found.</td></tr>
                )}
              </tbody>
            </table>
          </div>
        </div>
      )}
    </div>
  )
}

// ---- Routes Tab ----

function RoutesTab({ showCompany, companyName }: CompanyDisplay) {
  const { isRole } = useAuth()
  const canDelete = isRole('Admin', 'SuperAdmin')
  const [date, setDate] = useState(format(new Date(), 'yyyy-MM-dd'))
  const [routes, setRoutes] = useState<Route[]>([])
  const [loading, setLoading] = useState(true)
  // Accordion: collapsed by default so the day's grid of route cards
  // stays scannable. Clicking a card header toggles its stops list.
  // We track expanded ids rather than a single open-id so the operator
  // can compare multiple routes side-by-side.
  const [expandedIds, setExpandedIds] = useState<Set<string>>(new Set())

  function toggleExpanded(id: string) {
    setExpandedIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  useEffect(() => {
    setLoading(true)
    getRoutes(date)
      .then(setRoutes)
      .finally(() => setLoading(false))
    // Reset accordion state when the date changes — the previous day's
    // expanded IDs are stale for the new set of cards.
    setExpandedIds(new Set())
  }, [date])

  async function handleDeleteRoute(routeId: string) {
    if (!confirm('Delete this route? Its orders will go back to Ordered so you can re-schedule them.')) return
    const snapshot = routes
    setRoutes((prev) => prev.filter((r) => r.id !== routeId))
    try {
      await deleteRoute(routeId)
    } catch {
      setRoutes(snapshot)
      alert('Failed to delete route.')
    }
  }

  // Sort key: primary by scheduledDepartTime ascending (Pickup at 08:00
  // before ZonedDelivery at 09:22). Routes with no scheduledDepartTime
  // (Legacy) sort to the end. Within the same depart time, RouteType
  // priority puts Pickup first (so a warehouse run-out lands above any
  // 08:00 DirectDelivery card sharing the slot).
  function routeOrderKey(r: Route): number {
    const ms = r.scheduledDepartTime ? new Date(r.scheduledDepartTime).getTime() : Number.MAX_SAFE_INTEGER
    const typePriority =
      r.routeType === 'Pickup'         ? 0 :
      r.routeType === 'ZonedDelivery'  ? 1 :
      r.routeType === 'DirectDelivery' ? 2 : 3
    return ms * 4 + typePriority
  }

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <label className="text-sm font-medium text-gray-700">Delivery Date</label>
        <input
          type="date"
          value={date}
          onChange={(e) => setDate(e.target.value)}
          className="text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-brand-400"
        />
      </div>

      {loading ? (
        <div className="flex items-center justify-center h-40 text-gray-400">Loading…</div>
      ) : routes.length === 0 ? (
        <div className="text-center py-12 text-gray-400">No routes for {date}.</div>
      ) : (
        <div className="space-y-4">
          {/* Order by ScheduledDepartTime so the Pickup van's card lands
              ABOVE the ZonedDelivery cards (Pickup leaves the hub at the
              window start; ZonedDelivery vans leave after pickup return +
              sort wait). Legacy routes with no scheduledDepartTime sort
              to the bottom — they predate the pickup → sort flow. */}
          {[...routes]
            .sort((a, b) => routeOrderKey(a) - routeOrderKey(b))
            .map((route) => {
            const isPickup    = route.routeType === 'Pickup'
            const isDirect    = route.routeType === 'DirectDelivery'
            const isZoned     = route.routeType === 'ZonedDelivery'
            const headerTone  = isPickup ? 'bg-amber-50/60'
                              : isDirect ? 'bg-emerald-50/60'
                              : isZoned  ? 'bg-cyan-50/60'
                              : 'bg-white'
            const badgeTone   = isPickup ? 'bg-amber-100 text-amber-800'
                              : isDirect ? 'bg-emerald-100 text-emerald-800'
                              : isZoned  ? 'bg-cyan-100 text-cyan-800'
                              : 'bg-gray-100 text-gray-600'
            const badgeText   = isPickup ? 'PICKUP'
                              : isDirect ? 'DIRECT DELIVERY · hub bypass'
                              : isZoned  ? 'ZONED DELIVERY'
                              : 'LEGACY'

            const isExpanded = expandedIds.has(route.id)
            return (
            <div key={route.id} className="bg-white rounded-xl border border-gray-200 overflow-hidden">
              {/* Header doubles as the accordion toggle. Click anywhere
                  in the header (except the Delete button, which stops
                  propagation) to expand / collapse the body. role="button"
                  + tabIndex make it keyboard-reachable without nesting a
                  real <button> around a Delete <button> (invalid HTML). */}
              <div
                role="button"
                tabIndex={0}
                aria-expanded={isExpanded}
                onClick={() => toggleExpanded(route.id)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault()
                    toggleExpanded(route.id)
                  }
                }}
                className={`px-5 py-4 ${isExpanded ? 'border-b border-gray-100' : ''} flex items-center justify-between cursor-pointer select-none ${headerTone} hover:brightness-[0.98]`}
              >
                <div className="flex items-center gap-3 flex-1 min-w-0">
                  <span
                    aria-hidden="true"
                    className={`text-gray-400 text-xs transition-transform duration-150 shrink-0 ${isExpanded ? 'rotate-90' : ''}`}
                  >
                    ▶
                  </span>
                  <div className="space-y-1 min-w-0">
                    <div className="flex items-center gap-2 flex-wrap">
                      <span className={`px-2 py-0.5 rounded-full text-[10px] font-semibold uppercase tracking-wide ${badgeTone}`}>
                        {badgeText}
                      </span>
                      <p className="font-semibold text-gray-900">
                        {isPickup ? 'Warehouse pickup' : `${route.totalStops} stops`}
                      </p>
                    </div>
                    <p className="text-xs text-gray-500">
                      {route.totalDistanceMiles.toFixed(1)} mi · {route.totalDuration}
                      {route.isOptimized && <span className="ml-2 text-green-600 font-medium">Optimized</span>}
                    </p>
                    {(route.scheduledDepartTime || route.hubArrivalTime) && (
                      <p className="text-xs font-medium text-gray-700">
                        {route.scheduledDepartTime && (
                          <>Depart {format(new Date(route.scheduledDepartTime), 'HH:mm')}</>
                        )}
                        {isPickup && route.hubArrivalTime && (
                          <>{' '}→ Return to hub {format(new Date(route.hubArrivalTime), 'HH:mm')}</>
                        )}
                      </p>
                    )}
                  </div>
                </div>
                <div className="flex items-center gap-2 shrink-0">
                  {showCompany && (
                    <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-purple-50 text-purple-700">
                      {companyName(route.companyId)}
                    </span>
                  )}
                  <div className="text-xs text-gray-400">{route.id.slice(0, 8)}</div>
                  {/* Print → server-side iText itinerary PDF. Stops propagation
                      so clicking it doesn't also toggle the accordion. */}
                  <button
                    onClick={(e) => { e.stopPropagation(); printRouteItinerary(route.id).catch(() => alert('Failed to generate route itinerary PDF.')) }}
                    className="text-gray-500 hover:text-brand-600 ml-1"
                    title="Print this route's itinerary (PDF)"
                    aria-label="Print itinerary"
                  >
                    🖨
                  </button>
                  {canDelete && (
                    <button
                      onClick={(e) => { e.stopPropagation(); handleDeleteRoute(route.id) }}
                      className="text-xs text-red-500 hover:text-red-700 ml-2"
                      title="Delete this route and revert its orders to Ordered"
                    >
                      Delete
                    </button>
                  )}
                </div>
              </div>
              {isExpanded && (
              <div className="divide-y divide-gray-50">
                {/* Pickup-only body: three labelled rows (hub depart →
                    warehouse load → hub return) so the operator can see
                    times for every leg of the round trip. Mirrors the
                    delivery-card row style. Warehouse-load is a 50/50
                    midpoint estimate because we only persist the round
                    trip's total duration today; in practice outbound and
                    inbound legs are symmetric enough for the operator
                    view. Real per-leg times would require splitting
                    OutboundDurationSeconds / InboundDurationSeconds onto
                    the route entity. */}
                {isPickup ? (() => {
                  const departIso = route.scheduledDepartTime
                  const arriveIso = route.hubArrivalTime
                  let midDate: Date | null = null
                  if (departIso && arriveIso) {
                    const d = new Date(departIso).getTime()
                    const a = new Date(arriveIso).getTime()
                    if (Number.isFinite(d) && Number.isFinite(a) && a > d) {
                      midDate = new Date((d + a) / 2)
                    }
                  }
                  const fmt = (iso?: string | null, fallback = '—') =>
                    iso ? format(new Date(iso), 'HH:mm') : fallback
                  return (
                    <>
                      <div className="px-5 py-3 flex items-center gap-4">
                        <span className="w-7 h-7 rounded-md bg-amber-500 text-white text-[10px] font-bold flex items-center justify-center uppercase tracking-wide shrink-0">H</span>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-medium text-gray-900 truncate">Hub — depart</p>
                          <p className="text-xs text-gray-500 truncate">{route.startAddress}</p>
                        </div>
                        <div className="text-right shrink-0">
                          <p className="text-xs font-medium text-gray-700">{fmt(departIso)}</p>
                          <p className="text-[10px] text-gray-400">leave</p>
                        </div>
                      </div>
                      <div className="px-5 py-3 flex items-center gap-4 bg-amber-50/40">
                        <span className="w-7 h-7 rounded-md bg-amber-500 text-white text-[10px] font-bold flex items-center justify-center uppercase tracking-wide shrink-0">W</span>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-semibold text-amber-700 truncate">
                            {route.warehousePickup?.name ?? 'Warehouse'}
                          </p>
                          {route.warehousePickup?.address && (
                            <p className="text-xs text-gray-500 truncate">{route.warehousePickup.address}</p>
                          )}
                        </div>
                        <div className="text-right shrink-0">
                          <p className="text-xs font-medium text-gray-700">
                            {midDate ? format(midDate, 'HH:mm') : '—'}
                          </p>
                          <p className="text-[10px] text-gray-400">load (est.)</p>
                        </div>
                      </div>
                      <div className="px-5 py-3 flex items-center gap-4">
                        <span className="w-7 h-7 rounded-md bg-amber-500 text-white text-[10px] font-bold flex items-center justify-center uppercase tracking-wide shrink-0">H</span>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-medium text-gray-900 truncate">Hub — return</p>
                          <p className="text-xs text-gray-500 truncate">{route.endAddress}</p>
                        </div>
                        <div className="text-right shrink-0">
                          <p className="text-xs font-medium text-gray-700">{fmt(arriveIso)}</p>
                          <p className="text-[10px] text-gray-400">arrive</p>
                        </div>
                      </div>
                      <div className="px-5 py-2 text-[11px] text-gray-400 italic">
                        Cargo for every paired ZonedDelivery van on this date. Sort begins when this van returns.
                      </div>
                    </>
                  )
                })() : (
                  <>
                    {/* Warehouse pickup is the route's pinned first physical stop —
                        the driver loads product there before any delivery. */}
                    {route.warehousePickup && (
                      <div className="px-5 py-3 flex items-center gap-4 bg-amber-50/40">
                        <span className="w-7 h-7 rounded-md bg-amber-500 text-white text-[10px] font-bold flex items-center justify-center uppercase tracking-wide shrink-0">
                          W
                        </span>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-semibold text-amber-700 truncate">
                            Pickup · {route.warehousePickup.name}
                          </p>
                          <p className="text-xs text-gray-500 truncate">{route.warehousePickup.address}</p>
                        </div>
                        <div className="text-right shrink-0">
                          <p className="text-xs font-medium text-gray-700">
                            {route.warehousePickup.estimatedArrival ? format(new Date(route.warehousePickup.estimatedArrival), 'HH:mm') : '—'}
                          </p>
                        </div>
                      </div>
                    )}
                    {route.stops.map((stop) => (
                      <div key={stop.orderId} className="px-5 py-3 flex items-center gap-4">
                        <span className="w-7 h-7 rounded-full bg-brand-100 text-brand-700 text-xs font-bold flex items-center justify-center shrink-0">
                          {stop.sequence}
                        </span>
                        <div className="flex-1 min-w-0">
                          <p className="text-sm font-medium text-gray-900 truncate">{stop.storeName}</p>
                          <p className="text-xs text-gray-500 truncate">{stop.address}</p>
                        </div>
                        <div className="text-right shrink-0">
                          <p className="text-xs font-medium text-gray-700">
                            {stop.estimatedArrival ? format(new Date(stop.estimatedArrival), 'HH:mm') : '—'}
                          </p>
                          <StatusBadge status={stop.confirmationStatus} />
                        </div>
                      </div>
                    ))}
                  </>
                )}
              </div>
              )}
            </div>
            )
          })}
        </div>
      )}
    </div>
  )
}

// ---- Users Tab ----

function UsersTab({ showCompany, companyName }: CompanyDisplay) {
  const { isRole } = useAuth()
  const [users, setUsers] = useState<AppUser[]>([])
  const [warehouses, setWarehouses] = useState<Warehouse[]>([])
  const [loading, setLoading] = useState(true)
  const [showCreate, setShowCreate] = useState(false)
  const [form, setForm] = useState({
    email: '', password: '', firstName: '', lastName: '',
    role: 'Driver' as Role, warehouseId: '',
  })
  const [formError, setFormError] = useState('')
  const [formNotice, setFormNotice] = useState('')
  const [creating, setCreating] = useState(false)
  const { sorted: sortedUsers, sortKey, sortDir, toggle } = useSortedRows(users, {
    accessors: {
      roles:    (u) => u.roles.join(', '),
      isActive: (u) => u.isActive,
      company:  (u) => companyName(u.companyId),
    },
  })

  // Importer accounts are pinned to a single warehouse; the dropdown
  // appears only when the role demands it. Pre-load warehouses once so
  // it's instant when the admin flips the role.
  const isImporterRole = form.role === 'OrderImporter'
  const warehousesById = useMemo(
    () => new Map(warehouses.map((w) => [w.id, w])),
    [warehouses],
  )

  useEffect(() => {
    Promise.all([getUsers(), getWarehouses()])
      .then(([u, w]) => { setUsers(u); setWarehouses(w) })
      .finally(() => setLoading(false))
  }, [])

  async function handleCreate() {
    setFormError('')
    setFormNotice('')
    if (!form.email) { setFormError('Email is required.'); return }
    if (!isImporterRole && !form.password) { setFormError('Password is required.'); return }
    if (isImporterRole && !form.warehouseId) { setFormError('Select a warehouse for this Order Importer.'); return }
    setCreating(true)
    try {
      const payload = {
        email:       form.email,
        // Importer accounts ignore this field on the backend — a temp
        // password is generated and emailed. Pass an empty string so the
        // axios layer doesn't strip the property and confuse callers.
        password:    isImporterRole ? '' : form.password,
        firstName:   form.firstName,
        lastName:    form.lastName,
        role:        form.role,
        warehouseId: isImporterRole ? form.warehouseId : undefined,
      }
      const res = await registerUser(payload)
      const updated = await getUsers()
      setUsers(updated)
      if (isImporterRole) {
        setFormNotice(res.invitationEmailed
          ? 'User created. An invitation email with a temporary password has been sent.'
          : 'User created, but the invitation email failed to send. Resend manually.')
      } else {
        setShowCreate(false)
      }
      setForm({ email: '', password: '', firstName: '', lastName: '', role: 'Driver', warehouseId: '' })
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Failed to create user.'
      setFormError(msg)
    } finally {
      setCreating(false)
    }
  }

  async function handleDeactivate(userId: string) {
    if (!confirm('Deactivate this user?')) return
    await deactivateUser(userId)
    setUsers((prev) => prev.filter((u) => u.id !== userId))
  }

  const roleColors: Record<string, string> = {
    SuperAdmin: 'bg-purple-100 text-purple-700',
    Admin: 'bg-blue-100 text-blue-700',
    Logistics: 'bg-yellow-100 text-yellow-700',
    Driver: 'bg-green-100 text-green-700',
    OrderImporter: 'bg-pink-100 text-pink-700',
  }

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button
          onClick={() => setShowCreate(true)}
          className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 transition-colors"
        >
          Add User
        </button>
      </div>

      {showCreate && (
        <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-4">
          <h3 className="font-semibold text-gray-800">New User</h3>
          <div className="grid grid-cols-2 gap-3">
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">First Name</label>
              <input type="text" value={form.firstName} onChange={(e) => setForm({ ...form, firstName: e.target.value })}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Last Name</label>
              <input type="text" value={form.lastName} onChange={(e) => setForm({ ...form, lastName: e.target.value })}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Email *</label>
              <input type="email" value={form.email} onChange={(e) => setForm({ ...form, email: e.target.value })}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">
                {isImporterRole ? 'Password' : 'Password *'}
              </label>
              <input
                type="password"
                value={form.password}
                onChange={(e) => setForm({ ...form, password: e.target.value })}
                disabled={isImporterRole}
                placeholder={isImporterRole ? 'Generated and emailed to user' : ''}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400 disabled:bg-gray-50 disabled:text-gray-400"
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Role</label>
              <select value={form.role} onChange={(e) => setForm({ ...form, role: e.target.value as Role })}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400">
                {(isRole('SuperAdmin')
                    ? ['Admin', 'Logistics', 'Driver', 'OrderImporter']
                    : ['Logistics', 'Driver', 'OrderImporter']).map((r) => (
                  <option key={r} value={r}>{r}</option>
                ))}
              </select>
            </div>
            {isImporterRole && (
              <div>
                <label className="block text-xs font-medium text-gray-600 mb-1">Warehouse *</label>
                <select
                  value={form.warehouseId}
                  onChange={(e) => setForm({ ...form, warehouseId: e.target.value })}
                  className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400"
                >
                  <option value="">— Select a warehouse —</option>
                  {warehouses.map((w) => (
                    <option key={w.id} value={w.id}>{w.businessName}</option>
                  ))}
                </select>
                <p className="text-[11px] text-gray-400 mt-1">
                  Importer is permanently scoped to this warehouse. They'll receive an invitation email with a temporary password and must change it on first sign-in.
                </p>
              </div>
            )}
          </div>
          {formError && <p className="text-sm text-red-600">{formError}</p>}
          {formNotice && (
            <p className="text-sm text-green-700 bg-green-50 border border-green-200 rounded-lg px-3 py-2">{formNotice}</p>
          )}
          <div className="flex gap-2">
            <button onClick={handleCreate} disabled={creating}
              className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50">
              {creating ? 'Creating…' : 'Create'}
            </button>
            <button onClick={() => setShowCreate(false)}
              className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900">Cancel</button>
          </div>
        </div>
      )}

      {loading ? (
        <div className="flex items-center justify-center h-40 text-gray-400">Loading…</div>
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
              <tr>
                <SortHeader label="Name"       sortKey="fullName"    activeKey={sortKey} dir={sortDir} onClick={() => toggle('fullName')} />
                <SortHeader label="Email"      sortKey="email"       activeKey={sortKey} dir={sortDir} onClick={() => toggle('email')} />
                <SortHeader label="Roles"      sortKey="roles"       activeKey={sortKey} dir={sortDir} onClick={() => toggle('roles')} />
                {showCompany && (
                  <SortHeader label="Company"  sortKey="company"     activeKey={sortKey} dir={sortDir} onClick={() => toggle('company')} />
                )}
                <th className="px-4 py-3 text-left font-medium">Warehouse</th>
                <SortHeader label="Last Login" sortKey="lastLoginAt" activeKey={sortKey} dir={sortDir} onClick={() => toggle('lastLoginAt')} />
                <SortHeader label="Status"     sortKey="isActive"    activeKey={sortKey} dir={sortDir} onClick={() => toggle('isActive')} />
                <th className="px-4 py-3 text-left font-medium"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {sortedUsers.map((u) => (
                <tr key={u.id} className="hover:bg-gray-50">
                  <td className="px-4 py-3 font-medium text-gray-900">{u.fullName}</td>
                  <td className="px-4 py-3 text-gray-600">{u.email}</td>
                  <td className="px-4 py-3">
                    <div className="flex flex-wrap gap-1">
                      {u.roles.map((r) => (
                        <span key={r} className={`px-2 py-0.5 rounded-full text-xs font-medium ${roleColors[r] ?? 'bg-gray-100 text-gray-600'}`}>{r}</span>
                      ))}
                    </div>
                  </td>
                  {showCompany && (
                    <td className="px-4 py-3 text-gray-500 text-xs">{companyName(u.companyId)}</td>
                  )}
                  <td className="px-4 py-3 text-gray-500 text-xs">
                    {u.assignedWarehouseId
                      ? (warehousesById.get(u.assignedWarehouseId)?.businessName ?? '—')
                      : '—'}
                  </td>
                  <td className="px-4 py-3 text-gray-500">
                    {u.lastLoginAt ? format(new Date(u.lastLoginAt), 'MMM dd HH:mm') : '—'}
                  </td>
                  <td className="px-4 py-3">
                    <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${u.isActive ? 'bg-green-100 text-green-700' : 'bg-gray-100 text-gray-500'}`}>
                      {u.isActive ? 'Active' : 'Inactive'}
                    </span>
                  </td>
                  <td className="px-4 py-3">
                    {u.isActive && (
                      <button onClick={() => handleDeactivate(u.id)}
                        className="text-xs text-red-500 hover:text-red-700">Deactivate</button>
                    )}
                  </td>
                </tr>
              ))}
              {sortedUsers.length === 0 && (
                <tr><td colSpan={showCompany ? 8 : 7} className="px-4 py-8 text-center text-gray-400">No users found.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// ---- Hubs Tab ----

function HubsTab({ showCompany, companyName }: CompanyDisplay) {
  const [hubs, setHubs] = useState<Hub[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  // 30 mirrors the entity default; the input shows blank if a sort wait was
  // never set, but we round-trip it through state as a string to keep the
  // number-input controlled cleanly (empty string vs NaN).
  const [form, setForm] = useState({ name: '', address: '', city: '', state: '', zip: '', sortingWaitMinutes: '30' })
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState('')
  const { sorted: sortedHubs, sortKey, sortDir, toggle } = useSortedRows(hubs, {
    accessors: {
      address: (h) => `${h.address}, ${h.city}, ${h.state} ${h.zip}`,
      company: (h) => companyName(h.companyId),
    },
  })

  useEffect(() => {
    getHubs().then(setHubs).finally(() => setLoading(false))
  }, [])

  function startCreate() {
    setForm({ name: '', address: '', city: '', state: '', zip: '', sortingWaitMinutes: '30' })
    setEditing('new')
    setFormError('')
  }

  function startEdit(h: Hub) {
    setForm({
      name: h.name, address: h.address, city: h.city, state: h.state, zip: h.zip,
      sortingWaitMinutes: String(h.sortingWaitMinutes ?? 30),
    })
    setEditing(h.id)
    setFormError('')
  }

  async function handleSave() {
    if (!form.name || !form.address) { setFormError('Name and Address are required.'); return }
    const waitParsed = parseInt(form.sortingWaitMinutes, 10)
    if (Number.isNaN(waitParsed) || waitParsed < 0 || waitParsed > 240) {
      setFormError('Sorting Wait must be a number between 0 and 240 minutes.'); return
    }
    setSaving(true); setFormError('')
    try {
      const payload = {
        name: form.name, address: form.address, city: form.city, state: form.state, zip: form.zip,
        sortingWaitMinutes: waitParsed,
      }
      if (editing === 'new') {
        await createHub(payload)
      } else {
        await updateHub(editing!, payload)
      }
      setHubs(await getHubs())
      setEditing(null)
    } catch { setFormError('Failed to save hub.') }
    finally { setSaving(false) }
  }

  async function handleDeactivate(id: string) {
    if (!confirm('Deactivate this hub?')) return
    await deleteHub(id)
    setHubs((prev) => prev.filter((h) => h.id !== id))
  }

  if (loading) return <div className="flex items-center justify-center h-40 text-gray-400">Loading...</div>

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button onClick={startCreate}
          className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 transition-colors">
          Add Hub
        </button>
      </div>

      {editing !== null && (
        <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-4">
          <h3 className="font-semibold text-gray-800">{editing === 'new' ? 'New Hub' : 'Edit Hub'}</h3>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Name *" value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
            <div />
            <div className="col-span-2">
              <Field label="Address *" value={form.address} onChange={(v) => setForm({ ...form, address: v })} />
            </div>
            <Field label="City" value={form.city} onChange={(v) => setForm({ ...form, city: v })} />
            <div className="grid grid-cols-2 gap-3">
              <Field label="State" value={form.state} onChange={(v) => setForm({ ...form, state: v })} />
              <Field label="ZIP" value={form.zip} onChange={(v) => setForm({ ...form, zip: v })} />
            </div>
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Sorting Wait (minutes)</label>
              <input
                type="number"
                min={0}
                max={240}
                value={form.sortingWaitMinutes}
                onChange={(e) => setForm({ ...form, sortingWaitMinutes: e.target.value })}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400"
              />
              <p className="text-[11px] text-gray-400 mt-1">
                Minutes between a pickup van's arrival here and per-zone delivery vans dispatching. 0–240.
              </p>
            </div>
          </div>
          {formError && <p className="text-sm text-red-600">{formError}</p>}
          <div className="flex gap-2">
            <button onClick={handleSave} disabled={saving}
              className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50">
              {saving ? 'Saving...' : 'Save'}
            </button>
            <button onClick={() => setEditing(null)}
              className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900">Cancel</button>
          </div>
        </div>
      )}

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
            <tr>
              {showCompany && (
                <SortHeader label="Company" sortKey="company" activeKey={sortKey} dir={sortDir} onClick={() => toggle('company')} />
              )}
              <SortHeader label="Name"      sortKey="name"               activeKey={sortKey} dir={sortDir} onClick={() => toggle('name')} />
              <SortHeader label="Address"   sortKey="address"            activeKey={sortKey} dir={sortDir} onClick={() => toggle('address')} />
              <SortHeader label="Sort Wait" sortKey="sortingWaitMinutes" activeKey={sortKey} dir={sortDir} onClick={() => toggle('sortingWaitMinutes')} />
              <th className="px-4 py-3 text-left font-medium"></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {sortedHubs.map((h) => (
              <tr key={h.id} className="hover:bg-gray-50">
                {showCompany && (
                  <td className="px-4 py-3 text-gray-500 text-xs">{companyName(h.companyId)}</td>
                )}
                <td className="px-4 py-3 font-medium text-gray-900">{h.name}</td>
                <td className="px-4 py-3 text-gray-600">{h.address}, {h.city} {h.state} {h.zip}</td>
                <td className="px-4 py-3 text-gray-600">{h.sortingWaitMinutes} min</td>
                <td className="px-4 py-3">
                  <div className="flex gap-2">
                    <button onClick={() => startEdit(h)} className="text-xs text-brand-600 hover:text-brand-800">Edit</button>
                    <button onClick={() => handleDeactivate(h.id)} className="text-xs text-red-500 hover:text-red-700">Deactivate</button>
                  </div>
                </td>
              </tr>
            ))}
            {sortedHubs.length === 0 && (
              <tr><td colSpan={showCompany ? 5 : 4} className="px-4 py-8 text-center text-gray-400">No hubs found.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// ---- Vans Tab ----

function VansTab({ showCompany, companyName }: CompanyDisplay) {
  const [trucks, setTrucks] = useState<Truck[]>([])
  const [hubs, setHubs] = useState<Hub[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  const [form, setForm] = useState({ name: '', licensePlate: '', hubId: '', currentLocationAddress: '' })
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState('')
  const { sorted: sortedTrucks, sortKey, sortDir, toggle } = useSortedRows(trucks, {
    accessors: { company: (t) => companyName(t.companyId) },
  })

  useEffect(() => {
    Promise.all([getTrucks(), getHubs()])
      .then(([t, h]) => { setTrucks(t); setHubs(h) })
      .finally(() => setLoading(false))
  }, [])

  function startCreate() {
    setForm({ name: '', licensePlate: '', hubId: hubs[0]?.id ?? '', currentLocationAddress: '' })
    setEditing('new')
    setFormError('')
  }

  function startEdit(t: Truck) {
    setForm({
      name: t.name,
      licensePlate: t.licensePlate ?? '',
      hubId: t.hubId ?? '',
      currentLocationAddress: t.currentLocationAddress ?? '',
    })
    setEditing(t.id)
    setFormError('')
  }

  async function handleSave() {
    if (!form.name) { setFormError('Name is required.'); return }
    setSaving(true); setFormError('')
    try {
      const payload = {
        name: form.name,
        licensePlate: form.licensePlate || undefined,
        hubId: form.hubId || undefined,
        // Empty string clears the location server-side; undefined leaves it untouched.
        currentLocationAddress: form.currentLocationAddress,
      }
      if (editing === 'new') {
        await createTruck(payload)
      } else {
        await updateTruck(editing!, payload)
      }
      setTrucks(await getTrucks())
      setEditing(null)
    } catch { setFormError('Failed to save van.') }
    finally { setSaving(false) }
  }

  async function handleDeactivate(id: string) {
    if (!confirm('Deactivate this van?')) return
    await deleteTruck(id)
    setTrucks((prev) => prev.filter((t) => t.id !== id))
  }

  // Optimistic inline status update from the grid. We patch local state
  // first so the dropdown reflects the change immediately, then revert on
  // error so the user sees the rollback rather than a stale optimistic value.
  async function handleStatusChange(id: string, newStatus: TruckStatus) {
    const previous = trucks.find((t) => t.id === id)?.status
    setTrucks((prev) => prev.map((t) => (t.id === id ? { ...t, status: newStatus } : t)))
    try {
      await updateTruck(id, { status: newStatus })
    } catch {
      if (previous) {
        setTrucks((prev) => prev.map((t) => (t.id === id ? { ...t, status: previous } : t)))
      }
      alert('Failed to update van status.')
    }
  }

  if (loading) return <div className="flex items-center justify-center h-40 text-gray-400">Loading...</div>

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button onClick={startCreate}
          className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 transition-colors">
          Add Van
        </button>
      </div>

      {editing !== null && (
        <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-4">
          <h3 className="font-semibold text-gray-800">{editing === 'new' ? 'New Van' : 'Edit Van'}</h3>
          <div className="grid grid-cols-3 gap-3">
            <Field label="Vehicle Name *" value={form.name} onChange={(v) => setForm({ ...form, name: v })} />
            <Field label="License Plate" value={form.licensePlate} onChange={(v) => setForm({ ...form, licensePlate: v })} />
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Home Hub</label>
              <select value={form.hubId} onChange={(e) => setForm({ ...form, hubId: e.target.value })}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400">
                <option value="">No hub assigned</option>
                {hubs.map((h) => <option key={h.id} value={h.id}>{h.name}</option>)}
              </select>
            </div>
          </div>
          <div>
            <label className="block text-xs font-medium text-gray-600 mb-1">Current Location</label>
            <input
              type="text"
              value={form.currentLocationAddress}
              onChange={(e) => setForm({ ...form, currentLocationAddress: e.target.value })}
              placeholder="e.g. 1700 N Clark St, Chicago, IL 60614 — leave blank to clear"
              className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400"
            />
            <p className="text-[11px] text-gray-400 mt-1">Address is auto-geocoded on save. Separate from Home Hub — use for "where the van actually is right now."</p>
          </div>
          {formError && <p className="text-sm text-red-600">{formError}</p>}
          <div className="flex gap-2">
            <button onClick={handleSave} disabled={saving}
              className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50">
              {saving ? 'Saving...' : 'Save'}
            </button>
            <button onClick={() => setEditing(null)}
              className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900">Cancel</button>
          </div>
        </div>
      )}

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
            <tr>
              {showCompany && (
                <SortHeader label="Company"        sortKey="company"                activeKey={sortKey} dir={sortDir} onClick={() => toggle('company')} />
              )}
              <SortHeader label="Vehicle"          sortKey="name"                   activeKey={sortKey} dir={sortDir} onClick={() => toggle('name')} />
              <SortHeader label="License Plate"    sortKey="licensePlate"           activeKey={sortKey} dir={sortDir} onClick={() => toggle('licensePlate')} />
              <SortHeader label="Hub"              sortKey="hubName"                activeKey={sortKey} dir={sortDir} onClick={() => toggle('hubName')} />
              <SortHeader label="Current Location" sortKey="currentLocationAddress" activeKey={sortKey} dir={sortDir} onClick={() => toggle('currentLocationAddress')} />
              <SortHeader label="Status"           sortKey="status"                 activeKey={sortKey} dir={sortDir} onClick={() => toggle('status')} />
              <th className="px-4 py-3 text-left font-medium"></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {sortedTrucks.map((t) => (
              <tr key={t.id} className="hover:bg-gray-50">
                {showCompany && (
                  <td className="px-4 py-3 text-gray-500 text-xs">{companyName(t.companyId)}</td>
                )}
                <td className="px-4 py-3 font-medium text-gray-900">{t.name}</td>
                <td className="px-4 py-3 text-gray-600">{t.licensePlate ?? '---'}</td>
                <td className="px-4 py-3">
                  {t.hubName
                    ? <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-brand-50 text-brand-700">{t.hubName}</span>
                    : <span className="text-xs text-gray-400">Unassigned</span>}
                </td>
                <td className="px-4 py-3">
                  {t.currentLocationAddress
                    ? <span className="text-xs text-gray-700">{t.currentLocationAddress}</span>
                    : <span className="text-xs text-gray-400">—</span>}
                </td>
                <td className="px-4 py-3">
                  <TruckStatusSelect
                    value={t.status}
                    onChange={(s) => handleStatusChange(t.id, s)}
                  />
                </td>
                <td className="px-4 py-3">
                  <div className="flex gap-2">
                    <button onClick={() => startEdit(t)} className="text-xs text-brand-600 hover:text-brand-800">Edit</button>
                    <button onClick={() => handleDeactivate(t.id)} className="text-xs text-red-500 hover:text-red-700">Deactivate</button>
                  </div>
                </td>
              </tr>
            ))}
            {sortedTrucks.length === 0 && (
              <tr><td colSpan={showCompany ? 7 : 6} className="px-4 py-8 text-center text-gray-400">No vans found.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// ---- Truck Status Select (inline, color-coded) ----

const TRUCK_STATUS_LABELS: Record<TruckStatus, string> = {
  Available: 'Available',
  AvailableWithIssues: 'Available · Issues',
  Unavailable: 'Unavailable',
}

const TRUCK_STATUS_COLORS: Record<TruckStatus, string> = {
  Available:           'bg-green-50 text-green-700  border-green-200  hover:border-green-400',
  AvailableWithIssues: 'bg-amber-50 text-amber-700  border-amber-200  hover:border-amber-400',
  Unavailable:         'bg-red-50   text-red-700    border-red-200    hover:border-red-400',
}

function TruckStatusSelect({ value, onChange }: {
  value: TruckStatus
  onChange: (next: TruckStatus) => void
}) {
  return (
    <select
      value={value}
      onChange={(e) => {
        const next = e.target.value as TruckStatus
        if (next !== value) onChange(next)
      }}
      className={`text-xs font-medium px-2 py-1 rounded-md border focus:outline-none focus:ring-2 focus:ring-brand-300 transition-colors cursor-pointer ${TRUCK_STATUS_COLORS[value]}`}
    >
      {TRUCK_STATUSES.map((s) => (
        <option key={s} value={s}>{TRUCK_STATUS_LABELS[s]}</option>
      ))}
    </select>
  )
}

// ---- Stores Tab ----

function StoresTab({ showCompany }: CompanyDisplay) {
  const { isRole } = useAuth()
  // Stores are shared master data (one Store row can belong to multiple
  // companies via the StoreCompanies join), so only Global Administrators
  // (SuperAdmin) get the Edit button. Tenant Admins / Logistics see the
  // table read-only — same gate as the backend PUT /api/stores/{id}.
  const canEdit = isRole('SuperAdmin')

  const [stores, setStores] = useState<StoreLite[]>([])
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState('')
  const [editing, setEditing] = useState<StoreLite | null>(null)
  const { sorted: sortedStores, sortKey, sortDir, toggle } = useSortedRows(stores, {
    accessors: {
      zone:     (s) => s.zone     ?? '',
      district: (s) => s.district ?? '',
      // Stores are many-to-many with Companies; sort key is the joined list.
      company:  (s) => (s.companies ?? []).map((c) => c.name).join(', '),
    },
  })

  useEffect(() => {
    getStores().then(setStores).finally(() => setLoading(false))
  }, [])

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase()
    if (!q) return sortedStores
    return sortedStores.filter((s) =>
      s.name.toLowerCase().includes(q)
      || s.licenseNumber.toLowerCase().includes(q)
      || s.customer.toLowerCase().includes(q)
      || s.city.toLowerCase().includes(q)
      || (s.zone     ?? '').toLowerCase().includes(q)
      || (s.district ?? '').toLowerCase().includes(q)
      || (showCompany ? (s.companies ?? []).some((c) => c.name.toLowerCase().includes(q)) : false),
    )
  }, [sortedStores, search, showCompany])

  const zoned = stores.filter((s) => s.zone).length

  // Refresh the row in local state after a save so the operator sees their
  // edit immediately, without a full list re-fetch.
  function applyLocalUpdate(id: string, patch: UpdateStorePayload) {
    setStores((prev) => prev.map((s) => s.id !== id ? s : {
      ...s,
      ...(patch.name          !== undefined ? { name:          patch.name }          : {}),
      ...(patch.licenseNumber !== undefined ? { licenseNumber: patch.licenseNumber } : {}),
      ...(patch.customer      !== undefined ? { customer:      patch.customer }      : {}),
      ...(patch.address       !== undefined ? { address:       patch.address }       : {}),
      ...(patch.city          !== undefined ? { city:          patch.city }          : {}),
      ...(patch.state         !== undefined ? { state:         patch.state }         : {}),
      ...(patch.zip           !== undefined ? { zip:           patch.zip }           : {}),
      ...(patch.county        !== undefined ? { county:        patch.county }        : {}),
      ...(patch.email         !== undefined ? { email:         patch.email }         : {}),
      ...(patch.phone         !== undefined ? { phone:         patch.phone }         : {}),
      ...(patch.isActive      !== undefined ? { isActive:      patch.isActive }      : {}),
    }))
  }

  if (loading) return <div className="flex items-center justify-center h-40 text-gray-400">Loading...</div>

  return (
    <div className="space-y-3">
      <div className="flex items-center gap-3">
        <input
          type="text"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Filter by name, license, customer, city, zone…"
          className="flex-1 max-w-md px-3 py-1.5 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400"
        />
        <span className="text-xs text-gray-500">
          {filtered.length} of {stores.length} · {zoned} zoned
        </span>
      </div>

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
              <tr>
                {showCompany && (
                  <SortHeader label="Company" sortKey="company"       activeKey={sortKey} dir={sortDir} onClick={() => toggle('company')} />
                )}
                <SortHeader label="Name"      sortKey="name"          activeKey={sortKey} dir={sortDir} onClick={() => toggle('name')} />
                <SortHeader label="License #" sortKey="licenseNumber" activeKey={sortKey} dir={sortDir} onClick={() => toggle('licenseNumber')} />
                <SortHeader label="Customer"  sortKey="customer"      activeKey={sortKey} dir={sortDir} onClick={() => toggle('customer')} />
                <SortHeader label="City"      sortKey="city"          activeKey={sortKey} dir={sortDir} onClick={() => toggle('city')} />
                <SortHeader label="Zone"      sortKey="zone"          activeKey={sortKey} dir={sortDir} onClick={() => toggle('zone')} />
                <SortHeader label="District"  sortKey="district"      activeKey={sortKey} dir={sortDir} onClick={() => toggle('district')} />
                {canEdit && <th className="px-4 py-3 text-left font-medium"></th>}
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {filtered.map((s) => (
                <tr key={s.id} className="hover:bg-gray-50">
                  {showCompany && (
                    <td className="px-4 py-3">
                      <div className="flex flex-wrap gap-1">
                        {(s.companies ?? []).map((c) => (
                          <span key={c.id} className="px-2 py-0.5 rounded-full text-xs font-medium bg-purple-50 text-purple-700">{c.name}</span>
                        ))}
                        {(s.companies ?? []).length === 0 && <span className="text-xs text-gray-400">—</span>}
                      </div>
                    </td>
                  )}
                  <td className="px-4 py-3 font-medium text-gray-900">{s.name}</td>
                  <td className="px-4 py-3 text-gray-600 font-mono text-xs">{s.licenseNumber}</td>
                  <td className="px-4 py-3 text-gray-600">{s.customer}</td>
                  <td className="px-4 py-3 text-gray-600">{s.city}</td>
                  <td className="px-4 py-3">
                    {s.zone
                      ? <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-brand-50 text-brand-700">{s.zone}</span>
                      : <span className="text-xs text-gray-400">Unzoned</span>}
                  </td>
                  <td className="px-4 py-3 text-gray-500 text-xs">{s.district ?? '—'}</td>
                  {canEdit && (
                    <td className="px-4 py-3">
                      <button
                        onClick={() => setEditing(s)}
                        className="text-xs text-brand-600 hover:text-brand-800"
                        title="Edit this store"
                      >
                        Edit
                      </button>
                    </td>
                  )}
                </tr>
              ))}
              {filtered.length === 0 && (
                <tr><td colSpan={(showCompany ? 7 : 6) + (canEdit ? 1 : 0)} className="px-4 py-8 text-center text-gray-400">
                  {stores.length === 0 ? 'No stores found.' : 'No stores match the current filter.'}
                </td></tr>
              )}
            </tbody>
          </table>
        </div>
      </div>

      {editing && (
        <EditStoreDialog
          store={editing}
          onCancel={() => setEditing(null)}
          onSaved={(patch) => {
            applyLocalUpdate(editing.id, patch)
            setEditing(null)
          }}
        />
      )}
    </div>
  )
}

// ---- Edit Store Dialog ----
// Light modal that pre-fills from the selected row and PATCH-sends the
// whole form on save. Address fields are split out so the operator can
// fix typo'd street / city / zip without retyping the rest. Toggling
// "Active" off soft-deactivates the row server-side.

function EditStoreDialog({
  store,
  onCancel,
  onSaved,
}: {
  store: StoreLite
  onCancel: () => void
  onSaved: (patch: UpdateStorePayload) => void
}) {
  const [form, setForm] = useState({
    name:          store.name,
    licenseNumber: store.licenseNumber,
    customer:      store.customer ?? '',
    address:       store.address  ?? '',
    city:          store.city     ?? '',
    state:         store.state    ?? 'IL',
    zip:           store.zip      ?? '',
    county:        store.county   ?? '',
    email:         store.email    ?? '',
    phone:         store.phone    ?? '',
    isActive:      store.isActive ?? true,
  })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  async function handleSave() {
    if (!form.name.trim() || !form.licenseNumber.trim() || !form.address.trim()) {
      setError('Name, License Number, and Address are required.')
      return
    }
    setSaving(true)
    setError('')
    try {
      const payload: UpdateStorePayload = {
        name:          form.name.trim(),
        licenseNumber: form.licenseNumber.trim(),
        customer:      form.customer.trim(),
        address:       form.address.trim(),
        city:          form.city.trim(),
        state:         form.state.trim(),
        zip:           form.zip.trim(),
        county:        form.county.trim(),
        email:         form.email.trim(),
        phone:         form.phone.trim(),
        isActive:      form.isActive,
      }
      await updateStore(store.id, payload)
      onSaved(payload)
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Failed to save store.'
      setError(msg)
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="fixed inset-0 bg-black/40 flex items-center justify-center z-50 p-4">
      <div className="bg-white rounded-xl shadow-xl w-full max-w-2xl max-h-[90vh] overflow-y-auto">
        <div className="px-5 py-4 border-b border-gray-100 flex items-center justify-between">
          <h2 className="text-lg font-semibold text-gray-900">Edit Store</h2>
          <button onClick={onCancel} className="text-gray-400 hover:text-gray-600 text-2xl leading-none">×</button>
        </div>
        <div className="p-5 space-y-4">
          <div className="grid grid-cols-2 gap-3">
            <Field label="Name *"          value={form.name}          onChange={(v) => setForm({ ...form, name: v })} />
            <Field label="License Number *" value={form.licenseNumber} onChange={(v) => setForm({ ...form, licenseNumber: v })} />
            <Field label="Customer"        value={form.customer}      onChange={(v) => setForm({ ...form, customer: v })} />
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Status</label>
              <select
                value={form.isActive ? 'active' : 'inactive'}
                onChange={(e) => setForm({ ...form, isActive: e.target.value === 'active' })}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400"
              >
                <option value="active">Active</option>
                <option value="inactive">Inactive</option>
              </select>
            </div>
            <div className="col-span-2">
              <Field label="Address *" value={form.address} onChange={(v) => setForm({ ...form, address: v })} />
              <p className="text-[11px] text-gray-400 mt-1">
                Address changes drop the cached geocode — the next routing run re-resolves the coords.
              </p>
            </div>
            <Field label="City"   value={form.city}   onChange={(v) => setForm({ ...form, city: v })} />
            <Field label="County" value={form.county} onChange={(v) => setForm({ ...form, county: v })} />
            <div className="grid grid-cols-2 gap-3">
              <Field label="State" value={form.state} onChange={(v) => setForm({ ...form, state: v })} />
              <Field label="ZIP"   value={form.zip}   onChange={(v) => setForm({ ...form, zip: v })} />
            </div>
            <Field label="Email" value={form.email} onChange={(v) => setForm({ ...form, email: v })} />
            <Field label="Phone" value={form.phone} onChange={(v) => setForm({ ...form, phone: v })} />
          </div>

          {error && <p className="text-sm text-red-600">{error}</p>}
        </div>
        <div className="px-5 py-4 border-t border-gray-100 flex gap-2 justify-end">
          <button onClick={onCancel} className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900">Cancel</button>
          <button
            onClick={handleSave}
            disabled={saving}
            className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Save'}
          </button>
        </div>
      </div>
    </div>
  )
}

// ---- Warehouses Tab ----

const DAYS = ['monday', 'tuesday', 'wednesday', 'thursday', 'friday', 'saturday', 'sunday'] as const
const DAY_LABELS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun']

type WarehouseForm = {
  businessName: string
  alternateName: string
  address: string
  city: string
  state: string
  zip: string
  licenseNumber: string
  legacyLicenseNumber: string
  // String-typed so the controlled number input stays clean (no
  // NaN-vs-empty headaches). Validated on save.
  loadingWaitMinutes: string
  mondayPickupTime: string
  tuesdayPickupTime: string
  wednesdayPickupTime: string
  thursdayPickupTime: string
  fridayPickupTime: string
  saturdayPickupTime: string
  sundayPickupTime: string
}

const emptyWarehouseForm: WarehouseForm = {
  businessName: '', alternateName: '', address: '', city: '', state: '', zip: '',
  licenseNumber: '', legacyLicenseNumber: '',
  loadingWaitMinutes: '15',
  mondayPickupTime: '', tuesdayPickupTime: '', wednesdayPickupTime: '',
  thursdayPickupTime: '', fridayPickupTime: '', saturdayPickupTime: '', sundayPickupTime: '',
}

function WarehousesTab({ showCompany }: CompanyDisplay) {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  const [form, setForm] = useState<WarehouseForm>(emptyWarehouseForm)
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState('')
  const { sorted: sortedWarehouses, sortKey, sortDir, toggle } = useSortedRows(warehouses, {
    accessors: {
      address:        (w) => `${w.address}, ${w.city}, ${w.state} ${w.zip}`,
      // Warehouses are many-to-many with Companies; sort key is the joined list.
      company:        (w) => (w.companies ?? []).map((c) => c.name).join(', '),
      pickupSchedule: (w) => [
        w.mondayPickupTime, w.tuesdayPickupTime, w.wednesdayPickupTime, w.thursdayPickupTime,
        w.fridayPickupTime, w.saturdayPickupTime, w.sundayPickupTime,
      ].filter(Boolean).length,
    },
  })

  useEffect(() => {
    getWarehouses().then(setWarehouses).finally(() => setLoading(false))
  }, [])

  function startCreate() {
    setForm(emptyWarehouseForm)
    setEditing('new')
    setFormError('')
  }

  function startEdit(w: Warehouse) {
    setForm({
      businessName: w.businessName,
      alternateName: w.alternateName ?? '',
      address: w.address,
      city: w.city,
      state: w.state,
      zip: w.zip,
      licenseNumber: w.licenseNumber ?? '',
      legacyLicenseNumber: w.legacyLicenseNumber ?? '',
      loadingWaitMinutes: String(w.loadingWaitMinutes ?? 15),
      mondayPickupTime: w.mondayPickupTime ?? '',
      tuesdayPickupTime: w.tuesdayPickupTime ?? '',
      wednesdayPickupTime: w.wednesdayPickupTime ?? '',
      thursdayPickupTime: w.thursdayPickupTime ?? '',
      fridayPickupTime: w.fridayPickupTime ?? '',
      saturdayPickupTime: w.saturdayPickupTime ?? '',
      sundayPickupTime: w.sundayPickupTime ?? '',
    })
    setEditing(w.id)
    setFormError('')
  }

  async function handleSave() {
    if (!form.businessName || !form.address) { setFormError('Business Name and Address are required.'); return }
    const waitParsed = parseInt(form.loadingWaitMinutes, 10)
    if (Number.isNaN(waitParsed) || waitParsed < 0 || waitParsed > 240) {
      setFormError('Loading Wait must be a number between 0 and 240 minutes.'); return
    }
    setSaving(true)
    setFormError('')
    try {
      // Coerce the string-typed loadingWaitMinutes field to a number
      // before sending — the rest of the form stays strings (the backend
      // accepts the address fields and pickup times as strings either way).
      const payload: Partial<Warehouse> = { ...form, loadingWaitMinutes: waitParsed }
      if (editing === 'new') {
        await createWarehouse(payload)
      } else {
        await updateWarehouse(editing!, payload)
      }
      const updated = await getWarehouses()
      setWarehouses(updated)
      setEditing(null)
    } catch {
      setFormError('Failed to save warehouse.')
    } finally {
      setSaving(false)
    }
  }

  async function handleDeactivate(id: string) {
    if (!confirm('Deactivate this warehouse?')) return
    await deleteWarehouse(id)
    setWarehouses((prev) => prev.filter((w) => w.id !== id))
  }

  function updateField(field: keyof WarehouseForm, value: string) {
    setForm((prev) => ({ ...prev, [field]: value }))
  }

  if (loading) return <div className="flex items-center justify-center h-40 text-gray-400">Loading...</div>

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <button onClick={startCreate}
          className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 transition-colors">
          Add Warehouse
        </button>
      </div>

      {editing !== null && (
        <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-5">
          <h3 className="font-semibold text-gray-800">{editing === 'new' ? 'New Warehouse' : 'Edit Warehouse'}</h3>

          {/* Business info */}
          <div className="grid grid-cols-2 gap-3">
            <Field label="Business Name *" value={form.businessName} onChange={(v) => updateField('businessName', v)} />
            <Field label="Alternate Name" value={form.alternateName} onChange={(v) => updateField('alternateName', v)} />
            <Field label="License Number" value={form.licenseNumber} onChange={(v) => updateField('licenseNumber', v)} />
            <Field label="Legacy License #" value={form.legacyLicenseNumber} onChange={(v) => updateField('legacyLicenseNumber', v)} />
            <div>
              <label className="block text-xs font-medium text-gray-600 mb-1">Loading Wait (minutes)</label>
              <input
                type="number"
                min={0}
                max={240}
                value={form.loadingWaitMinutes}
                onChange={(e) => updateField('loadingWaitMinutes', e.target.value)}
                className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400"
              />
              <p className="text-[11px] text-gray-400 mt-1">
                Minutes the van waits at this warehouse for loading. Applied to pickup round trips and any DirectDelivery / Legacy route that visits here. 0–240.
              </p>
            </div>
          </div>

          {/* Address */}
          <div className="grid grid-cols-4 gap-3">
            <div className="col-span-2">
              <Field label="Address *" value={form.address} onChange={(v) => updateField('address', v)} />
            </div>
            <Field label="City" value={form.city} onChange={(v) => updateField('city', v)} />
            <div className="grid grid-cols-2 gap-3">
              <Field label="State" value={form.state} onChange={(v) => updateField('state', v)} />
              <Field label="ZIP" value={form.zip} onChange={(v) => updateField('zip', v)} />
            </div>
          </div>

          {/* Pickup schedule */}
          <div>
            <label className="block text-sm font-medium text-gray-700 mb-2">Tentative Weekly Pickup Schedule</label>
            <div className="grid grid-cols-7 gap-2">
              {DAYS.map((day, i) => {
                const key = `${day}PickupTime` as keyof WarehouseForm
                return (
                  <div key={day}>
                    <label className="block text-xs font-medium text-gray-500 mb-1 text-center">{DAY_LABELS[i]}</label>
                    <input
                      type="time"
                      value={form[key]}
                      onChange={(e) => updateField(key, e.target.value)}
                      className="w-full px-2 py-1.5 text-xs border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400 text-center"
                    />
                    {form[key] && (
                      <button onClick={() => updateField(key, '')}
                        className="w-full text-[10px] text-gray-400 hover:text-red-400 mt-0.5">clear</button>
                    )}
                  </div>
                )
              })}
            </div>
            <p className="text-xs text-gray-400 mt-1">Leave blank for days with no scheduled pickup.</p>
          </div>

          {formError && <p className="text-sm text-red-600">{formError}</p>}
          <div className="flex gap-2">
            <button onClick={handleSave} disabled={saving}
              className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50">
              {saving ? 'Saving...' : 'Save'}
            </button>
            <button onClick={() => setEditing(null)}
              className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900">Cancel</button>
          </div>
        </div>
      )}

      {/* Warehouse list */}
      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
            <tr>
              {showCompany && (
                <SortHeader label="Company"       sortKey="company"        activeKey={sortKey} dir={sortDir} onClick={() => toggle('company')} />
              )}
              <SortHeader label="Business Name"   sortKey="businessName"   activeKey={sortKey} dir={sortDir} onClick={() => toggle('businessName')} />
              <SortHeader label="Address"         sortKey="address"        activeKey={sortKey} dir={sortDir} onClick={() => toggle('address')} />
              <SortHeader label="License #"       sortKey="licenseNumber"  activeKey={sortKey} dir={sortDir} onClick={() => toggle('licenseNumber')} />
              <SortHeader label="Pickup Schedule" sortKey="pickupSchedule" activeKey={sortKey} dir={sortDir} onClick={() => toggle('pickupSchedule')} />
              <th className="px-4 py-3 text-left font-medium"></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {sortedWarehouses.map((w) => (
              <tr key={w.id} className="hover:bg-gray-50">
                {showCompany && (
                  <td className="px-4 py-3">
                    <div className="flex flex-wrap gap-1">
                      {(w.companies ?? []).map((c) => (
                        <span key={c.id} className="px-2 py-0.5 rounded-full text-xs font-medium bg-purple-50 text-purple-700">{c.name}</span>
                      ))}
                      {(w.companies ?? []).length === 0 && <span className="text-xs text-gray-400">—</span>}
                    </div>
                  </td>
                )}
                <td className="px-4 py-3">
                  <p className="font-medium text-gray-900">{w.businessName}</p>
                  {w.alternateName && <p className="text-xs text-gray-400">{w.alternateName}</p>}
                </td>
                <td className="px-4 py-3 text-gray-600">{w.address}, {w.city} {w.state}</td>
                <td className="px-4 py-3 text-gray-500 text-xs">{w.licenseNumber ?? '---'}</td>
                <td className="px-4 py-3">
                  <div className="flex gap-1">
                    {DAYS.map((day, i) => {
                      const time = w[`${day}PickupTime` as keyof Warehouse] as string | undefined
                      return (
                        <span key={day} className={`text-[10px] px-1.5 py-0.5 rounded ${
                          time ? 'bg-brand-50 text-brand-700' : 'bg-gray-50 text-gray-300'
                        }`} title={time ? `${DAY_LABELS[i]}: ${time}` : `${DAY_LABELS[i]}: none`}>
                          {DAY_LABELS[i][0]}{time ? ` ${time}` : ''}
                        </span>
                      )
                    })}
                  </div>
                </td>
                <td className="px-4 py-3">
                  <div className="flex gap-2">
                    <button onClick={() => startEdit(w)}
                      className="text-xs text-brand-600 hover:text-brand-800">Edit</button>
                    <button onClick={() => handleDeactivate(w.id)}
                      className="text-xs text-red-500 hover:text-red-700">Deactivate</button>
                  </div>
                </td>
              </tr>
            ))}
            {sortedWarehouses.length === 0 && (
              <tr><td colSpan={showCompany ? 6 : 5} className="px-4 py-8 text-center text-gray-400">No warehouses found.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function Field({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <div>
      <label className="block text-xs font-medium text-gray-600 mb-1">{label}</label>
      <input type="text" value={value} onChange={(e) => onChange(e.target.value)}
        className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
    </div>
  )
}

function StatusBadge({ status }: { status: string }) {
  const styles: Record<string, string> = {
    Ordered: 'bg-indigo-100 text-indigo-700',
    Scheduled: 'bg-blue-100 text-blue-700',
    RouteOptimized: 'bg-cyan-100 text-cyan-700',
    RouteOmitted: 'bg-stone-100 text-stone-600',
    ConfirmationPending: 'bg-yellow-100 text-yellow-700',
    Confirmed: 'bg-green-100 text-green-700',
    Rejected: 'bg-red-100 text-red-700',
    OutForDelivery: 'bg-orange-100 text-orange-700',
    Delivered: 'bg-gray-100 text-gray-600',
    Archived: 'bg-gray-50 text-gray-400',
    Cancelled: 'bg-red-50 text-red-500',
    SentEmail: 'bg-blue-50 text-blue-600',
    Pending: 'bg-yellow-50 text-yellow-600',
    Expired: 'bg-gray-100 text-gray-500',
  }
  return (
    <span className={`px-2 py-0.5 rounded-full text-xs font-medium ${styles[status] ?? 'bg-gray-100 text-gray-500'}`}>
      {status.replace(/([A-Z])/g, ' $1').trim()}
    </span>
  )
}

// ---- Optimization Audit Tab ----
//
// One row per RouteScheduler invocation. The list shows summary stats per
// run; clicking a row expands into a monospace viewer for the full audit
// trail (every step the scheduler took and why). The "Download PDF" button
// invokes the server-side iText generator (see PdfService.BuildAuditPdf)
// rather than building the PDF in-browser — keeps the rendering consistent
// with the route itinerary PDFs and avoids carrying jsPDF in the bundle.

function OptimizationAuditsTab() {
  const [audits, setAudits] = useState<OptimizationAuditSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<OptimizationAuditDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [downloading, setDownloading]   = useState(false)

  useEffect(() => {
    getOptimizationAudits().then(setAudits).finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    if (!selectedId) { setDetail(null); return }
    setDetailLoading(true)
    setDetail(null)
    getOptimizationAudit(selectedId)
      .then(setDetail)
      .finally(() => setDetailLoading(false))
  }, [selectedId])

  async function downloadPdf(d: OptimizationAuditDetail) {
    setDownloading(true)
    try {
      await printOptimizationAudit(d.id)
    } catch {
      alert('Failed to download audit PDF.')
    } finally {
      setDownloading(false)
    }
  }

  if (loading) return <div className="flex items-center justify-center h-40 text-gray-400">Loading...</div>

  return (
    <div className="space-y-4">
      <div className="text-sm text-gray-500">
        Each row is one optimization run. The detail view explains every decision the route
        scheduler made — warehouses involved, hubs picked, chunks formed, hub-bypass exceptions.
      </div>

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
            <tr>
              <th className="px-4 py-3 text-left font-medium">When</th>
              <th className="px-4 py-3 text-left font-medium">Trigger</th>
              <th className="px-4 py-3 text-left font-medium">By</th>
              <th className="px-4 py-3 text-right font-medium">Orders</th>
              <th className="px-4 py-3 text-right font-medium">Routes</th>
              <th className="px-4 py-3 text-right font-medium">Warehouses</th>
              <th className="px-4 py-3 text-right font-medium">Zones</th>
              <th className="px-4 py-3 text-right font-medium">Bypass</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {audits.map((a) => (
              <tr
                key={a.id}
                onClick={() => setSelectedId(a.id === selectedId ? null : a.id)}
                className={`hover:bg-gray-50 cursor-pointer ${selectedId === a.id ? 'bg-brand-50/40' : ''}`}
              >
                <td className="px-4 py-3 text-gray-700">{format(new Date(a.createdAt), 'MMM dd HH:mm:ss')}</td>
                <td className="px-4 py-3 text-gray-700">{a.trigger}</td>
                <td className="px-4 py-3 text-gray-500 text-xs">{a.triggeredBy ?? <em className="text-gray-400">(system)</em>}</td>
                <td className="px-4 py-3 text-right text-gray-600">{a.orderCount}</td>
                <td className="px-4 py-3 text-right text-gray-600">{a.routeCount}</td>
                <td className="px-4 py-3 text-right text-gray-600">{a.warehouseCount}</td>
                <td className="px-4 py-3 text-right text-gray-600">{a.zoneCount}</td>
                <td className="px-4 py-3 text-right">
                  {a.directDeliveryCount > 0
                    ? <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-amber-50 text-amber-700">{a.directDeliveryCount}</span>
                    : <span className="text-gray-300 text-xs">—</span>}
                </td>
              </tr>
            ))}
            {audits.length === 0 && (
              <tr><td colSpan={8} className="px-4 py-8 text-center text-gray-400">
                No optimization runs yet. They appear here after the first time you bulk-Schedule or import with Scheduled status.
              </td></tr>
            )}
          </tbody>
        </table>
      </div>

      {selectedId && (
        <div className="bg-white rounded-xl border border-gray-200">
          <div className="px-5 py-3 border-b border-gray-100 flex items-center justify-between">
            <div>
              <h3 className="text-sm font-semibold text-gray-800">Audit detail</h3>
              {detail && (
                <p className="text-xs text-gray-500 mt-0.5">{detail.summary}</p>
              )}
            </div>
            <div className="flex items-center gap-2">
              {detail && (
                <button
                  onClick={() => downloadPdf(detail)}
                  disabled={downloading}
                  className="px-3 py-1.5 bg-brand-500 text-white text-xs font-medium rounded-md hover:bg-brand-600 disabled:opacity-50"
                >
                  {downloading ? 'Generating…' : 'Download PDF'}
                </button>
              )}
              <button
                onClick={() => setSelectedId(null)}
                className="text-xs text-gray-500 hover:text-gray-700"
              >
                Close
              </button>
            </div>
          </div>
          <div className="p-5">
            {detailLoading ? (
              <div className="text-sm text-gray-400">Loading audit body…</div>
            ) : detail ? (
              <pre className="text-xs font-mono text-gray-700 whitespace-pre-wrap leading-relaxed bg-gray-50 rounded-lg p-4 border border-gray-100 max-h-[32rem] overflow-y-auto">
                {detail.logText}
              </pre>
            ) : (
              <div className="text-sm text-red-600">Failed to load audit body.</div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
