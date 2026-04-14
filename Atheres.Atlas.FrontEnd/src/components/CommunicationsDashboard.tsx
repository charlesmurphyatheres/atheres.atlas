import { useEffect, useState } from 'react'
import { getOrderCommunications } from '../services/apiService'
import type { CommunicationRecord, OrderStatus } from '../types'
import { format } from 'date-fns'

const STATUS_FILTERS: { value: OrderStatus | ''; label: string }[] = [
  { value: '', label: 'All' },
  { value: 'ConfirmationPending', label: 'Awaiting Response' },
  { value: 'Confirmed', label: 'Confirmed' },
  { value: 'Rejected', label: 'Rejected' },
]

export default function CommunicationsDashboard() {
  const [records, setRecords] = useState<CommunicationRecord[]>([])
  const [total, setTotal] = useState(0)
  const [page, setPage] = useState(1)
  const [statusFilter, setStatusFilter] = useState('')
  const [loading, setLoading] = useState(true)
  const pageSize = 25

  useEffect(() => {
    setLoading(true)
    getOrderCommunications({ status: statusFilter || undefined, page, pageSize })
      .then((res) => { setRecords(res.items); setTotal(res.total) })
      .finally(() => setLoading(false))
  }, [page, statusFilter])

  const totalPages = Math.ceil(total / pageSize)

  return (
    <div className="space-y-4">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Communications</h1>
        <p className="text-sm text-gray-500 mt-0.5">Track confirmation emails and store responses</p>
      </div>

      <div className="flex items-center gap-3">
        <select
          value={statusFilter}
          onChange={(e) => { setStatusFilter(e.target.value); setPage(1) }}
          className="text-sm border border-gray-300 rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-brand-400"
        >
          {STATUS_FILTERS.map((f) => (
            <option key={f.value} value={f.value}>{f.label}</option>
          ))}
        </select>
        <span className="text-sm text-gray-400">{total} record{total !== 1 ? 's' : ''}</span>
      </div>

      {loading ? (
        <div className="flex items-center justify-center h-40 text-gray-400">Loading...</div>
      ) : (
        <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
                <tr>
                  {['Store', 'Location', 'Email', 'Email Sent', 'ETA', 'Deadline', 'Status', 'Responded'].map((h) => (
                    <th key={h} className="px-4 py-3 text-left font-medium">{h}</th>
                  ))}
                </tr>
              </thead>
              <tbody className="divide-y divide-gray-100">
                {records.map((r) => (
                  <tr key={r.id} className="hover:bg-gray-50">
                    <td className="px-4 py-3 font-medium text-gray-900">{r.storeName}</td>
                    <td className="px-4 py-3 text-gray-600">{r.city}</td>
                    <td className="px-4 py-3 text-gray-600 text-xs">{r.confirmation?.emailSentTo ?? r.email}</td>
                    <td className="px-4 py-3 text-gray-500 text-xs">
                      {r.confirmation?.emailSentAt
                        ? format(new Date(r.confirmation.emailSentAt), 'MMM dd HH:mm')
                        : <span className="text-yellow-500">Pending</span>}
                    </td>
                    <td className="px-4 py-3 text-gray-500 text-xs">
                      {r.expectedDeliveryDate
                        ? format(new Date(r.expectedDeliveryDate), 'MMM dd HH:mm')
                        : '---'}
                    </td>
                    <td className="px-4 py-3 text-gray-500 text-xs">
                      {r.confirmationDeadline
                        ? format(new Date(r.confirmationDeadline), 'MMM dd HH:mm')
                        : '---'}
                    </td>
                    <td className="px-4 py-3">
                      <CommStatusBadge
                        orderStatus={r.status}
                        confirmationStatus={r.confirmation?.status}
                        confirmedBy={r.confirmation?.confirmedBy}
                      />
                    </td>
                    <td className="px-4 py-3 text-gray-500 text-xs">
                      {r.confirmation?.confirmedAt
                        ? format(new Date(r.confirmation.confirmedAt), 'MMM dd HH:mm')
                        : '---'}
                    </td>
                  </tr>
                ))}
                {records.length === 0 && (
                  <tr><td colSpan={8} className="px-4 py-8 text-center text-gray-400">No communications found.</td></tr>
                )}
              </tbody>
            </table>
          </div>

          {totalPages > 1 && (
            <div className="px-4 py-3 border-t border-gray-100 flex items-center justify-between">
              <button onClick={() => setPage((p) => Math.max(1, p - 1))} disabled={page === 1}
                className="text-sm text-gray-500 hover:text-gray-700 disabled:opacity-30">Previous</button>
              <span className="text-xs text-gray-400">Page {page} of {totalPages}</span>
              <button onClick={() => setPage((p) => Math.min(totalPages, p + 1))} disabled={page >= totalPages}
                className="text-sm text-gray-500 hover:text-gray-700 disabled:opacity-30">Next</button>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function CommStatusBadge({ orderStatus, confirmationStatus, confirmedBy }: {
  orderStatus: string
  confirmationStatus?: string
  confirmedBy?: string
}) {
  if (confirmedBy === 'rejected' || orderStatus === 'Rejected') {
    return <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-red-100 text-red-700">Rejected</span>
  }
  if (orderStatus === 'Confirmed') {
    return <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-green-100 text-green-700">Confirmed</span>
  }
  if (confirmationStatus === 'SentEmail') {
    return <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-blue-50 text-blue-600">Email Sent</span>
  }
  if (confirmationStatus === 'Expired') {
    return <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-gray-100 text-gray-500">Expired</span>
  }
  return <span className="px-2 py-0.5 rounded-full text-xs font-medium bg-yellow-50 text-yellow-600">Pending</span>
}
