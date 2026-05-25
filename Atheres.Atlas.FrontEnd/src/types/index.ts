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
  | 'RouteOmitted'

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
  companyId?: string
  warehouseLicenseNumber?: string
  storeLicenseNumber?: string
  licenseNumber?: string
  customer?: string
  salesOrderNumber?: string
  purchaseOrderNumber?: string
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

export interface WarehousePickup {
  id: string
  name: string
  address: string
  latitude?: number
  longitude?: number
  estimatedArrival?: string
}

/** Classifies a route by its role in the pickup → hub-sort → zoned-delivery
 *  pipeline. Mirrors RouteType.cs on the backend. */
export type RouteTypeKind = 'Legacy' | 'Pickup' | 'ZonedDelivery' | 'DirectDelivery'

export interface Route {
  id: string
  companyId?: string
  /** New-flow shape — what kind of van this route represents. Old rows
   *  default to 'Legacy' and render with the original card layout. */
  routeType?: RouteTypeKind
  /** Single zone the route's stops belong to. Set for ZonedDelivery and
   *  DirectDelivery; null for Pickup and Legacy. */
  zoneId?: string | null
  /** When the van leaves its start point. Pickup leaves the hub at the
   *  delivery window start; ZonedDelivery leaves the hub after the paired
   *  Pickup returns + the hub sort wait. */
  scheduledDepartTime?: string | null
  /** Pickup-only: when the van returns to the hub from the warehouse. */
  hubArrivalTime?: string | null
  deliveryDate: string
  warehouseId?: string
  warehousePickup?: WarehousePickup
  hubId?: string
  startAddress: string
  startLatitude: number
  startLongitude: number
  endAddress: string
  endLatitude: number
  endLongitude: number
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
  maxStopsPerRoute: number
  waitMinutesPerStop: number
}

/** Server-enforced ceiling on UserRouteSettings.maxStopsPerRoute. */
export const MAX_STOPS_HARD_CAP = 20

/** Default UserRouteSettings.maxStopsPerRoute applied to new rows. */
export const DEFAULT_MAX_STOPS = 5

/** Default per-stop service minutes applied to new UserRouteSettings rows. */
export const DEFAULT_WAIT_MINUTES_PER_STOP = 15

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

// ---- Auth ----

export type Role = 'SuperAdmin' | 'Admin' | 'Logistics' | 'Driver' | 'OrderImporter'

export interface AuthUser {
  userId: string
  email: string
  fullName: string
  roles: Role[]
  companyId?: string
  companyName?: string
  companySlug?: string
  warehouseId?: string
  warehouseName?: string
  mustChangePassword?: boolean
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
  warehouseId?: string
  warehouseName?: string
  mustChangePassword?: boolean
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
  /** Minutes pickup vans wait at the hub for orders to be sorted before
   *  per-zone delivery vans dispatch. Editable per hub. */
  sortingWaitMinutes: number
  isActive: boolean
}

export interface Warehouse {
  id: string
  /** Companies this warehouse is shared with (many-to-many). */
  companies?: { id: string; name: string }[]
  /** Minutes a van spends at this warehouse loading. Applied to the
   *  Pickup round trip and any DirectDelivery / Legacy route that
   *  visits this warehouse. */
  loadingWaitMinutes?: number
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

export type TruckStatus = 'Available' | 'AvailableWithIssues' | 'Unavailable'

export const TRUCK_STATUSES: readonly TruckStatus[] = [
  'Available',
  'AvailableWithIssues',
  'Unavailable',
] as const

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
  status: TruckStatus
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
  assignedWarehouseId?: string
  mustChangePassword?: boolean
  isActive: boolean
  lastLoginAt?: string
}
