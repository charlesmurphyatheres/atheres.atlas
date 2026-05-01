import { useEffect, useMemo, useState } from 'react'
import { getUsers, registerUser, deactivateUser, getOrders, updateOrderStatus, bulkUpdateOrderStatus, getRoutes, getWarehouses, createWarehouse, updateWarehouse, deleteWarehouse, getHubs, createHub, updateHub, deleteHub, getTrucks, createTruck, updateTruck, deleteTruck } from '../../services/apiService'
import BulkStatusBar from '../ui/BulkStatusBar'
import { summarizeBulkStatusResult } from '../ui/bulkStatusSummary'
import { TRUCK_STATUSES, type TruckStatus } from '../../types'
import { useSortedRows } from '../../hooks/useSortedRows'
import { SortHeader } from '../ui/SortHeader'
import type { AppUser, Order, Route, Role, Warehouse, Hub, Truck } from '../../types'
import { format } from 'date-fns'
import { useAuth } from '../../contexts/AuthContext'

type Tab = 'users' | 'orders' | 'routes' | 'warehouses' | 'hubs' | 'vans'

export default function AdminPanel() {
  const { user } = useAuth()
  const [tab, setTab] = useState<Tab>('orders')

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Admin Panel</h1>
          <p className="text-sm text-gray-500 mt-0.5">{user?.companyName ?? 'All Companies'}</p>
        </div>
      </div>

      <div className="flex gap-1 border-b border-gray-200">
        {(['orders', 'routes', 'hubs', 'vans', 'warehouses', 'users'] as Tab[]).map((t) => (
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

      {tab === 'orders' && <OrdersTab />}
      {tab === 'routes' && <RoutesTab />}
      {tab === 'hubs' && <HubsTab />}
      {tab === 'vans' && <VansTab />}
      {tab === 'warehouses' && <WarehousesTab />}
      {tab === 'users' && <UsersTab />}
    </div>
  )
}

// ---- Orders Tab ----

function OrdersTab() {
  const [orders, setOrders] = useState<Order[]>([])
  const [loading, setLoading] = useState(true)
  const [statusFilter, setStatusFilter] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const { sorted: sortedOrders, sortKey, sortDir, toggle } = useSortedRows(orders, {
    accessors: {
      address:      (o) => `${o.address ?? ''}, ${o.city ?? ''}`,
      districtZone: (o) => `${o.district ?? ''} / ${o.zone ?? ''}`,
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
                    </td>
                  </tr>
                ))}
                {sortedOrders.length === 0 && (
                  <tr><td colSpan={8} className="px-4 py-8 text-center text-gray-400">No orders found.</td></tr>
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

function RoutesTab() {
  const [date, setDate] = useState(format(new Date(), 'yyyy-MM-dd'))
  const [routes, setRoutes] = useState<Route[]>([])
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    setLoading(true)
    getRoutes(date)
      .then(setRoutes)
      .finally(() => setLoading(false))
  }, [date])

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
          {routes.map((route) => (
            <div key={route.id} className="bg-white rounded-xl border border-gray-200 overflow-hidden">
              <div className="px-5 py-4 border-b border-gray-100 flex items-center justify-between">
                <div>
                  <p className="font-semibold text-gray-900">{route.totalStops} stops</p>
                  <p className="text-xs text-gray-500 mt-0.5">
                    {route.totalDistanceMiles.toFixed(1)} mi · {route.totalDuration}
                    {route.isOptimized && <span className="ml-2 text-green-600 font-medium">Optimized</span>}
                  </p>
                </div>
                <div className="text-xs text-gray-400">{route.id.slice(0, 8)}</div>
              </div>
              <div className="divide-y divide-gray-50">
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
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}

// ---- Users Tab ----

function UsersTab() {
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
                <tr><td colSpan={7} className="px-4 py-8 text-center text-gray-400">No users found.</td></tr>
              )}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// ---- Hubs Tab ----

function HubsTab() {
  const [hubs, setHubs] = useState<Hub[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  const [form, setForm] = useState({ name: '', address: '', city: '', state: '', zip: '' })
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState('')
  const { sorted: sortedHubs, sortKey, sortDir, toggle } = useSortedRows(hubs, {
    accessors: { address: (h) => `${h.address}, ${h.city}, ${h.state} ${h.zip}` },
  })

  useEffect(() => {
    getHubs().then(setHubs).finally(() => setLoading(false))
  }, [])

  function startCreate() {
    setForm({ name: '', address: '', city: '', state: '', zip: '' })
    setEditing('new')
    setFormError('')
  }

  function startEdit(h: Hub) {
    setForm({ name: h.name, address: h.address, city: h.city, state: h.state, zip: h.zip })
    setEditing(h.id)
    setFormError('')
  }

  async function handleSave() {
    if (!form.name || !form.address) { setFormError('Name and Address are required.'); return }
    setSaving(true); setFormError('')
    try {
      if (editing === 'new') {
        await createHub(form)
      } else {
        await updateHub(editing!, form)
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
              <SortHeader label="Name"    sortKey="name"    activeKey={sortKey} dir={sortDir} onClick={() => toggle('name')} />
              <SortHeader label="Address" sortKey="address" activeKey={sortKey} dir={sortDir} onClick={() => toggle('address')} />
              <th className="px-4 py-3 text-left font-medium"></th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {sortedHubs.map((h) => (
              <tr key={h.id} className="hover:bg-gray-50">
                <td className="px-4 py-3 font-medium text-gray-900">{h.name}</td>
                <td className="px-4 py-3 text-gray-600">{h.address}, {h.city} {h.state} {h.zip}</td>
                <td className="px-4 py-3">
                  <div className="flex gap-2">
                    <button onClick={() => startEdit(h)} className="text-xs text-brand-600 hover:text-brand-800">Edit</button>
                    <button onClick={() => handleDeactivate(h.id)} className="text-xs text-red-500 hover:text-red-700">Deactivate</button>
                  </div>
                </td>
              </tr>
            ))}
            {sortedHubs.length === 0 && (
              <tr><td colSpan={3} className="px-4 py-8 text-center text-gray-400">No hubs found.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// ---- Vans Tab ----

function VansTab() {
  const [trucks, setTrucks] = useState<Truck[]>([])
  const [hubs, setHubs] = useState<Hub[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  const [form, setForm] = useState({ name: '', licensePlate: '', hubId: '', currentLocationAddress: '' })
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState('')
  const { sorted: sortedTrucks, sortKey, sortDir, toggle } = useSortedRows(trucks)

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
              <tr><td colSpan={6} className="px-4 py-8 text-center text-gray-400">No vans found.</td></tr>
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
  mondayPickupTime: '', tuesdayPickupTime: '', wednesdayPickupTime: '',
  thursdayPickupTime: '', fridayPickupTime: '', saturdayPickupTime: '', sundayPickupTime: '',
}

function WarehousesTab() {
  const [warehouses, setWarehouses] = useState<Warehouse[]>([])
  const [loading, setLoading] = useState(true)
  const [editing, setEditing] = useState<string | 'new' | null>(null)
  const [form, setForm] = useState<WarehouseForm>(emptyWarehouseForm)
  const [saving, setSaving] = useState(false)
  const [formError, setFormError] = useState('')
  const { sorted: sortedWarehouses, sortKey, sortDir, toggle } = useSortedRows(warehouses, {
    accessors: {
      address:        (w) => `${w.address}, ${w.city}, ${w.state} ${w.zip}`,
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
    setSaving(true)
    setFormError('')
    try {
      if (editing === 'new') {
        await createWarehouse(form as Partial<Warehouse>)
      } else {
        await updateWarehouse(editing!, form)
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
              <tr><td colSpan={5} className="px-4 py-8 text-center text-gray-400">No warehouses found.</td></tr>
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
