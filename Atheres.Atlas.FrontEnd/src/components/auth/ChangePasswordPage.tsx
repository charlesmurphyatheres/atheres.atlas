import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { changePassword } from '../../services/apiService'
import { useAuth } from '../../contexts/AuthContext'

/**
 * Required first-login screen for invited accounts (the OrderImporter
 * invitation flow leaves the user with a temporary password and a
 * `mustChangePassword` flag). After a successful change the backend revokes
 * all refresh tokens, so we sign the user out and route them back to /login.
 */
export default function ChangePasswordPage() {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword]         = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [error, setError]                     = useState('')
  const [loading, setLoading]                 = useState(false)
  const [done, setDone]                       = useState(false)

  async function handleSubmit(e: FormEvent) {
    e.preventDefault()
    setError('')
    if (newPassword.length < 8) { setError('New password must be at least 8 characters.'); return }
    if (newPassword !== confirmPassword) { setError('New passwords do not match.'); return }
    if (newPassword === currentPassword) { setError('New password must differ from the current one.'); return }

    setLoading(true)
    try {
      await changePassword(currentPassword, newPassword)
      setDone(true)
      // Backend revoked all refresh tokens — drop client state and go to /login.
      await logout()
      navigate('/login', { replace: true, state: { passwordChanged: true } })
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Password update failed.'
      setError(msg)
    } finally {
      setLoading(false)
    }
  }

  if (done) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="text-sm text-gray-500">Redirecting to sign in…</div>
      </div>
    )
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm">
        <img
          src="/images/atlas_logo.jpg"
          alt="Atlas Deliver"
          className="w-full h-auto rounded-2xl mb-6 shadow-sm"
        />
        <div className="bg-white rounded-2xl border border-gray-200 shadow-sm p-8">
          <h1 className="text-2xl font-bold text-gray-900 mb-1">Change Password</h1>
          <p className="text-sm text-gray-500 mb-6">
            {user?.mustChangePassword
              ? 'Welcome — please replace the temporary password we emailed you before continuing.'
              : 'Update your password.'}
          </p>

          <form onSubmit={handleSubmit} className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">
                {user?.mustChangePassword ? 'Temporary password' : 'Current password'}
              </label>
              <input
                type="password"
                required
                autoComplete="current-password"
                value={currentPassword}
                onChange={(e) => setCurrentPassword(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-brand-400 focus:border-transparent"
              />
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">New password</label>
              <input
                type="password"
                required
                autoComplete="new-password"
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-brand-400 focus:border-transparent"
              />
              <p className="text-[11px] text-gray-400 mt-1">At least 8 characters with an uppercase letter, a digit, and a symbol.</p>
            </div>

            <div>
              <label className="block text-sm font-medium text-gray-700 mb-1">Confirm new password</label>
              <input
                type="password"
                required
                autoComplete="new-password"
                value={confirmPassword}
                onChange={(e) => setConfirmPassword(e.target.value)}
                className="w-full px-3 py-2 border border-gray-300 rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-brand-400 focus:border-transparent"
              />
            </div>

            {error && <p className="text-sm text-red-600 bg-red-50 rounded-lg px-3 py-2">{error}</p>}

            <button
              type="submit"
              disabled={loading}
              className="w-full py-2.5 px-4 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
            >
              {loading ? 'Updating…' : 'Update password'}
            </button>
          </form>
        </div>
      </div>
    </div>
  )
}
