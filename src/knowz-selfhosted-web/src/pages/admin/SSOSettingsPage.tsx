import { useState, useEffect, type FormEvent } from 'react'
import { Shield, Loader2, CheckCircle, XCircle, AlertCircle, ChevronDown, ChevronUp } from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import type { SelfHostedSSOConfigDto, SelfHostedSSOTestResultDto } from '../../lib/types'

const MODE_LABELS: Record<string, string> = {
  PkcePublicClient: 'PKCE Public Client',
  ConfidentialClient: 'Confidential Client',
  Disabled: 'Not Configured',
}

const MODE_STYLES: Record<string, string> = {
  PkcePublicClient: 'bg-blue-100 dark:bg-blue-950/40 text-blue-700 dark:text-blue-400',
  ConfidentialClient: 'bg-green-100 dark:bg-green-950/40 text-green-700 dark:text-green-400',
  Disabled: 'bg-muted text-muted-foreground',
}

export default function SSOSettingsPage() {
  const [config, setConfig] = useState<SelfHostedSSOConfigDto | null>(null)
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [testing, setTesting] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [testResult, setTestResult] = useState<SelfHostedSSOTestResultDto | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [success, setSuccess] = useState<string | null>(null)
  const [showInstructions, setShowInstructions] = useState(false)

  // Form state
  const [isEnabled, setIsEnabled] = useState(false)
  const [clientId, setClientId] = useState('')
  const [clientSecret, setClientSecret] = useState('')
  const [secretChanged, setSecretChanged] = useState(false)
  const [directoryTenantId, setDirectoryTenantId] = useState('')
  const [autoProvisionUsers, setAutoProvisionUsers] = useState(false)
  const [defaultRole, setDefaultRole] = useState('User')

  useEffect(() => {
    loadConfig()
  }, [])

  const loadConfig = async () => {
    try {
      const data = await api.getSSOConfig()
      setConfig(data)
      setIsEnabled(data.isEnabled)
      setClientId(data.clientId ?? '')
      setDirectoryTenantId(data.directoryTenantId ?? '')
      setAutoProvisionUsers(data.autoProvisionUsers)
      setDefaultRole(data.defaultRole)
      setClientSecret('')
      setSecretChanged(false)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load SSO configuration')
    } finally {
      setLoading(false)
    }
  }

  const handleSave = async (e: FormEvent) => {
    e.preventDefault()
    setError(null)
    setSuccess(null)
    setSaving(true)

    try {
      const data = await api.updateSSOConfig({
        isEnabled,
        clientId: clientId || null,
        clientSecret: secretChanged ? clientSecret : null,
        directoryTenantId: directoryTenantId || null,
        autoProvisionUsers,
        defaultRole,
      })
      setConfig(data)
      setSecretChanged(false)
      setClientSecret('')
      setSuccess('SSO configuration saved successfully.')
      setTimeout(() => setSuccess(null), 3000)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to save SSO configuration')
    } finally {
      setSaving(false)
    }
  }

  const handleTest = async () => {
    setTestResult(null)
    setTesting(true)
    try {
      const result = await api.testSSOConnection()
      setTestResult(result)
    } catch (err) {
      setTestResult({
        success: false,
        detectedMode: null,
        errorMessage: err instanceof ApiError ? err.message : 'Test failed',
        status: null,
        validTenantIds: null,
        testedAt: new Date().toISOString(),
      })
    } finally {
      setTesting(false)
    }
  }

  const handleDelete = async () => {
    if (!confirm('Are you sure you want to clear all SSO configuration? This will disable SSO login.')) return
    setDeleting(true)
    setError(null)
    try {
      await api.deleteSSOConfig()
      await loadConfig()
      setSuccess('SSO configuration cleared.')
      setTimeout(() => setSuccess(null), 3000)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to clear SSO configuration')
    } finally {
      setDeleting(false)
    }
  }

  const detectedMode = config?.detectedMode ?? 'Disabled'

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <Loader2 size={24} className="animate-spin text-muted-foreground" />
      </div>
    )
  }

  return (
    <div className="max-w-2xl mx-auto space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <Shield size={24} className="text-muted-foreground" />
          <div>
            <h1 className="text-xl font-semibold text-foreground">Single Sign-On (SSO)</h1>
            <p className="text-sm text-muted-foreground">Configure Microsoft Entra ID login</p>
          </div>
        </div>
        <span className={`px-2.5 py-1 rounded-full text-xs font-medium ${MODE_STYLES[detectedMode] ?? MODE_STYLES.Disabled}`}>
          {MODE_LABELS[detectedMode] ?? detectedMode}
        </span>
      </div>

      {/* Alerts */}
      {error && (
        <div className="flex items-start gap-2 p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded-lg">
          <AlertCircle size={16} className="text-red-600 dark:text-red-400 mt-0.5 shrink-0" />
          <p className="text-sm text-red-600 dark:text-red-400">{error}</p>
        </div>
      )}
      {success && (
        <div className="flex items-start gap-2 p-3 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-800 rounded-lg">
          <CheckCircle size={16} className="text-green-600 dark:text-green-400 mt-0.5 shrink-0" />
          <p className="text-sm text-green-600 dark:text-green-400">{success}</p>
        </div>
      )}

      <form onSubmit={handleSave} className="space-y-6">
        {/* Enable Toggle */}
        <div className="bg-card border border-border/60 rounded-xl shadow-sm p-5">
          <label className="flex items-center justify-between cursor-pointer">
            <div>
              <span className="text-sm font-medium text-foreground">Enable SSO</span>
              <p className="text-xs text-muted-foreground mt-0.5">Show SSO login button on the sign-in page</p>
            </div>
            <input
              type="checkbox"
              checked={isEnabled}
              onChange={(e) => setIsEnabled(e.target.checked)}
              className="w-5 h-5 rounded border-input text-primary focus:ring-ring"
            />
          </label>
        </div>

        {/* Connection Details */}
        <div className="bg-card border border-border/60 rounded-xl shadow-sm p-5 space-y-4">
          <h2 className="text-sm font-semibold text-foreground">Connection Details</h2>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Application (Client) ID
            </label>
            <input
              type="text"
              value={clientId}
              onChange={(e) => setClientId(e.target.value)}
              placeholder="00000000-0000-0000-0000-000000000000"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm text-foreground placeholder-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Client Secret <span className="text-muted-foreground font-normal">(optional for PKCE mode)</span>
            </label>
            <input
              type="password"
              value={clientSecret}
              onChange={(e) => { setClientSecret(e.target.value); setSecretChanged(true) }}
              placeholder={config?.hasClientSecret ? '(secret configured - leave empty to keep)' : 'Enter client secret or leave empty for PKCE mode'}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm text-foreground placeholder-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
            <p className="text-xs text-muted-foreground mt-1">
              Without a client secret, SSO uses PKCE public client flow. Directory Tenant ID is required in this mode.
            </p>
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Directory (Tenant) ID(s)
            </label>
            <textarea
              value={directoryTenantId}
              onChange={(e) => setDirectoryTenantId(e.target.value)}
              placeholder="e.g., 11111111-1111-1111-1111-111111111111"
              rows={2}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm text-foreground placeholder-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
            <p className="text-xs text-muted-foreground mt-1">
              Single GUID or comma-separated GUIDs for multi-org access. Required for PKCE mode.
            </p>
          </div>

          {/* Test Connection */}
          <div className="pt-2">
            <button
              type="button"
              onClick={handleTest}
              disabled={testing}
              className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium text-foreground hover:bg-muted transition-colors disabled:opacity-50"
            >
              {testing ? <Loader2 size={14} className="animate-spin" /> : null}
              Test Connection
            </button>
            {testResult && (
              <div className={`mt-2 flex items-start gap-2 p-2.5 rounded-md text-sm ${
                testResult.success
                  ? 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400'
                  : 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400'
              }`}>
                {testResult.success ? <CheckCircle size={14} className="mt-0.5 shrink-0" /> : <XCircle size={14} className="mt-0.5 shrink-0" />}
                <div>
                  <p>{testResult.status ?? testResult.errorMessage ?? (testResult.success ? 'Connection successful' : 'Connection failed')}</p>
                  {testResult.detectedMode && (
                    <p className="text-xs opacity-75 mt-0.5">Mode: {testResult.detectedMode}</p>
                  )}
                </div>
              </div>
            )}
          </div>
        </div>

        {/* Access Control */}
        <div className="bg-card border border-border/60 rounded-xl shadow-sm p-5 space-y-4">
          <h2 className="text-sm font-semibold text-foreground">Access Control</h2>

          <label className="flex items-center justify-between cursor-pointer">
            <div>
              <span className="text-sm font-medium text-foreground">Auto-Provision Users</span>
              <p className="text-xs text-muted-foreground mt-0.5">Automatically create accounts for new SSO users</p>
            </div>
            <input
              type="checkbox"
              checked={autoProvisionUsers}
              onChange={(e) => setAutoProvisionUsers(e.target.checked)}
              className="w-5 h-5 rounded border-input text-primary focus:ring-ring"
            />
          </label>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Default Role for New Users
            </label>
            <select
              value={defaultRole}
              onChange={(e) => setDefaultRole(e.target.value)}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm text-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            >
              <option value="User">User</option>
              <option value="Admin">Admin</option>
              <option value="SuperAdmin">SuperAdmin</option>
            </select>
          </div>
        </div>

        {/* Setup Instructions */}
        <div className="bg-card border border-border/60 rounded-xl shadow-sm">
          <button
            type="button"
            onClick={() => setShowInstructions(!showInstructions)}
            className="w-full flex items-center justify-between p-5 text-left"
          >
            <h2 className="text-sm font-semibold text-foreground">Azure Portal Setup Instructions</h2>
            {showInstructions ? <ChevronUp size={16} className="text-muted-foreground" /> : <ChevronDown size={16} className="text-muted-foreground" />}
          </button>
          {showInstructions && (
            <div className="px-5 pb-5 space-y-4 text-sm text-muted-foreground">
              <div>
                <h3 className="font-medium text-foreground mb-2">PKCE Public Client Mode (simpler, no secret needed)</h3>
                <ol className="list-decimal list-inside space-y-1">
                  <li>Go to Azure Portal &gt; Entra ID &gt; App registrations &gt; New registration</li>
                  <li>Name: "Knowz Self-Hosted SSO"</li>
                  <li>Supported account types: "Accounts in this organizational directory only"</li>
                  <li>Redirect URI: Select "Single-page application (SPA)" and enter <code className="bg-muted px-1 rounded">{window.location.origin}/auth/sso/callback</code></li>
                  <li>After creation: Copy the Application (client) ID</li>
                  <li>Go to Authentication &gt; Under "Advanced settings" &gt; Enable "Allow public client flows" = Yes</li>
                  <li>Enter the Client ID and your Entra Tenant ID in the form above</li>
                  <li>No client secret needed</li>
                </ol>
              </div>
              <div>
                <h3 className="font-medium text-foreground mb-2">Confidential Client Mode (full app registration)</h3>
                <ol className="list-decimal list-inside space-y-1">
                  <li>Go to Azure Portal &gt; Entra ID &gt; App registrations &gt; New registration</li>
                  <li>Name: "Knowz Self-Hosted SSO"</li>
                  <li>Supported account types: "Accounts in this organizational directory only"</li>
                  <li>Redirect URI: Select "Web" and enter <code className="bg-muted px-1 rounded">{window.location.origin}/auth/sso/callback</code></li>
                  <li>After creation: Copy the Application (client) ID</li>
                  <li>Go to Certificates &amp; secrets &gt; New client secret &gt; Copy the secret value</li>
                  <li>Enter the Client ID, Client Secret, and your Entra Tenant ID in the form above</li>
                </ol>
              </div>
            </div>
          )}
        </div>

        {/* Actions */}
        <div className="flex items-center justify-between">
          <button
            type="button"
            onClick={handleDelete}
            disabled={deleting}
            className="inline-flex items-center gap-2 px-3 py-2 border border-red-300 dark:border-red-700 rounded-md text-sm font-medium text-red-700 dark:text-red-400 hover:bg-red-50 dark:hover:bg-red-950/30 transition-colors disabled:opacity-50"
          >
            {deleting ? <Loader2 size={14} className="animate-spin" /> : null}
            Clear Configuration
          </button>
          <button
            type="submit"
            disabled={saving}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50"
          >
            {saving ? <Loader2 size={14} className="animate-spin" /> : null}
            Save Configuration
          </button>
        </div>
      </form>
    </div>
  )
}
