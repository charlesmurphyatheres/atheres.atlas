import axios from 'axios'
import type { Order, Route, UserRouteSettings, PagedResult, TokenResponse, Company, Hub, Warehouse, OrderBatch, CommunicationRecord, Truck, AppUser } from '../types'

// In dev the Vite proxy maps /api to localhost:7071. In production behind
// Front Door the same relative /api path works. Deployments that serve the
// frontend from a different origin can override via VITE_API_BASE_URL.
const API_BASE = (import.meta.env.VITE_API_BASE_URL ?? '').replace(/\/$/, '') + '/api'

const api = axios.create({
  baseURL: API_BASE,
  headers: { 'Content-Type': 'application/json' },
})

// Attach stored access token + active company on every request
api.interceptors.request.use((config) => {
  const token = localStorage.getItem('atlas_access_token')
  if (token) config.headers.Authorization = `Bearer ${token}`

  // For SuperAdmin: pass the currently-selected company as a header
  // Non-SuperAdmins are already scoped by their JWT claim
  const activeCompanyId = localStorage.getItem('atlas_active_company')
  if (activeCompanyId) config.headers['X-Company-Id'] = activeCompanyId
  return config
})

// ---- Auth ----

export async function login(email: string, password: string): Promise<TokenResponse> {
  const { data } = await api.post<TokenResponse>('/auth/login', { email, password })
  return data
}

export async function refreshToken(refreshToken: string): Promise<TokenResponse> {
  const { data } = await api.post<TokenResponse>('/auth/refresh', { refreshToken })
  return data
}

export async function logout(): Promise<void> {
  await api.post('/auth/logout')
}

export async function getMe(): Promise<AppUser> {
  const { data } = await api.get<AppUser>('/auth/me')
  return data
}

export async function registerUser(payload: {
  email: string
  password: string
  firstName?: string
  lastName?: string
  role?: string
  companyId?: string
}): Promise<{ userId: string }> {
  const { data } = await api.post<{ userId: string }>('/auth/register', payload)
  return data
}

// ---- Orders ----

export async function getOrders(params?: {
  status?: string
  date?: string
  page?: number
  pageSize?: number
}): Promise<PagedResult<Order>> {
  const { data } = await api.get<PagedResult<Order>>('/orders', { params })
  return data
}

export async function getOrder(id: string): Promise<Order> {
  const { data } = await api.get<Order>(`/orders/${id}`)
  return data
}

export async function ingestOrders(orders: unknown[]): Promise<{ accepted: number; orderIds: string[] }> {
  const { data } = await api.post('/orders', orders)
  return data
}

export async function updateOrderStatus(id: string, status: string): Promise<void> {
  await api.patch(`/orders/${id}/status`, { status })
}

// ---- Routes ----

export async function getRoutes(date?: string): Promise<Route[]> {
  const { data } = await api.get<Route[]>('/routes', { params: { date } })
  return data
}

export async function getRoute(id: string): Promise<Route> {
  const { data } = await api.get<Route>(`/routes/${id}`)
  return data
}

export async function confirmDeliveryManual(orderId: string): Promise<void> {
  await api.post(`/orders/${orderId}/confirm`)
}

export async function reorderRouteStop(routeId: string, orderId: string, newSequence: number): Promise<void> {
  await api.patch(`/routes/${routeId}/stops/${orderId}`, { sequence: newSequence })
}

// ---- Settings ----

export async function getSettings(userId = 'default'): Promise<UserRouteSettings> {
  const { data } = await api.get<UserRouteSettings>(`/settings/${userId}`)
  return data
}

export async function saveSettings(settings: UserRouteSettings): Promise<void> {
  await api.put(`/settings/${settings.userId}`, settings)
}

// ---- Companies (SuperAdmin) ----

export async function getCompanies(): Promise<Company[]> {
  // Trailing slash: Azure Front Door's /api/companies/* route only matches
  // paths that actually have a segment after "companies" (including the
  // empty segment produced by a trailing slash). Omitting it falls through
  // to /api/* -> main Function App -> 404. Same convention below for
  // hubs/warehouses/users list endpoints.
  const { data } = await api.get<Company[]>('/companies/')
  return data
}

export async function getCompany(id: string): Promise<Company> {
  const { data } = await api.get<Company>(`/companies/${id}`)
  return data
}

export async function createCompany(payload: Partial<Company>): Promise<Company> {
  const { data } = await api.post<Company>('/companies/', payload)
  return data
}

export async function updateCompany(id: string, payload: Partial<Company>): Promise<void> {
  await api.put(`/companies/${id}`, payload)
}

// ---- Trucks ----

export async function getTrucks(): Promise<Truck[]> {
  const { data } = await api.get<Truck[]>('/trucks')
  return data
}

export async function createTruck(payload: {
  name: string
  licensePlate?: string
  hubId?: string
  currentLocationAddress?: string
}): Promise<Truck> {
  const { data } = await api.post<Truck>('/trucks', payload)
  return data
}

export async function updateTruck(id: string, payload: Partial<Truck>): Promise<void> {
  await api.put(`/trucks/${id}`, payload)
}

export async function deleteTruck(id: string): Promise<void> {
  await api.delete(`/trucks/${id}`)
}

// ---- Stores ----

export async function getStores(): Promise<{ id: string; name: string; licenseNumber: string; customer: string; city: string }[]> {
  const { data } = await api.get('/stores')
  return data
}

// ---- Hubs ----

export async function getHubs(): Promise<Hub[]> {
  const { data } = await api.get<Hub[]>('/hubs/')
  return data
}

export async function createHub(payload: Partial<Hub>): Promise<Hub> {
  const { data } = await api.post<Hub>('/hubs/', payload)
  return data
}

export async function updateHub(id: string, payload: Partial<Hub>): Promise<void> {
  await api.put(`/hubs/${id}`, payload)
}

export async function deleteHub(id: string): Promise<void> {
  await api.delete(`/hubs/${id}`)
}

// ---- Warehouses ----

export async function getWarehouses(): Promise<Warehouse[]> {
  const { data } = await api.get<Warehouse[]>('/warehouses/')
  return data
}

export async function createWarehouse(payload: Partial<Warehouse>): Promise<Warehouse> {
  const { data } = await api.post<Warehouse>('/warehouses/', payload)
  return data
}

export async function updateWarehouse(id: string, payload: Partial<Warehouse>): Promise<void> {
  await api.put(`/warehouses/${id}`, payload)
}

export async function deleteWarehouse(id: string): Promise<void> {
  await api.delete(`/warehouses/${id}`)
}

// ---- Batches ----

export async function getBatches(status?: string): Promise<OrderBatch[]> {
  const { data } = await api.get<OrderBatch[]>('/batches', { params: { status } })
  return data
}

export async function getBatch(id: string): Promise<OrderBatch & { orders: Order[] }> {
  const { data } = await api.get<OrderBatch & { orders: Order[] }>(`/batches/${id}`)
  return data
}

export async function createBatch(payload: { name: string; hubId: string; warehouseId: string; truckId?: string }): Promise<{ id: string; name: string }> {
  const { data } = await api.post('/batches', payload)
  return data
}

export async function addOrdersToBatch(batchId: string, orderIds: string[]): Promise<{ added: number }> {
  const { data } = await api.post(`/batches/${batchId}/orders`, { orderIds })
  return data
}

export async function removeOrderFromBatch(batchId: string, orderId: string): Promise<void> {
  await api.delete(`/batches/${batchId}/orders/${orderId}`)
}

export async function scheduleBatch(batchId: string, pickupDate: string): Promise<{ ordersQueued: number; routeRequestId: string }> {
  const { data } = await api.post(`/batches/${batchId}/schedule`, { pickupDate })
  return data
}

// ---- Order Actions ----

export async function archiveOrder(orderId: string): Promise<void> {
  await api.post(`/orders/${orderId}/archive`)
}

// ---- Communications ----

export async function getOrderCommunications(params?: {
  status?: string
  page?: number
  pageSize?: number
}): Promise<PagedResult<CommunicationRecord>> {
  const { data } = await api.get<PagedResult<CommunicationRecord>>('/orders/communications', { params })
  return data
}

// ---- Ready to Pickup (public) ----

export async function readyToPickup(companySlug: string, warehouseLicenseNumber: string, pickupDateTime: string): Promise<{
  batchId: string
  orderCount: number
  ordersQueuedForRouting: number
}> {
  const { data } = await api.post('/ready-to-pickup', { companySlug, warehouseLicenseNumber, pickupDateTime })
  return data
}

// ---- Users (Admin) ----

export async function getUsers(): Promise<AppUser[]> {
  const { data } = await api.get<AppUser[]>('/users/')
  return data
}

export async function updateUser(userId: string, payload: Partial<AppUser>): Promise<void> {
  await api.put(`/users/${userId}`, payload)
}

export async function deactivateUser(userId: string): Promise<void> {
  await api.delete(`/users/${userId}`)
}
