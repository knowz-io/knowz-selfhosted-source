import { useState, useEffect } from 'react'
import { INSTANCE_URL_PLACEHOLDER } from '../lib/instanceUrl'
import { toUserError } from '../lib/toUserError'
import { probeConnection, saveBrowserConnection, resetBrowserConnection } from '../lib/connection-profile'
import { Save, Eye, EyeOff, CheckCircle, XCircle, Loader2 } from 'lucide-react'

export default function SettingsPage() {
  const [apiUrl, setApiUrl] = useState('')
  const [apiKey, setApiKey] = useState('')
  const [showKey, setShowKey] = useState(false)
  const [saved, setSaved] = useState(false)
  const [testStatus, setTestStatus] = useState<'idle' | 'testing' | 'success' | 'error'>('idle')
  const [testError, setTestError] = useState('')
  const [reachable, setReachable] = useState<boolean | null>(null)

  useEffect(() => {
    setApiUrl(localStorage.getItem('apiUrl') || '')
    setApiKey(localStorage.getItem('apiKey') || '')
  }, [])

  const handleSave = () => {
    try { saveBrowserConnection(apiUrl, apiKey) }
    catch (error) { setTestError(toUserError(error, 'Invalid connection')); return }
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  const handleTest = async () => {
    setTestStatus('testing')
    setTestError('')
    try {
      const result = await probeConnection(apiUrl, apiKey)
      setReachable(result.reachable)
      setTestStatus(result.authenticated ? 'success' : 'error')
      setTestError(result.message)
    } catch (err) {
      setTestStatus('error')
      setTestError(toUserError(err, 'Connection failed.'))
    }
  }

  return (
    <div className="space-y-6 max-w-lg">
      <div className="space-y-4">
        <div>
          <label className="block text-sm font-medium mb-1">API URL</label>
          <input
            aria-label="API URL"
            type="text"
            value={apiUrl}
            onChange={(e) => setApiUrl(e.target.value)}
            placeholder={INSTANCE_URL_PLACEHOLDER}
            className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
          />
          <p className="text-xs text-muted-foreground mt-1">
            Leave empty to use {INSTANCE_URL_PLACEHOLDER}.
          </p>
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">API Key</label>
          <div className="relative">
            <input
              aria-label="API Key"
              type={showKey ? 'text' : 'password'}
              value={apiKey}
              onChange={(e) => setApiKey(e.target.value)}
              placeholder="Enter API key..."
              className="w-full px-3 py-2 pr-10 border border-input rounded-md bg-card text-sm"
            />
            <button
              type="button"
              onClick={() => setShowKey(!showKey)}
              aria-label={showKey ? "Hide API key" : "Show API key"}
              className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-muted-foreground hover:text-foreground transition-colors"
            >
              {showKey ? <EyeOff size={16} /> : <Eye size={16} />}
            </button>
          </div>
          <p className="text-xs text-muted-foreground mt-1">
            Required if the API has authentication enabled. Sent as X-Api-Key header.
          </p>
        </div>

        <div className="flex gap-3">
          <button
            onClick={handleSave}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium transition-colors"
          >
            <Save size={16} /> {saved ? 'Saved!' : 'Save'}
          </button>
          <button
            onClick={handleTest}
            disabled={testStatus === 'testing'}
            className="inline-flex items-center gap-2 px-4 py-2 border border-input rounded-md text-sm font-medium disabled:opacity-50 transition-colors"
          >
            {testStatus === 'testing' && <Loader2 size={16} className="animate-spin" />}
            {testStatus === 'success' && <CheckCircle size={16} className="text-green-600" />}
            {testStatus === 'error' && <XCircle size={16} className="text-red-600" />}
            {testStatus === 'idle' && null}
            Test Connection
          </button>
        </div>

        <button type="button" className="text-sm underline" onClick={() => {
          resetBrowserConnection(); setApiUrl(''); setApiKey(''); setTestStatus('idle'); setReachable(null)
        }}>Use this instance ({window.location.origin})</button>
        {reachable !== null && <p className="text-sm">Endpoint health: {reachable ? 'reachable' : 'not verified'}</p>}
        {testStatus === 'success' && (
          <p className="text-green-600 dark:text-green-400 text-sm">
            Knowz API authentication verified!
          </p>
        )}
        {testStatus === 'error' && (
          <p className="text-red-600 dark:text-red-400 text-sm">
            {testError}
          </p>
        )}
      </div>
    </div>
  )
}
