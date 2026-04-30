import { useEffect, useState } from 'react'
import { getSettings, saveSettings } from '../services/apiService'
import { DEFAULT_MAX_STOPS, MAX_STOPS_HARD_CAP, type UserRouteSettings } from '../types'

// Start and end addresses are no longer configured here — every route
// originates and terminates at the assigned truck's home hub (set per-van
// in Administration → Vans).
export default function SettingsPanel() {
  const [settings, setSettings] = useState<UserRouteSettings>({
    userId: 'default',
    deliveryWindowStart: '08:00:00',
    deliveryWindowEnd: '17:00:00',
    confirmationDeadlineHours: 3,
    maxStopsPerRoute: DEFAULT_MAX_STOPS,
    waitMinutesPerStop: 0,
  })
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [saved, setSaved] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getSettings('default')
      .then(setSettings)
      .catch(() => { /* Use defaults if no settings exist yet */ })
      .finally(() => setLoading(false))
  }, [])

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    setSaving(true)
    setError(null)
    try {
      await saveSettings(settings)
      setSaved(true)
      setTimeout(() => setSaved(false), 3000)
    } catch (err: any) {
      const message = err?.response?.data?.error ?? err?.message ?? 'Failed to save settings.'
      setError(message)
    } finally {
      setSaving(false)
    }
  }

  function update(field: keyof UserRouteSettings, value: string | number) {
    setSettings((s) => ({ ...s, [field]: value }))
  }

  if (loading) return <div className="flex items-center justify-center h-64 text-gray-400">Loading...</div>

  return (
    <div className="max-w-2xl space-y-6">
      <h1 className="text-2xl font-bold text-gray-900">Route Settings</h1>
      <p className="text-sm text-gray-500 -mt-3">
        Routes start and end at each van's home hub. Configure hubs under
        Administration → Hubs and assign them per van under Administration → Vans.
      </p>

      <form onSubmit={handleSubmit} className="space-y-6">
        <Section title="Delivery Window & Confirmation">
          <div className="grid grid-cols-2 gap-3">
            <Field label="Window Start">
              <input type="time" value={settings.deliveryWindowStart.substring(0, 5)}
                onChange={(e) => update('deliveryWindowStart', e.target.value + ':00')}
                className={inputClass} required />
            </Field>
            <Field label="Window End">
              <input type="time" value={settings.deliveryWindowEnd.substring(0, 5)}
                onChange={(e) => update('deliveryWindowEnd', e.target.value + ':00')}
                className={inputClass} required />
            </Field>
          </div>
          <Field label="Confirmation Deadline (hours before delivery)">
            <input type="number" value={settings.confirmationDeadlineHours} min={1} max={24}
              onChange={(e) => update('confirmationDeadlineHours', parseInt(e.target.value, 10))}
              className={inputClass} required />
            <p className="text-xs text-gray-400 mt-1">
              If a store hasn't confirmed within this window, the delivery is rescheduled.
            </p>
          </Field>
        </Section>

        <Section title="Route Limits">
          <Field label={`Maximum Stops per Route (max ${MAX_STOPS_HARD_CAP})`}>
            <input
              type="number"
              value={settings.maxStopsPerRoute}
              min={1}
              max={MAX_STOPS_HARD_CAP}
              onChange={(e) => {
                const parsed = parseInt(e.target.value, 10)
                if (Number.isNaN(parsed)) return
                // Browser-side clamp so users don't submit a value above the cap.
                update('maxStopsPerRoute', Math.min(Math.max(parsed, 1), MAX_STOPS_HARD_CAP))
              }}
              className={inputClass}
              required
            />
            <p className="text-xs text-gray-400 mt-1">
              Hard cap on deliveries per route. The optimizer splits ready
              orders into chunks no larger than this value. Server enforces a
              maximum of {MAX_STOPS_HARD_CAP}.
            </p>
          </Field>
          <Field label="Wait Time per Stop (minutes)">
            <input
              type="number"
              value={settings.waitMinutesPerStop}
              min={0}
              max={120}
              onChange={(e) => {
                const parsed = parseInt(e.target.value, 10)
                if (Number.isNaN(parsed)) return
                update('waitMinutesPerStop', Math.max(parsed, 0))
              }}
              className={inputClass}
              required
            />
            <p className="text-xs text-gray-400 mt-1">
              Idle time the van waits at each delivery stop, on top of the
              service time. Does not apply to the start or end at the home
              hub — those depot bookends are not delivery stops.
            </p>
          </Field>
        </Section>

        <div className="flex items-center gap-3">
          <button
            type="submit"
            disabled={saving}
            className="px-5 py-2 bg-brand-500 text-white rounded-lg text-sm font-medium hover:bg-brand-600 disabled:opacity-50 transition-colors"
          >
            {saving ? 'Saving...' : 'Save Settings'}
          </button>
          {saved && <span className="text-sm text-green-600 font-medium">✓ Saved successfully</span>}
          {error && <span className="text-sm text-red-600 font-medium">⚠ {error}</span>}
        </div>
      </form>
    </div>
  )
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 p-5 space-y-4">
      <h2 className="font-semibold text-gray-800">{title}</h2>
      {children}
    </div>
  )
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <label className="block text-sm font-medium text-gray-700 mb-1">{label}</label>
      {children}
    </div>
  )
}

const inputClass =
  'w-full border border-gray-300 rounded-lg px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-brand-500'
