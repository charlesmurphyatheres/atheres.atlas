export type OrderStatus =
  | 'Ordered'
  | 'Scheduled'
  | 'RouteOptimized'
  | 'ConfirmationPending'
  | 'Confirmed'
  | 'Rejected'
  | 'OutForDelivery'
  | 'Delivered'
  | 'Archived'
  | 'Cancelled'

export type ConfirmationStatus =
  | 'Pending'
  | 'SentEmail'
  | 'SentSms'
  | 'Confirmed'
  | 'Expired'
  | 'Failed'

export type NotificationType =
  | 'OrderReceived'
  | 'RouteReady'
  | 'ConfirmationSent'
  | 'ConfirmationReceived'
  | 'OrderRescheduled'
  | 'DeliveryStarted'
  | 'DeliveryCompleted'
  | 'SystemAlert'

export interface Order {
  id: string
  warehouseLicenseNumber?: string
  storeLicenseNumber?: string
  storeName: string
  address: string
  city: string
  state: string
  zip: string
  county: string
  district: string
  zone: string
  orderDate: string
  email: string
  phone?: string
  latitude?: number
  longitude?: number
  status: OrderStatus
  expectedDeliveryDate?: string
  confirmationDeadline?: string
  rescheduleCount: number
  confirmedAt?: string
  storeId?: string
  batchId?: string
  warehouseId?: string
  stopSequence?: number
  routeId?: string
  createdAt: string
  updatedAt: string
}

export interface RouteStop {
  orderId: string
  sequence: number
  storeName: string
  address: string
  latitude: number
  longitude: number
  estimatedArrival?: string
  orderStatus: OrderStatus
  confirmationStatus: ConfirmationStatus
  legDistanceMiles: number
  legDurationMinutes: number
}

export interface Route {
  id: string
  deliveryDate: string
  warehouseId?: string
  startAddress: string
  endAddress: string
  totalStops: number
  totalDistanceMiles: number
  totalDuration: string
  isOptimized: boolean
  overviewPolyline?: string
  stops: RouteStop[]
}

export interface Notification {
  id: string
  type: NotificationType
  title: string
  body: string
  orderId?: string
  routeId?: string
  metadata?: Record<string, string>
  occurredAt: string
}

export interface UserRouteSettings {
  userId: string
  deliveryWindowStart: string
  deliveryWindowEnd: string
  confirmationDeadlineHours: number
}

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

// ---- Auth ----

export type Role = 'SuperAdmin' | 'Admin' | 'Logistics' | 'Driver'

export interface AuthUser {
  userId: string
  email: string
  fullName: string
  roles: Role[]
  companyId?: string
  companyName?: string
  companySlug?: string
}

export interface TokenResponse {
  accessToken: string
  refreshToken: string
  expiresAt: string
  userId: string
  email: string
  fullName: string
  roles: Role[]
  companyId?: string
  companyName?: string
  companySlug?: string
}

// ---- Company / Truck ----

export interface Company {
  id: string
  name: string
  slug: string
  contactEmail?: string
  contactPhone?: string
  timezone: string
  isActive: boolean
  createdAt: string
}

export interface Hub {
  id: string
  companyId: string
  name: string
  address: string
  city: string
  state: string
  zip: string
  isActive: boolean
}

export interface Warehouse {
  id: string
  companyId: string
  businessName: string
  alternateName?: string
  address: string
  city: string
  state: string
  zip: string
  licenseNumber?: string
  legacyLicenseNumber?: string
  isActive: boolean
  mondayPickupTime?: string
  tuesdayPickupTime?: string
  wednesdayPickupTime?: string
  thursdayPickupTime?: string
  fridayPickupTime?: string
  saturdayPickupTime?: string
  sundayPickupTime?: string
}

export type BatchStatus = 'Open' | 'Scheduled' | 'RouteOptimized' | 'InTransit' | 'Completed' | 'Cancelled'

export interface OrderBatch {
  id: string
  companyId: string
  name: string
  hubId: string
  warehouseId: string
  truckId?: string
  status: BatchStatus
  pickupDate?: string
  routeId?: string
  hubName?: string
  warehouseName?: string
  truckName?: string
  orderCount?: number
  createdAt: string
}

export interface Truck {
  id: string
  companyId: string
  name: string
  licensePlate?: string
  hubId?: string
  hubName?: string
  currentLocationAddress?: string | null
  currentLocationLatitude?: number | null
  currentLocationLongitude?: number | null
  currentLocationUpdatedAt?: string | null
  assignedDriverId?: string
  isActive: boolean
}

export interface CommunicationRecord {
  id: string
  storeName: string
  address: string
  city: string
  status: OrderStatus
  email: string
  expectedDeliveryDate?: string
  confirmationDeadline?: string
  confirmedAt?: string
  confirmation?: {
    id: string
    status: string
    emailSentTo?: string
    emailSentAt?: string
    confirmedAt?: string
    confirmedBy?: string
    expiresAt?: string
  }
}

export interface AppUser {
  id: string
  email: string
  fullName: string
  roles: Role[]
  companyId?: string
  assignedTruckId?: string
  isActive: boolean
  lastLoginAt?: string
}
