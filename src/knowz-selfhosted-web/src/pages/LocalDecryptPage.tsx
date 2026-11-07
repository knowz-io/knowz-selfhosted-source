import { ArchiveWorkbench } from '../components/portability/ArchiveWorkbench'
import { useEffect, useRef, useState } from 'react'
import { runDecrypt, SafeDecryptError, GENERIC_DECRYPT_ERROR } from '../lib/decrypt-flow'

export default function LocalDecryptPage() {
  const [messageText, setMessageText] = useState('')
  const [keyText, setKeyText] = useState('')
  const [passphrase, setPassphrase] = useState('')
  const epoch = useRef(0)
  useEffect(() => () => { epoch.current++ }, [])
  const clear = () => { epoch.current++; setPlaintext(null); setKeyText(''); setPassphrase(''); setMessageText(''); setError(null); setBusy(false) }
  const [plaintext, setPlaintext] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onUnlock() {
    const version = ++epoch.current
    setBusy(true)
    setError(null)
    setPlaintext(null)
    try {
      const result = await runDecrypt({ messageText, keyText, passphrase })
      if (epoch.current === version) setPlaintext(result.plaintext)
    } catch (err) {
      if (epoch.current === version) setError(err instanceof SafeDecryptError ? err.message : GENERIC_DECRYPT_ERROR)
    } finally {
      if (epoch.current === version) { setBusy(false); setKeyText(''); setPassphrase('') }
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
      <label className="block space-y-2"><span>Key file passphrase</span><input type="password" autoComplete="off" value={passphrase} disabled={busy} onChange={event => setPassphrase(event.target.value)} className="w-full rounded-md border border-border bg-background p-3" /></label>
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
      <button type="button" onClick={clear}>Clear recovered message</button>
      {plaintext !== null ? (
        <pre data-testid="decrypt-plaintext" className="overflow-auto rounded-md border border-border bg-card p-4 text-sm">
          {plaintext === '' ? '(Empty content)' : plaintext}
        </pre>
      ) : null}
      <ArchiveWorkbench />
    </div>
  )
}
