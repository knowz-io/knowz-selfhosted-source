import { useState } from 'react'
import { runDecrypt } from '../lib/decrypt-flow'

export default function LocalDecryptPage() {
  const [messageText, setMessageText] = useState('')
  const [keyText, setKeyText] = useState('')
  const [plaintext, setPlaintext] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onUnlock() {
    setBusy(true)
    setError(null)
    setPlaintext(null)
    try {
      const result = await runDecrypt({ messageText, keyText })
      setPlaintext(result.plaintext)
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Could not decrypt.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="mx-auto max-w-3xl space-y-6 p-6">
      <div>
        <h1 className="text-2xl font-semibold">Unlock encrypted knowledge</h1>
        <p className="mt-1 text-sm text-muted-foreground">
          Decrypt happens only in this browser tab. The private key is not stored.
        </p>
      </div>
      <label className="block space-y-2">
        <span className="text-sm font-medium">Encrypted message</span>
        <textarea
          data-testid="decrypt-message"
          className="min-h-[8rem] w-full rounded-md border border-border bg-background p-3 font-mono text-sm"
          value={messageText}
          onChange={(event) => setMessageText(event.target.value)}
        />
      </label>
      <label className="block space-y-2">
        <span className="text-sm font-medium">Private key or .knowzkey</span>
        <textarea
          data-testid="decrypt-key"
          className="min-h-[4rem] w-full rounded-md border border-border bg-background p-3 font-mono text-sm"
          value={keyText}
          onChange={(event) => setKeyText(event.target.value)}
        />
      </label>
      <button
        type="button"
        data-testid="decrypt-submit"
        className="rounded-md bg-primary px-4 py-2 text-sm text-primary-foreground"
        disabled={busy}
        onClick={() => void onUnlock()}
      >
        {busy ? 'Unlocking…' : 'Unlock'}
      </button>
      {error ? (
        <p data-testid="decrypt-error" className="text-sm text-destructive">
          {error}
        </p>
      ) : null}
      {plaintext ? (
        <pre data-testid="decrypt-plaintext" className="overflow-auto rounded-md border border-border bg-card p-4 text-sm">
          {plaintext}
        </pre>
      ) : null}
    </div>
  )
}
