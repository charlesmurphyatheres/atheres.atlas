import axios from 'axios'

const api = axios.create({
  baseURL: '/api',
  headers: { 'Content-Type': 'application/json' },
})

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

export async function ingestOrder(order: unknown): Promise<{ orderId: string; itemCount: number }> {
  const { data } = await api.post('/orders', order)
  return data
}

// ---- Ready to Pickup ----

export async function readyToPickup(companySlug: string, warehouseLicenseNumber: string, pickupDateTime: string) {
  const { data } = await api.post('/ready-to-pickup', { companySlug, warehouseLicenseNumber, pickupDateTime })
  return data as { batchId: string; orderCount: number; ordersQueuedForRouting: number }
}

// ---- Reset ----

export async function resetAllOrders(): Promise<void> {
  await api.delete('/orders/all')
}
