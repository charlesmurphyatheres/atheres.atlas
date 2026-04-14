import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import type { AuthUser, Role, TokenResponse } from '../types'
import { login as apiLogin, refreshToken as apiRefresh, logout as apiLogout } from '../services/apiService'

interface AuthContextType {
  user: AuthUser | null
  accessToken: string | null
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
  isRole: (...roles: Role[]) => boolean
  loading: boolean
}

const AuthContext = createContext<AuthContextType | null>(null)

const TOKEN_KEY = 'atlas_access_token'
const REFRESH_KEY = 'atlas_refresh_token'

function tokenToUser(res: TokenResponse): AuthUser {
  return {
    userId: res.userId,
    email: res.email,
    fullName: res.fullName,
    roles: res.roles,
    companyId: res.companyId,
    companyName: res.companyName,
    companySlug: res.companySlug,
  }
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(null)
  const [accessToken, setAccessToken] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  // On mount, try to restore session from stored refresh token
  useEffect(() => {
    const stored = localStorage.getItem(REFRESH_KEY)
    if (!stored) {
      setLoading(false)
      return
    }
    apiRefresh(stored)
      .then((res) => {
        setAccessToken(res.accessToken)
        setUser(tokenToUser(res))
        localStorage.setItem(TOKEN_KEY, res.accessToken)
        localStorage.setItem(REFRESH_KEY, res.refreshToken)
      })
      .catch(() => {
        localStorage.removeItem(TOKEN_KEY)
        localStorage.removeItem(REFRESH_KEY)
      })
      .finally(() => setLoading(false))
  }, [])

  async function login(email: string, password: string) {
    const res = await apiLogin(email, password)
    setAccessToken(res.accessToken)
    setUser(tokenToUser(res))
    localStorage.setItem(TOKEN_KEY, res.accessToken)
    localStorage.setItem(REFRESH_KEY, res.refreshToken)
  }

  async function logout() {
    try { await apiLogout() } catch { /* ignore */ }
    setAccessToken(null)
    setUser(null)
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(REFRESH_KEY)
  }

  function isRole(...roles: Role[]) {
    return roles.some((r) => user?.roles.includes(r))
  }

  return (
    <AuthContext.Provider value={{ user, accessToken, login, logout, isRole, loading }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth must be used within AuthProvider')
  return ctx
}
