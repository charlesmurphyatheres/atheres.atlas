import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import { useAuth } from './AuthContext'

interface CompanyContextType {
  /** The currently-active company ID. For SuperAdmin this is user-selected.
   *  For Admin/Logistics/Driver this is pinned to their own company.
   *  '' or null means "All companies" (SuperAdmin only). */
  activeCompanyId: string | null
  setActiveCompanyId: (id: string | null) => void
}

const CompanyContext = createContext<CompanyContextType | null>(null)

const STORAGE_KEY = 'atlas_active_company'

export function CompanyProvider({ children }: { children: ReactNode }) {
  const { user } = useAuth()
  const isSuperAdmin = user?.roles.includes('SuperAdmin') ?? false

  const [activeCompanyId, setActiveCompanyIdState] = useState<string | null>(() => {
    if (!isSuperAdmin) return null
    return localStorage.getItem(STORAGE_KEY)
  })

  // Non-SuperAdmins are pinned to their own company
  useEffect(() => {
    if (user && !isSuperAdmin) {
      setActiveCompanyIdState(user.companyId ?? null)
    }
  }, [user, isSuperAdmin])

  function setActiveCompanyId(id: string | null) {
    if (!isSuperAdmin) return // can't change for non-super-admins
    setActiveCompanyIdState(id)
    if (id) localStorage.setItem(STORAGE_KEY, id)
    else localStorage.removeItem(STORAGE_KEY)
  }

  return (
    <CompanyContext.Provider value={{ activeCompanyId, setActiveCompanyId }}>
      {children}
    </CompanyContext.Provider>
  )
}

export function useCompanyContext() {
  const ctx = useContext(CompanyContext)
  if (!ctx) throw new Error('useCompanyContext must be used within CompanyProvider')
  return ctx
}
