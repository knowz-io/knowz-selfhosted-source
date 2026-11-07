import { useEffect, useRef, useState } from 'react';
import { PortableCryptoError } from '../../lib/vendored/portable/portable';
import type { ArchivePeerBinding, ArchivePeerReceipt, ArchivePeerReview, ArchivePeerTransport } from '../../lib/archive-peer-api';
import './archive-workbench.css';
export interface SavedArchiveLink {
  id: string; localVaultId: string; remoteVaultId: string; destinationBaseUrl: string; destinationLabel: string;
  enabled: boolean; directions: readonly ('pull' | 'push')[];
}
interface Props { link: SavedArchiveLink; scopeKey: string; localSourceBaseUrl: string; transport: ArchivePeerTransport; onPreserved?: (receipt: ArchivePeerReceipt) => void }
const address = (value: string) => { try { return new URL(value).href.replace(/\/+$/, ''); } catch { return ''; } };
export function SavedArchivePeerPanel({ link, scopeKey, localSourceBaseUrl, transport, onPreserved }: Props) {
  const [review, setReview] = useState<ArchivePeerReview | null>(null); const [consent, setConsent] = useState(false);
  const [localAddress, setLocalAddress] = useState(localSourceBaseUrl); const [busy, setBusy] = useState('');
  const [status, setStatus] = useState(''); const [error, setError] = useState(''); const [receipt, setReceipt] = useState<ArchivePeerReceipt | null>(null);
  const operation = useRef<AbortController | null>(null); const epoch = useRef(0);
  const run = async (label: string, work: (signal: AbortSignal, current: () => boolean) => Promise<void>) => {
    if (operation.current) return; const controller = new AbortController(); operation.current = controller; const version = ++epoch.current;
    const current = () => version === epoch.current && !controller.signal.aborted;
    setBusy(label); setError(''); setStatus(''); setReceipt(null);
    try { await work(controller.signal, current); }
    catch (failure) { if (current()) setError(failure instanceof PortableCryptoError ? failure.message : 'The complete archive operation did not finish. Review the saved link and destination before trying again.'); }
    finally { if (current()) { operation.current = null; setBusy(''); setConsent(false); } }
  };
  const refresh = () => run('Reviewing saved peer registration…', async (signal, current) => {
    const result = await transport.review(link.id, signal); if (!current()) return;
    setReview(result); setLocalAddress(result.binding?.localSourceBaseUrl ?? localSourceBaseUrl);
  });
  const pairMatches = (binding: ArchivePeerBinding) => binding.destinationVaultId === link.localVaultId && binding.sourceVaultId === link.remoteVaultId
    && address(binding.sourceBaseUrl) === address(link.destinationBaseUrl);
  useEffect(() => {
    ++epoch.current; operation.current?.abort(); operation.current = null; setReview(null); setConsent(false); setReceipt(null); setStatus(''); setError(''); setLocalAddress(localSourceBaseUrl);
    void refresh();
    return () => { ++epoch.current; operation.current?.abort(); operation.current = null; };
  }, [scopeKey, link.id, link.localVaultId, link.remoteVaultId, link.destinationBaseUrl, link.enabled, transport]);
  const binding = review?.binding; const ready = review?.status === 'registered' && !!binding && pairMatches(binding) && binding.isEnabled;
  const register = () => consent && run('Registering complete archive transport…', async (signal, current) => {
    const result = await transport.register(link.id, { localSourceBaseUrl: localAddress, ...(binding ? { expectedBindingRevision: binding.bindingRevision } : {}) }, signal);
    if (!current()) return; if (!pairMatches(result)) throw new PortableCryptoError('peer-scope', 'Registration does not match this saved vault pair. Refresh and review it.');
    setReview({ status: 'registered', binding: result }); setStatus('Complete archive registration saved.');
  });
  const transfer = (direction: 'pull' | 'push') => ready && binding && consent && run(`Transferring complete archive (${direction})…`, async (signal, current) => {
    const result = await transport.transfer(link.id, direction, binding, signal); if (!current()) return;
    setReceipt(result); onPreserved?.(result);
  });
  const cancel = () => { const transferPending = busy.startsWith('Transferring'); ++epoch.current; operation.current?.abort(); operation.current = null; setBusy(''); setConsent(false); setReceipt(null);
    setStatus(transferPending ? 'Stopped waiting. The transfer may have committed; inspect the destination before starting another run.' : 'Stopped waiting. Refresh registration before resuming this operation.'); };
  return <section className="archive-workbench" aria-label={`Complete archive link ${link.destinationLabel}`}>
    <h3>Complete vault transfer</h3><p>Transfer this saved vault pair with its bodies, protected fields, files and recoverable history. Original keys stay local; source records are retained.</p>
    <p>Local vault <code>{link.localVaultId}</code> · {link.destinationLabel} vault <code>{link.remoteVaultId}</code></p>
    {busy && <p role="status">{busy}</p>}{error && <p role="alert" className="archive-error">{error}</p>}{status && <p role="status">{status}</p>}
    {review?.status === 'not-registered' && <p>This saved link is not registered for complete archives.</p>}
    {review?.status === 'pending' && <p>Registration is pending. Review and resume the same binding revision.</p>}
    {review?.status === 'review-required' && <p>Registration changed and requires explicit review before another transfer.</p>}
    {ready && <p>Registered for complete archives. The destination signer was authenticated through saved credentials on first use.</p>}
    {binding && <details><summary>Review public registration</summary><dl><dt>Signer fingerprint</dt><dd><code>{binding.signerFingerprint}</code></dd><dt>Binding revision</dt><dd>{binding.bindingRevision}</dd><dt>Trust</dt><dd>Transport-authenticated first-use trust</dd></dl></details>}
    {review?.status === 'registered' && binding && !pairMatches(binding) && <p role="alert">Registration does not match this saved vault pair. Refresh and review it.</p>}
    {review && <><label>This instance API URL, reachable by the destination<input aria-label="This instance API URL" value={localAddress} disabled={!!busy} onChange={event => { setLocalAddress(event.target.value); setConsent(false); }} /></label>
      <label className="archive-check"><input type="checkbox" checked={consent} disabled={!!busy || !link.enabled} onChange={event => setConsent(event.target.checked)} />I reviewed this destination and vault pair</label>
      <div className="archive-actions"><button type="button" disabled={!!busy || !consent || !link.enabled || !localAddress} onClick={() => void register()}>{review.status === 'pending' ? 'Resume complete registration' : ready ? 'Review and re-register archives' : 'Register complete archives'}</button>
        {ready && link.directions.map(direction => <button type="button" key={direction} disabled={!!busy || !consent || !link.enabled} onClick={() => void transfer(direction)}>{direction === 'pull' ? 'Pull complete archive' : 'Push complete archive'}</button>)}</div></>}
    <div className="archive-actions"><button type="button" disabled={!!busy} onClick={() => void refresh()}>Refresh registration</button>{busy && <button type="button" onClick={cancel}>Cancel wait</button>}</div>
    {receipt && <p role="status">Server preserved {receipt.preservedPayloadCount}/{receipt.requiredPayloadCount} payloads for this registered link. Local preservation and decryption have not been verified. Open the destination with the original keys and keep a separate verification receipt.</p>}
    {link.directions.length > 1 && <p>Pull and Push are separate commits. Run them explicitly in order; they are not an atomic two-way transfer.</p>}
  </section>;
}
