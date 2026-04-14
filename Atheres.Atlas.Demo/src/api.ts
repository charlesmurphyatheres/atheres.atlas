import axios from 'axios'

const api = axios.create({
  baseURL: '/api',
  headers: { 'Content-Type': 'application/json' },
})

// Attach token if available
api.interceptors.request.use((config) => {
  const token = localStorage.getItem('demo_access_token')
  if (token) config.headers.Authorization = `Bearer ${token}`
  return config
})

// ---- Auth ----

export async function login(email: string, password: string) {
  const { data } = await api.post('/auth/login', { email, password })
  return data as {
    accessToken: string
    refreshToken: string
    userId: string
    email: string
    fullName: string
    roles: string[]
  }
}

// ---- Companies ----

export async function getCompanies() {
  const { data } = await api.get<{ id: string; name: string }[]>('/companies')
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
  const { data } = await api.get<Warehouse[]>('/warehouses')
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

export async function ingestOrders(orders: unknown[]): Promise<{ accepted: number; orderIds: string[]; errors: string[] }> {
  const { data } = await api.post('/orders', orders)
  return data
}

// ---- Ready to Pickup ----

export async function readyToPickup(companySlug: string, warehouseLicenseNumber: string, pickupDateTime: string) {
  const { data } = await api.post('/ready-to-pickup', { companySlug, warehouseLicenseNumber, pickupDateTime })
  return data as { batchId: string; orderCount: number; ordersQueuedForRouting: number }
}
