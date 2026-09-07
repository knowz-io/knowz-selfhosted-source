import { useState, useEffect, type FormEvent } from 'react'
import { APP_VERSION } from '../components/AppVersionFooter'
import { Navigate, useLocation } from 'react-router-dom'
import { Loader2, AlertCircle, Brain } from 'lucide-react'
import { useAuth } from '../lib/auth'
import { api, ApiError } from '../lib/api-client'
import { resetBrowserConnection } from '../lib/connection-profile'
import { UserRole } from '../lib/types'
import type { SSOProviderInfo, TenantMembershipDto } from '../lib/types'

type LoginStep = 'credentials' | 'tenantSelection'

const roleLabels: Record<number, string> = {
  [UserRole.SuperAdmin]: 'SuperAdmin',
  [UserRole.Admin]: 'Admin',
  [UserRole.User]: 'User',
}

const roleStyles: Record<number, string> = {
  [UserRole.SuperAdmin]: 'bg-purple-50 dark:bg-purple-950/30 text-purple-700 dark:text-purple-400',
  [UserRole.Admin]: 'bg-blue-50 dark:bg-blue-950/30 text-blue-700 dark:text-blue-400',
  [UserRole.User]: 'bg-muted text-muted-foreground',
}

export default function LoginPage() {
  const { user, login, selectTenant, isAuthenticated, isLoading: authLoading } = useAuth()
  const location = useLocation()

  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [isSubmitting, setIsSubmitting] = useState(false)
  const [ssoProviders, setSSOProviders] = useState<SSOProviderInfo[]>([])
  const [ssoLoading, setSSOLoading] = useState(false)
  const [step, setStep] = useState<LoginStep>('credentials')
  const [tenantOptions, setTenantOptions] = useState<TenantMembershipDto[]>([])
  const [hasSavedConnection, setHasSavedConnection] = useState(() => Boolean(localStorage.getItem('apiUrl') || localStorage.getItem('apiKey')))

  const resetConnection = () => {
    resetBrowserConnection()
    setHasSavedConnection(false)
    setError(''); setStep('credentials'); setUsername(''); setPassword('')
  }

  const from = (location.state as { from?: { pathname: string } })?.from?.pathname || '/'

  useEffect(() => {
    api.getSSOProviders()
      .then((res) => setSSOProviders(res.data ?? []))
      .catch(() => {
        // Silently ignore - SSO not available
      })
  }, [])

  if (authLoading) {
    return (
      <div className="flex min-h-screen items-center justify-center bg-background">
        <div className="text-center space-y-4">
          <div className="mx-auto h-10 w-10 rounded-full border-4 border-muted border-t-primary animate-spin" />
          {hasSavedConnection && <>
            <p>Reset a saved connection to this instance ({window.location.origin}).</p>
            <button type="button" className="underline" onClick={resetConnection}>Use this instance</button>
          </>}
        </div>
      </div>
    )
  }

  if (isAuthenticated) {
    return <Navigate to={user?.mustChangePassword ? '/first-login' : from} replace />
  }

  const handleSubmit = async (e: FormEvent) => {
    e.preventDefault()
    setError('')

    if (!username.trim() || !password.trim()) {
      setError('Please enter both username and password.')
      return
    }

    setIsSubmitting(true)
    try {
      const response = await login(username.trim(), password)
      if (response.requiresTenantSelection) {
        setTenantOptions(response.availableTenants)
        setStep('tenantSelection')
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
      } else {
        setError('An unexpected error occurred. Please try again.')
      }
    } finally {
      setIsSubmitting(false)
    }
  }

  const handleTenantSelect = async (tenantId: string) => {
    setIsSubmitting(true)
    setError('')
    try {
      await selectTenant(tenantId)
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
      } else {
        setError('Failed to select tenant.')
      }
    } finally {
      setIsSubmitting(false)
    }
  }

  const handleSSOLogin = async (provider: string) => {
    setSSOLoading(true)
    setError('')
    try {
      const callbackUrl = `${window.location.origin}/auth/sso/callback`
      const result = await api.getSSOAuthorizeUrl(provider, callbackUrl)
      if (result.success && result.data.authorizationUrl) {
        window.location.href = result.data.authorizationUrl
      } else {
        setError('Failed to initiate SSO login.')
        setSSOLoading(false)
      }
    } catch (err) {
      if (err instanceof ApiError) {
        setError(err.message)
      } else {
        setError('Failed to initiate SSO login.')
      }
      setSSOLoading(false)
    }
  }

  return (
    <div
      data-testid="sh-login-surface"
      className="relative flex min-h-screen items-center justify-center overflow-hidden px-4 py-10 sm:px-6"
      style={{ background: 'linear-gradient(135deg, #0f172a 0%, #1e293b 50%, #0f172a 100%)' }}
    >
      <div className="pointer-events-none absolute inset-0 bg-[radial-gradient(circle_at_top_left,rgba(59,130,246,0.16),transparent_28%),radial-gradient(circle_at_bottom_right,rgba(99,102,241,0.12),transparent_30%)]" />
      <div className="relative w-full max-w-md">
        <section className="flex items-center justify-center">
          <div className="w-full rounded-lg border border-slate-700/70 bg-slate-900/70 p-6 shadow-elev-3 sm:p-8">
            <div className="mb-8 text-center">
              <div className="mx-auto mb-5 flex h-16 w-16 items-center justify-center rounded-lg bg-primary text-primary-foreground shadow-elev-2">
                <Brain size={30} data-testid="sh-login-brain-icon" />
              </div>
              <h1
                data-testid="sh-login-wordmark"
                className="font-logo text-4xl font-normal tracking-wide text-slate-100"
                style={{ fontFamily: "'Pacifico', cursive" }}
              >
                knowz
              </h1>
              <p className="mt-2 t-body text-slate-400">
                {step === 'tenantSelection' ? 'Choose a tenant to continue' : 'Sign in to continue'}
              </p>
            </div>

            {hasSavedConnection && <div className="mb-5 text-sm text-slate-400">
              <p>Having trouble with a saved connection? Reset to this instance ({window.location.origin}) and sign in again.</p>
              <button type="button" className="mt-2 underline" onClick={resetConnection}>Use this instance</button>
            </div>}

            {step === 'tenantSelection' ? (
              <div className="space-y-3">
                {error && (
                  <div className="flex items-start gap-2 rounded-lg border border-red-500/30 bg-red-500/10 p-3">
                    <AlertCircle size={16} className="mt-0.5 shrink-0 text-red-300" />
                    <p className="t-body text-red-300">{error}</p>
                  </div>
                )}

                {tenantOptions.map((tenant) => (
                  <button
                    key={tenant.tenantId}
                    onClick={() => handleTenantSelect(tenant.tenantId)}
                    disabled={isSubmitting}
                    className="flex w-full items-center justify-between rounded-lg border border-slate-700 bg-slate-800/80 p-4 text-left text-slate-100 transition-colors hover:bg-slate-800 disabled:opacity-50"
                  >
                    <div>
                      <p className="t-label font-medium">{tenant.tenantName}</p>
                      <p className="t-meta text-slate-400">{tenant.tenantSlug}</p>
                    </div>
                    <span className={`rounded-full px-2.5 py-1 t-meta font-medium ${roleStyles[tenant.role] ?? 'bg-slate-700 text-slate-300'}`}>
                      {roleLabels[tenant.role] ?? 'User'}
                    </span>
                  </button>
                ))}

                <button
                  onClick={() => { setStep('credentials'); setError('') }}
                  className="w-full rounded-lg border border-slate-700 px-4 py-2.5 t-label font-medium text-slate-300 transition-colors hover:bg-slate-800"
                >
                  Back to login
                </button>
              </div>
            ) : (
              <>
                <form onSubmit={handleSubmit} className="space-y-4">
                  {error && (
                    <div className="flex items-start gap-2 rounded-lg border border-red-500/30 bg-red-500/10 p-3">
                      <AlertCircle size={16} className="mt-0.5 shrink-0 text-red-300" />
                      <p className="t-body text-red-300">{error}</p>
                    </div>
                  )}

                  <aside className="rounded-lg border border-blue-400/25 bg-blue-400/10 p-3 text-left text-slate-300">
                    <p className="t-label font-medium text-slate-100">First time here?</p>
                    <p className="mt-1 t-body">
                      Sign in as <code className="text-blue-200">admin</code> with the password from setup.
                    </p>
                  </aside>

                  <div>
                    <label htmlFor="username" className="mb-1 block t-label font-medium text-slate-200">
                      Username
                    </label>
                    <input
                      id="username"
                      type="text"
                      value={username}
                      onChange={(e) => setUsername(e.target.value)}
                      placeholder="Enter your username"
                      autoComplete="username"
                      autoFocus
                      disabled={isSubmitting}
                      className="w-full rounded-lg border border-slate-700 bg-slate-800/80 px-3 py-3 t-body text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-2 focus:ring-primary/40 disabled:opacity-50"
                    />
                  </div>

                  <div>
                    <label htmlFor="password" className="mb-1 block t-label font-medium text-slate-200">
                      Password
                    </label>
                    <input
                      id="password"
                      type="password"
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                      placeholder="Enter your password"
                      autoComplete="current-password"
                      disabled={isSubmitting}
                      className="w-full rounded-lg border border-slate-700 bg-slate-800/80 px-3 py-3 t-body text-slate-100 placeholder:text-slate-500 focus:outline-none focus:ring-2 focus:ring-primary/40 disabled:opacity-50"
                    />
                  </div>

                  <button
                    type="submit"
                    disabled={isSubmitting}
                    className="flex w-full items-center justify-center gap-2 rounded-lg bg-primary px-4 py-3 t-label font-semibold text-primary-foreground shadow-elev-1 transition-colors hover:brightness-110 disabled:cursor-not-allowed disabled:opacity-50"
                  >
                    {isSubmitting ? (
                      <>
                        <Loader2 size={16} className="animate-spin" />
                        Signing in...
                      </>
                    ) : (
                      'Sign In'
                    )}
                  </button>
                </form>

                {ssoProviders.length > 0 && (
                  <>
                    <div className="relative my-5">
                      <div className="absolute inset-0 flex items-center">
                        <div className="w-full border-t border-slate-700" />
                      </div>
                      <div className="relative flex justify-center t-meta">
                        <span className="bg-slate-900 px-3 text-slate-400">or continue with SSO</span>
                      </div>
                    </div>

                    <div className="space-y-2">
                      {ssoProviders.map((provider) => (
                        <button
                          key={provider.provider}
                          type="button"
                          onClick={() => handleSSOLogin(provider.provider)}
                          disabled={ssoLoading}
                          className="flex w-full items-center justify-center gap-2 rounded-lg border border-slate-700 bg-slate-800/70 px-4 py-3 t-label font-medium text-slate-100 transition-colors hover:bg-slate-800 disabled:cursor-not-allowed disabled:opacity-50"
                        >
                          {ssoLoading ? (
                            <Loader2 size={16} className="animate-spin" />
                          ) : provider.provider === 'Microsoft' ? (
                            <svg width="16" height="16" viewBox="0 0 21 21" fill="none" xmlns="http://www.w3.org/2000/svg">
                              <rect x="1" y="1" width="9" height="9" fill="#F25022"/>
                              <rect x="11" y="1" width="9" height="9" fill="#7FBA00"/>
                              <rect x="1" y="11" width="9" height="9" fill="#00A4EF"/>
                              <rect x="11" y="11" width="9" height="9" fill="#FFB900"/>
                            </svg>
                          ) : null}
                          {provider.displayName}
                        </button>
                      ))}
                    </div>
                  </>
                )}
              </>
            )}

            <p data-testid="sh-version-footer" className="mt-8 text-center t-meta text-slate-500">
              Knowz Self-Hosted &middot; v{APP_VERSION}
            </p>
          </div>
        </section>
      </div>
    </div>
  )
}
