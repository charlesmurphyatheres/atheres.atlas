import axios from 'axios'

// In dev the Vite proxy maps /api to localhost:7071. In Azure deployments the
// Demo is served from its own storage origin, so the build sets
// VITE_API_BASE_URL to the Front Door URL that owns the real /api routes.
const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '') + '/api'

const api = axios.create({
  baseURL: API_BASE,
  headers: { 'Content-Type': 'application/json' },
})

// ---- Companies ----

export async function getCompanies() {
  // Trailing slash: AFD's /api/companies/* route only matches /api/companies/
  // not /api/companies (AFD Standard can't express a bare-path pattern). Same
  // rule below for warehouses. See apiService.ts comment for full context.
  const { data } = await api.get<{ id: string; name: string }[]>('/companies/')
  return data
}

// ---- Warehouses ----

export interface Warehouse {
  id: string
  companyId: string
  businessName: string
  alternateName?: string
  licenseNumber?: string
}

export async function getWarehouses(): Promise<Warehouse[]> {
  const { data } = await api.get<Warehouse[]>('/warehouses/')
  return data
}

// ---- Stores ----

export interface Store {
  id: string
  name: string
  licenseNumber: string
  customer: string
  city: string
}

export async function getStores(): Promise<Store[]> {
  const { data } = await api.get<Store[]>('/stores')
  return data
}

// ---- Orders ----

export async function ingestOrder(order: unknown): Promise<{ orderId: string; itemCount: number }> {
  const { data } = await api.post('/orders', order)
  return data
}

// ---- Ready to Pickup ----

// Per-warehouse variant — creates one batch for the named warehouse.
export async function readyToPickup(companySlug: string, warehouseLicenseNumber: string, pickupDateTime: string) {
  const { data } = await api.post('/ready-to-pickup', { companySlug, warehouseLicenseNumber, pickupDateTime })
  return data as { batchId: string; orderCount: number; ordersQueuedForRouting: number }
}

// Marks every Ordered order for a company as ready to pickup AND immediately
// queues routing — there's no separate "run optimization" step in the main app.
export async function markAllReady(companySlug: string, deliveryDate?: string) {
  const { data } = await api.post('/orders/mark-all-ready', { companySlug, deliveryDate })
  return data as { message: string; routesQueued: number; totalOrders: number }
}

// ---- Reset ----

export async function resetAllOrders(): Promise<void> {
  await api.delete('/orders/all')
}
