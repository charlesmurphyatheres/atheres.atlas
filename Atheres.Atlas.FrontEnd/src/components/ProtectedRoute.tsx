import { Navigate } from 'react-router-dom'
import { useAuth } from '../contexts/AuthContext'
import type { Role } from '../types'

interface Props {
  children: React.ReactNode
  roles?: Role[]
}

export default function ProtectedRoute({ children, roles }: Props) {
  const { user, loading } = useAuth()

  if (loading) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="w-8 h-8 border-2 border-brand-500 border-t-transparent rounded-full animate-spin" />
      </div>
    )
  }

  if (!user) return <Navigate to="/login" replace />

  if (roles && !roles.some((r) => user.roles.includes(r))) {
    return <Navigate to="/unauthorized" replace />
  }

  return <>{children}</>
}
