import { useState, type FormEvent } from 'react'
import { AlertCircle, Brain, CheckCircle2, Eye, EyeOff, KeyRound } from 'lucide-react'
import { useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { api, ApiError } from '../lib/api-client'

function meetsVisiblePolicy(password: string): boolean {
  return password.length >= 12 &&
    /[a-z]/.test(password) &&
    /[A-Z]/.test(password) &&
    /\d/.test(password) &&
    /[^A-Za-z0-9]/.test(password)
}

export default function FirstLoginPasswordPage() {
  const navigate = useNavigate()
  const { loginWithToken } = useAuth()
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [showPasswords, setShowPasswords] = useState(false)
  const [error, setError] = useState('')
  const [submitting, setSubmitting] = useState(false)

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    setError('')
    if (!meetsVisiblePolicy(newPassword)) {
      setError('Use at least 12 characters with uppercase, lowercase, a number, and a symbol.')
      return
    }
    if (newPassword !== confirmPassword) {
      setError('The new passwords do not match.')
      return
    }
    setSubmitting(true)
    try {
      const result = await api.changePassword(currentPassword, newPassword)
      await loginWithToken(result.token)
      navigate('/', { replace: true })
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Could not change the password. Try again.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <main className="flex min-h-[100dvh] items-center justify-center bg-slate-950 px-4 py-8 text-slate-100 sm:px-6">
      <section className="w-full max-w-md rounded-2xl border border-slate-700 bg-slate-900 p-5 shadow-elev-3 sm:p-8">
        <div className="mb-6 text-center">
          <div className="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-xl bg-primary text-primary-foreground">
            <Brain size={28} aria-hidden="true" />
          </div>
          <p className="t-meta font-medium uppercase tracking-wider text-blue-300">Secure your account</p>
          <h1 className="mt-2 text-2xl font-semibold">Choose your permanent password</h1>
          <p className="mt-2 t-body text-slate-400">
            The installer generated a temporary password. Replace it before entering your Knowz.
          </p>
        </div>

        <div className="mb-5 flex items-start gap-3 rounded-xl border border-blue-400/25 bg-blue-400/10 p-3 t-body text-slate-300">
          <KeyRound size={18} className="mt-0.5 shrink-0 text-blue-300" />
          <p>This one-time step protects the local administrator account. Your new password stays on this instance.</p>
        </div>

        <form onSubmit={submit} className="space-y-4">
          {error && (
            <div role="alert" className="flex items-start gap-2 rounded-lg border border-red-500/30 bg-red-500/10 p-3 t-body text-red-300">
              <AlertCircle size={16} className="mt-0.5 shrink-0" />
              <span>{error}</span>
            </div>
          )}

          <label className="block">
            <span className="mb-1 block t-label font-medium text-slate-200">Current password</span>
            <input
              type={showPasswords ? 'text' : 'password'}
              autoComplete="current-password"
              value={currentPassword}
              onChange={(event) => setCurrentPassword(event.target.value)}
              required
              autoFocus
              className="w-full rounded-lg border border-slate-700 bg-slate-800 px-3 py-3 text-slate-100 focus:outline-none focus:ring-2 focus:ring-primary/50"
            />
          </label>

          <label className="block">
            <span className="mb-1 block t-label font-medium text-slate-200">New password</span>
            <input
              type={showPasswords ? 'text' : 'password'}
              autoComplete="new-password"
              value={newPassword}
              onChange={(event) => setNewPassword(event.target.value)}
              required
              minLength={12}
              className="w-full rounded-lg border border-slate-700 bg-slate-800 px-3 py-3 text-slate-100 focus:outline-none focus:ring-2 focus:ring-primary/50"
            />
          </label>

          <label className="block">
            <span className="mb-1 block t-label font-medium text-slate-200">Confirm new password</span>
            <input
              type={showPasswords ? 'text' : 'password'}
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(event) => setConfirmPassword(event.target.value)}
              required
              minLength={12}
              className="w-full rounded-lg border border-slate-700 bg-slate-800 px-3 py-3 text-slate-100 focus:outline-none focus:ring-2 focus:ring-primary/50"
            />
          </label>

          <button
            type="button"
            onClick={() => setShowPasswords((value) => !value)}
            className="inline-flex min-h-11 items-center gap-2 rounded-lg px-1 t-label text-slate-300 hover:text-white focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-primary"
          >
            {showPasswords ? <EyeOff size={16} /> : <Eye size={16} />}
            {showPasswords ? 'Hide passwords' : 'Show passwords'}
          </button>

          <div className="rounded-lg bg-slate-800/70 p-3 t-meta text-slate-400">
            <p className="flex items-center gap-2">
              <CheckCircle2 size={14} className={meetsVisiblePolicy(newPassword) ? 'text-green-400' : ''} />
              12+ characters with upper/lowercase, a number, and a symbol
            </p>
          </div>

          <button
            type="submit"
            disabled={submitting}
            className="flex min-h-12 w-full items-center justify-center rounded-lg bg-primary px-4 py-3 t-label font-semibold text-primary-foreground disabled:opacity-50"
          >
            {submitting ? 'Changing password…' : 'Change password and continue'}
          </button>
        </form>
      </section>
    </main>
  )
}
