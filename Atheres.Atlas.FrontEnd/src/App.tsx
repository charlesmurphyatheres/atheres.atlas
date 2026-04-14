import { useEffect, useState } from 'react'
import { Routes, Route, NavLink, useNavigate } from 'react-router-dom'
import Dashboard from './components/Dashboard'
import OrderList from './components/OrderList'
import LiveRouteTracker from './components/LiveRouteTracker'
import SettingsPanel from './components/SettingsPanel'
import NotificationPanel from './components/NotificationPanel'
import LoginPage from './components/auth/LoginPage'
import ProtectedRoute from './components/ProtectedRoute'
import AdminPanel from './components/admin/AdminPanel'
import LogisticsPanel from './components/logistics/LogisticsPanel'
import CommunicationsDashboard from './components/CommunicationsDashboard'
import DriverPanel from './components/driver/DriverPanel'
import { signalRService } from './services/signalRService'
import { useAuth } from './contexts/AuthContext'
import type { Notification } from './types'

export default function App() {
  const { user, logout, loading } = useAuth()
  const navigate = useNavigate()
  const [notifications, setNotifications] = useState<Notification[]>([])
  const [connected, setConnected] = useState(false)

  useEffect(() => {
    if (!user) return

    signalRService.start()
      .then(() => setConnected(true))
      .catch((err) => console.error('SignalR start failed:', err))

    const unsubscribe = signalRService.onNotification((n) => {
      setNotifications((prev) => [n, ...prev].slice(0, 50))
    })

    return () => {
      unsubscribe()
      signalRService.stop()
      setConnected(false)
    }
  }, [user])

  async function handleLogout() {
    await logout()
    navigate('/login', { replace: true })
  }

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="w-8 h-8 border-2 border-brand-500 border-t-transparent rounded-full animate-spin" />
      </div>
    )
  }

  const navLinkClass = ({ isActive }: { isActive: boolean }) =>
    `px-4 py-2 rounded-md text-sm font-medium transition-colors ${
      isActive
        ? 'bg-brand-500 text-white'
        : 'text-gray-600 hover:bg-gray-100'
    }`

  const isAdmin = user?.roles.some((r) => r === 'Admin' || r === 'SuperAdmin')
  const isLogistics = user?.roles.some((r) => r === 'Logistics')
  const isDriver = user?.roles.includes('Driver') && !isAdmin && !isLogistics

  return (
    <div className="min-h-screen flex flex-col">
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route
          path="/unauthorized"
          element={
            <div className="min-h-screen flex items-center justify-center text-gray-500">
              <div className="text-center">
                <p className="text-4xl mb-3">🔒</p>
                <p className="font-medium">You don't have permission to view this page.</p>
              </div>
            </div>
          }
        />
        <Route
          path="/*"
          element={
            <ProtectedRoute>
              <div className="min-h-screen flex flex-col">
                {/* Header */}
                <header className="bg-white border-b border-gray-200 shadow-sm">
                  <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
                    <div className="flex items-center justify-between h-16">
                      <div className="flex items-center gap-3">
                        <div className="w-8 h-8 bg-brand-500 rounded-lg flex items-center justify-center">
                          <span className="text-white font-bold text-sm">A</span>
                        </div>
                        <div>
                          <span className="text-xl font-bold text-gray-900">Atheres Atlas</span>
                          {user?.companyName && (
                            <span className="ml-2 text-xs text-gray-400">{user.companyName}</span>
                          )}
                        </div>
                      </div>

                      <nav className="flex items-center gap-1">
                        {/* Shared */}
                        {!isDriver && (
                          <>
                            <NavLink to="/" end className={navLinkClass}>Dashboard</NavLink>
                            <NavLink to="/orders" className={navLinkClass}>Orders</NavLink>
                            <NavLink to="/routes" className={navLinkClass}>Routes</NavLink>
                            <NavLink to="/communications" className={navLinkClass}>Comms</NavLink>
                          </>
                        )}
                        {/* Admin-only */}
                        {isAdmin && (
                          <NavLink to="/admin" className={navLinkClass}>Admin</NavLink>
                        )}
                        {/* Logistics */}
                        {(isLogistics || isAdmin) && (
                          <NavLink to="/logistics" className={navLinkClass}>Logistics</NavLink>
                        )}
                        {/* Driver */}
                        {isDriver && (
                          <NavLink to="/driver" className={navLinkClass}>My Route</NavLink>
                        )}
                        {isAdmin && (
                          <NavLink to="/settings" className={navLinkClass}>Settings</NavLink>
                        )}
                      </nav>

                      <div className="flex items-center gap-3">
                        <span className={`w-2 h-2 rounded-full ${connected ? 'bg-green-500' : 'bg-red-400'}`} />
                        <span className="text-xs text-gray-500 hidden sm:block">{user?.fullName}</span>
                        <NotificationPanel notifications={notifications} />
                        <button
                          onClick={handleLogout}
                          className="text-xs text-gray-500 hover:text-gray-700 px-2 py-1 rounded hover:bg-gray-100"
                        >
                          Sign out
                        </button>
                      </div>
                    </div>
                  </div>
                </header>

                {/* Main content */}
                <main className="flex-1 max-w-7xl mx-auto w-full px-4 sm:px-6 lg:px-8 py-6">
                  <Routes>
                    {/* Driver gets their own default route */}
                    {isDriver ? (
                      <>
                        <Route path="/" element={<DriverPanel />} />
                        <Route path="/driver" element={<DriverPanel />} />
                      </>
                    ) : (
                      <Route path="/" element={<Dashboard notifications={notifications} />} />
                    )}
                    <Route path="/orders" element={
                      <ProtectedRoute roles={['Admin', 'SuperAdmin', 'Logistics']}>
                        <OrderList />
                      </ProtectedRoute>
                    } />
                    <Route path="/routes" element={
                      <ProtectedRoute roles={['Admin', 'SuperAdmin', 'Logistics']}>
                        <LiveRouteTracker />
                      </ProtectedRoute>
                    } />
                    <Route path="/admin" element={
                      <ProtectedRoute roles={['Admin', 'SuperAdmin']}>
                        <AdminPanel />
                      </ProtectedRoute>
                    } />
                    <Route path="/communications" element={
                      <ProtectedRoute roles={['Admin', 'SuperAdmin', 'Logistics']}>
                        <CommunicationsDashboard />
                      </ProtectedRoute>
                    } />
                    <Route path="/logistics" element={
                      <ProtectedRoute roles={['Admin', 'SuperAdmin', 'Logistics']}>
                        <LogisticsPanel />
                      </ProtectedRoute>
                    } />
                    <Route path="/driver" element={
                      <ProtectedRoute roles={['Driver']}>
                        <DriverPanel />
                      </ProtectedRoute>
                    } />
                    <Route path="/settings" element={
                      <ProtectedRoute roles={['Admin', 'SuperAdmin']}>
                        <SettingsPanel />
                      </ProtectedRoute>
                    } />
                  </Routes>
                </main>
              </div>
            </ProtectedRoute>
          }
        />
      </Routes>
    </div>
  )
}
