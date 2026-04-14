import { useState } from 'react'
import type { Notification } from '../types'
import { format } from 'date-fns'

interface Props {
  notifications: Notification[]
}

const TYPE_COLORS: Record<string, string> = {
  OrderReceived: 'bg-blue-500',
  RouteReady: 'bg-indigo-500',
  ConfirmationSent: 'bg-yellow-500',
  ConfirmationReceived: 'bg-green-500',
  OrderRescheduled: 'bg-red-500',
  DeliveryStarted: 'bg-purple-500',
  DeliveryCompleted: 'bg-teal-500',
  SystemAlert: 'bg-orange-500',
}

export default function NotificationPanel({ notifications }: Props) {
  const [open, setOpen] = useState(false)
  const unread = notifications.length

  return (
    <div className="relative">
      <button
        onClick={() => setOpen((o) => !o)}
        className="relative p-2 rounded-lg hover:bg-gray-100 transition-colors"
        aria-label="Notifications"
      >
        <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2}
            d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" />
        </svg>
        {unread > 0 && (
          <span className="absolute -top-0.5 -right-0.5 w-4 h-4 bg-red-500 text-white text-xs rounded-full flex items-center justify-center font-bold">
            {unread > 9 ? '9+' : unread}
          </span>
        )}
      </button>

      {open && (
        <>
          <div className="fixed inset-0 z-10" onClick={() => setOpen(false)} />
          <div className="absolute right-0 mt-2 w-80 bg-white border border-gray-200 rounded-xl shadow-lg z-20 overflow-hidden">
            <div className="px-4 py-3 border-b border-gray-100 flex items-center justify-between">
              <span className="font-semibold text-gray-800 text-sm">Notifications</span>
              <span className="text-xs text-gray-400">{notifications.length} events</span>
            </div>
            <div className="max-h-96 overflow-y-auto divide-y divide-gray-100">
              {notifications.length === 0 ? (
                <div className="px-4 py-8 text-center text-gray-400 text-sm">No notifications yet.</div>
              ) : (
                notifications.map((n) => (
                  <div key={n.id} className="flex gap-3 px-4 py-3 hover:bg-gray-50">
                    <div className={`w-2 h-2 rounded-full mt-1.5 shrink-0 ${TYPE_COLORS[n.type] ?? 'bg-gray-400'}`} />
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-gray-800">{n.title}</p>
                      <p className="text-xs text-gray-500 mt-0.5 leading-relaxed">{n.body}</p>
                      <p className="text-xs text-gray-400 mt-1">
                        {format(new Date(n.occurredAt), 'h:mm a')}
                      </p>
                    </div>
                  </div>
                ))
              )}
            </div>
          </div>
        </>
      )}
    </div>
  )
}
