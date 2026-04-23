import { useEffect, useState } from 'react'
import { getCompanies } from '../services/apiService'
import type { Company } from '../types'
import { useAuth } from '../contexts/AuthContext'
import { useCompanyContext } from '../contexts/CompanyContext'

/**
 * Company selector for SuperAdmin. Non-SuperAdmins see only their own company as a label.
 * Persists selection in localStorage via CompanyContext.
 */
export default function CompanyPicker() {
  const { user } = useAuth()
  const { activeCompanyId, setActiveCompanyId } = useCompanyContext()
  const [companies, setCompanies] = useState<Company[]>([])

  const isSuperAdmin = user?.roles.includes('SuperAdmin') ?? false

  useEffect(() => {
    if (isSuperAdmin) {
      getCompanies().then(setCompanies).catch(() => setCompanies([]))
    }
  }, [isSuperAdmin])

  if (!user) return null

  if (!isSuperAdmin) {
    // Pinned to user's own company — show as read-only label
    return (
      <div className="flex items-center gap-2 text-xs text-gray-500">
        <span className="font-medium text-gray-700">{user.companyName ?? '—'}</span>
      </div>
    )
  }

  const selected = companies.find((c) => c.id === activeCompanyId)

  return (
    <div className="flex items-center gap-2">
      <label className="text-[10px] font-semibold text-gray-400 uppercase tracking-wide">Company</label>
      <select
        value={activeCompanyId ?? ''}
        onChange={(e) => setActiveCompanyId(e.target.value || null)}
        className={`text-xs px-2 py-1 rounded-md border focus:outline-none focus:ring-2 focus:ring-brand-400 ${
          activeCompanyId
            ? 'border-brand-300 bg-brand-50 text-brand-700 font-medium'
            : 'border-yellow-300 bg-yellow-50 text-yellow-700 font-medium'
        }`}
      >
        <option value="">⚠ All Companies</option>
        {companies.map((c) => (
          <option key={c.id} value={c.id}>{c.name}</option>
        ))}
      </select>
      {!activeCompanyId && (
        <span className="text-[10px] text-yellow-600 font-medium">Select a company to proceed</span>
      )}
      {selected && (
        <span className="text-[10px] text-gray-400">({selected.slug})</span>
      )}
    </div>
  )
}
