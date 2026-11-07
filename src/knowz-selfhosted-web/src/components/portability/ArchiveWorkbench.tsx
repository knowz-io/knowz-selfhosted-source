import { useEffect, useRef, useState } from 'react';
import { PortableCryptoError, PortableKeyring, PORTABLE_LIMITS, decryptPayload, recoverLegacyPayload, verifyAllArchivePayloads, validateArchivePlanBinding, type LegacyAttribution, type OpenedPortableArchive, type PortablePayload, type PortableVerificationResult } from '../../lib/vendored/portable/portable';
import { openBrowserArchive, protectBrowserArchive, saveLocalDownload } from '../../lib/archive-browser';
import type { ArchivePlan, ArchiveReceipt, ArchiveTransport } from './archive-types';
import './archive-workbench.css';
export interface ArchiveWorkbenchProps { transport?: ArchiveTransport; scopeKey?: string; vaultId?: string; openArchive?: typeof openBrowserArchive }
const safeError = (error: unknown) => error instanceof PortableCryptoError ? error.message : 'The operation could not be completed. Check the archive and your access, then retry.';
const stableJson = (value: unknown) => JSON.stringify(value, (_key, child) => child && typeof child === 'object' && !Array.isArray(child) ? Object.fromEntries(Object.keys(child).sort().map(key => [key, child[key]])) : child);
export function ArchiveWorkbench({ transport, scopeKey = 'offline', vaultId, openArchive = openBrowserArchive }: ArchiveWorkbenchProps) {
  const [file, setFile] = useState<File | null>(null); const [password, setPassword] = useState(''); const [pin, setPin] = useState('');
  const [opened, setOpened] = useState<OpenedPortableArchive | null>(null); const [blob, setBlob] = useState<Blob | null>(null);
  const [busy, setBusy] = useState(''); const [error, setError] = useState(''); const [status, setStatus] = useState('');
  const [context, setContext] = useState(''); const [keyText, setKeyText] = useState(''); const [keyLoaded, setKeyLoaded] = useState(false); const [keyPassphrase, setKeyPassphrase] = useState(''); const [derivation, setDerivation] = useState('');
  const [unlocked, setUnlocked] = useState<string[]>([]); const [verification, setVerification] = useState<PortableVerificationResult | null>(null);
  const [legacyCandidates, setLegacyCandidates] = useState<string[]>([]); const [legacyAttribution, setLegacyAttribution] = useState<LegacyAttribution | null>(null);
  const [selected, setSelected] = useState(0); const [plaintext, setPlaintext] = useState<string | null>(null);
  const [allowPartial, setAllowPartial] = useState(false); const [strategy, setStrategy] = useState('skip'); const [target, setTarget] = useState(vaultId ?? '');
  const [plan, setPlan] = useState<ArchivePlan | null>(null); const [reviewed, setReviewed] = useState(false); const [receipt, setReceipt] = useState<ArchiveReceipt | null>(null);
  const [protect, setProtect] = useState(false); const [exportPassword, setExportPassword] = useState(''); const [confirmPassword, setConfirmPassword] = useState('');
  const operation = useRef<AbortController | null>(null); const epoch = useRef(0); const ring = useRef<PortableKeyring | null>(null); const activeArchive = useRef<OpenedPortableArchive | null>(null); const planRef = useRef<ArchivePlan | null>(null); const keyInput = useRef<HTMLInputElement>(null); const fileInput = useRef<HTMLInputElement>(null);
  const invalidatePlan = () => { const old = planRef.current; planRef.current = null; setPlan(null); setReviewed(false); setReceipt(null); if (old) void transport?.cancel(old.planId).catch(() => {}); };
  const cancel = () => { setLegacyAttribution(null); const pendingCommit = busy === 'Preserving archive in the destination…'; ++epoch.current; operation.current?.abort(); operation.current = null; setBusy(''); setPassword(''); setKeyText(''); setKeyLoaded(false); setKeyPassphrase(''); setDerivation(''); setExportPassword(''); setConfirmPassword(''); ring.current?.lock(); setPlaintext(null); setVerification(null); setStatus(pendingCommit ? 'Stopped waiting for the destination. The import may have committed; retry this reviewed plan to obtain its receipt before starting another import.' : 'Cancelled. No incomplete local result was accepted.'); };
  const clear = () => { cancel(); setLegacyCandidates([]); ring.current?.dispose(); ring.current = null; void activeArchive.current?.close(); activeArchive.current = null; setOpened(null); setBlob(null); setFile(null); if (fileInput.current) fileInput.current.value = ''; if (keyInput.current) keyInput.current.value = ''; setUnlocked([]); setError(''); setStatus(''); invalidatePlan(); };
  useEffect(() => { clear(); return () => { ++epoch.current; operation.current?.abort(); ring.current?.dispose(); void activeArchive.current?.close(); const old = planRef.current; if (old) void transport?.cancel(old.planId).catch(() => {}); }; }, [scopeKey]); // Account/tenant/session changes invalidate all local secrets and work.
  const run = async (label: string, work: (signal: AbortSignal, current: () => boolean) => Promise<void>) => {
    if (operation.current) return; const controller = new AbortController(); operation.current = controller; const version = ++epoch.current;
    const current = () => version === epoch.current && !controller.signal.aborted;
    setBusy(label); setError(''); setStatus('');
    try { await work(controller.signal, current); }
    catch (failure) { if (current()) { ring.current?.lock(); setPlaintext(null); setVerification(null); setError(safeError(failure)); } }
    finally { if (current()) { operation.current = null; setBusy(''); setPassword(''); setKeyText(''); setKeyLoaded(false); setKeyPassphrase(''); setDerivation(''); setExportPassword(''); setConfirmPassword(''); if (keyInput.current) keyInput.current.value = ''; } }
  };
  const inspect = () => file && run('Verifying archive inventory…', async (signal, current) => {
    ring.current?.dispose(); void activeArchive.current?.close(); activeArchive.current = null; setOpened(null); setBlob(null); setPlaintext(null); setVerification(null); invalidatePlan();
    const result = await openArchive(file, password, { expectedSignerFingerprint: pin.trim() || undefined, signal, onProgress: (done, total) => { if (current()) setBusy(`Verifying inventory ${done}/${total}…`); } });
    if (!current()) { await result.archive.close(); return; }
    const keys = new PortableKeyring({ userId: scopeKey, sessionId: crypto.randomUUID(), tenantId: result.archive.manifest.sourceTenantId }, result.archive.graph.catalog);
    ring.current = keys; keys.subscribe(() => { if (ring.current !== keys) return; setUnlocked(keys.snapshot().unlocked); setPlaintext(null); setVerification(null); });
    activeArchive.current = result.archive; setOpened(result.archive); setBlob(result.blob); setSelected(0); setContext(result.archive.graph.catalog[0]?.contextId ?? ''); setUnlocked([]);
  });
  const verify = () => opened && ring.current && run('Verifying all encrypted payloads…', async (signal, current) => {
    const result = await verifyAllArchivePayloads(opened.graph.payloads, ring.current!, { readBinary: opened.readBinary, legacyCandidates: opened.graph.catalog.filter(key => legacyCandidates.includes(`${key.contextId}:${key.generation}:${key.fingerprint}`)), signal, onProgress: (_, done, total) => { if (current()) setBusy(`Decrypting payload ${done}/${total}…`); } });
    if (current()) setVerification(result);
  });
  const view = (payload: PortablePayload, download = false) => opened && ring.current && run('Decrypting locally…', async (signal, current) => {
    setPlaintext(null); const binary = payload.binaryEntryPath ? await opened.readBinary(payload.binaryEntryPath, signal) : undefined;
    let bytes: Uint8Array | undefined;
    try {
      if (payload.reference.legacyUnattributed) {
        const recovered = await recoverLegacyPayload(payload, ring.current!, { candidates: opened.graph.catalog.filter(key => key.contextId === payload.reference.contextId && legacyCandidates.includes(`${key.contextId}:${key.generation}:${key.fingerprint}`)), binary, signal });
        bytes = recovered.plaintext; if (current()) setLegacyAttribution(recovered.attribution);
      } else bytes = await decryptPayload(payload, ring.current!, { binary, signal });
      if (!current()) return;
      if (download || payload.reference.role === 'binary' || payload.reference.role === 'variant') saveLocalDownload(new Blob([new Uint8Array(bytes)], { type: 'application/octet-stream' }), `decrypted-${payload.reference.ownerId}.bin`);
      else setPlaintext(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    } finally { bytes?.fill(0); binary?.fill(0); }
  });
  const makePlan = () => transport && blob && opened && run('Preparing import plan…', async (signal, current) => {
    const requiredOrdinaryFileBytes = (opened.graph.plainData.fileRecords ?? []).reduce<number>((largest, file) => Math.max(largest, Number((file as Record<string, unknown>).sizeBytes)), 0);
    invalidatePlan(); const prepared = await transport.plan(blob, { strategy, targetVaultId: target || undefined, allowPartial, expectedSignerFingerprint: opened.sourceTrust === 'trusted' ? opened.signerFingerprint : undefined, idempotencyKey: crypto.randomUUID(), requiredOrdinaryFileBytes }, signal);
    if (!current()) { void transport.cancel(prepared.planId).catch(() => {}); return; }
    try { validateArchivePlanBinding(opened, prepared, { allowPartial, validateOnly: true }); }
    catch (failure) { await transport.cancel(prepared.planId).catch(() => {}); throw failure; }
    planRef.current = prepared; setPlan(prepared);
  });
  const commit = () => transport && plan && reviewed && run('Preserving archive in the destination…', async (signal, current) => {
    if (opened) validateArchivePlanBinding(opened, plan, { allowPartial });
    const result = await transport.commit(plan.planId, signal);
    if (!current()) return;
    if (opened) validateArchivePlanBinding(opened, result, { allowPartial });
    const omissions = result.omissions.map(stableJson);
    const keepsReviewedOmissions = plan.omissions.every(omission => { const index = omissions.indexOf(stableJson(omission)); if (index < 0) return false; omissions.splice(index, 1); return true; });
    if (!keepsReviewedOmissions || plan.preservationState !== 'complete' && result.preservationState === 'complete' || result.archiveId !== plan.archiveId || result.sourceSnapshot !== plan.sourceSnapshot || result.requiredPayloadCount !== plan.requiredPayloadCount || result.sourceTrust !== plan.sourceTrust || result.planId !== plan.planId || result.manifestSha256 !== plan.manifestSha256 || result.mappingSha256 !== plan.mappingSha256 || result.destinationTenantId !== plan.destinationTenantId) throw new PortableCryptoError('receipt-mismatch', 'The destination receipt does not match this reviewed plan.');
    planRef.current = null; setPlan(null); setReceipt(result);
  });
  const exportArchive = () => transport && run('Exporting archive…', async (signal, current) => {
    if (protect && (!exportPassword || exportPassword !== confirmPassword)) throw new PortableCryptoError('password-confirmation', 'Enter and confirm the same archive password.');
    const output = await transport.exportArchive({ vaultId, allowPartial }, signal); if (!current()) return;
    const checked = await openArchive(output.blob, '', { expectedSignerFingerprint: output.signerFingerprint, signal });
    try {
      const download = protect ? await protectBrowserArchive(output.blob, exportPassword, signal) : output.blob;
      if (!current()) return;
      saveLocalDownload(download, `knowz-${vaultId ? 'vault' : 'tenant'}-${checked.archive.manifest.inventory.archiveId}.knowzarchive${protect ? '.age' : ''}`);
      saveLocalDownload(new Blob([JSON.stringify(output.receipt ?? { archiveId: checked.archive.manifest.inventory.archiveId, manifestSha256: checked.archive.manifestSha256, signerFingerprint: output.signerFingerprint, sourceSnapshot: checked.archive.manifest.inventory.snapshotBoundary }, null, 2)], { type: 'application/json' }), 'knowz-source-receipt.json');
      setStatus('Archive and independent source receipt downloaded. Keep the receipt separately. Your original keys are still needed to decrypt sealed content.');
    } finally { await checked.archive.close(); }
  });
  const dependentCount = opened?.graph.dependentPayloadCount ?? 0;
  const policies = opened?.graph.suspendedReleasePolicies ?? [];
  const fullRecovery = verification?.complete && opened?.manifest.preservationState === 'complete' && dependentCount === 0 && !policies.some(policy => policy.recipientEnvelopeJson);
  const payload = opened?.graph.payloads[selected];
  const ordinaryFiles = (opened?.graph.plainData.fileRecords ?? []).filter((value): value is Record<string, unknown> => !!value && typeof value === 'object' && !Array.isArray(value));
  const downloadOrdinaryFile = (file: Record<string, unknown>) => opened && run('Reading ordinary file bytes…', async (signal, current) => {
    if (typeof file.binaryEntryPath !== 'string') throw new PortableCryptoError('file-missing', 'This ordinary file has no verified archive binary.');
    const name = payload?.reference.ownerKind === 'file' && payload.reference.ownerId === file.id && payload.reference.fieldName === 'fileName' && plaintext ? plaintext : typeof file.fileName === 'string' && file.fileName !== 'encrypted-file' ? file.fileName : `download-${file.id}.bin`;
    const picker = (globalThis as typeof globalThis & { showSaveFilePicker?: (options: { suggestedName: string }) => Promise<{ createWritable(): Promise<WritableStream<Uint8Array>> }> }).showSaveFilePicker;
    if (picker) {
      const handle = await picker({ suggestedName: name }); signal.throwIfAborted();
      const writable = await handle.createWritable();
      try {
        signal.throwIfAborted();
        // Native file writers publish their temporary bytes on close. The member
        // stream closes only after its full length/hash/ZIP checks have succeeded.
        await opened.streamBinary(file.binaryEntryPath, signal).pipeThrough(new TransformStream<Uint8Array, Uint8Array>({
          transform(chunk, controller) { signal.throwIfAborted(); if (!current()) throw new DOMException('Cancelled', 'AbortError'); controller.enqueue(chunk); },
          flush() { signal.throwIfAborted(); if (!current()) throw new DOMException('Cancelled', 'AbortError'); },
        })).pipeTo(writable, { signal });
        if (current()) setStatus('Ordinary file saved after its complete size and hash verification.');
      } catch (failure) { if (!writable.locked) await writable.abort(failure).catch(() => {}); throw failure; }
      return;
    }
    if (Number(file.sizeBytes) > PORTABLE_LIMITS.encryptedFileBytes) throw new PortableCryptoError('binary-stream-required', `This browser cannot save a large file as a stream. Use knowz archive extract <archive> --file-id ${file.id} --out <new-file> offline, or a browser with streaming file saves.`);
    const bytes = await opened.readBinary(file.binaryEntryPath, signal);
    try { if (current()) saveLocalDownload(new Blob([new Uint8Array(bytes)], { type: 'application/octet-stream' }), name); }
    finally { bytes.fill(0); }
  });
  return <section className="archive-workbench" aria-label="Original-key archive recovery">
    <header><h2>Complete vault archives</h2><p>Preserve plain content and encrypted content with its original key generations. Keys and passwords stay in this browser.</p></header>
    {transport && <details className="archive-panel"><summary>Export {vaultId ? 'this vault' : 'this tenant'}</summary><p>Browser downloads are limited to 128 MiB; use the CLI to stream larger exports. The archive includes plaintext metadata and any plain records. An optional archive password protects the whole download; it does not replace your original vault keys.</p>
      <label className="archive-check"><input type="checkbox" checked={protect} disabled={!!busy} onChange={e => { setProtect(e.target.checked); setExportPassword(''); setConfirmPassword(''); }} />Protect the entire archive with a password</label>
      {protect && <><label>Archive password<input type="password" autoComplete="new-password" value={exportPassword} onChange={e => setExportPassword(e.target.value)} /></label><label>Confirm archive password<input type="password" autoComplete="new-password" value={confirmPassword} onChange={e => setConfirmPassword(e.target.value)} /></label><p>Browser password archives are limited to 128 MiB. Use the CLI for larger archives.</p></>}
      <button type="button" disabled={!!busy || (protect && (!exportPassword || exportPassword !== confirmPassword))} onClick={() => void exportArchive()}>Export complete archive</button></details>}
    <div className="archive-panel"><h3>Inspect and recover</h3>
      <label>Archive file<input ref={fileInput} type="file" disabled={!!busy} onChange={e => { const selectedFile = e.target.files?.[0] ?? null; clear(); setFile(selectedFile); }} /></label>
      {file && <p>Selected archive: {file.name}</p>}
      <label>Archive password, if protected<input type="password" autoComplete="off" value={password} disabled={!!busy} onChange={e => setPassword(e.target.value)} /></label>
      <label>Trusted exporter fingerprint, from a separate source receipt<input type="text" spellCheck={false} autoComplete="off" value={pin} maxLength={64} disabled={!!busy || !!opened} onChange={e => setPin(e.target.value)} placeholder="Optional: full 64-character fingerprint" /></label>
      <p className="archive-muted">A signature included in an archive alone does not prove who exported it. Do not use its embedded fingerprint as your independent trust pin.</p>
      <div className="archive-actions"><button type="button" disabled={!file || !!busy} onClick={() => void inspect()}>Inspect archive</button><button type="button" onClick={clear}>Lock and clear</button>{busy && <button type="button" onClick={cancel}>Cancel</button>}</div>
    </div>
    {busy && <p role="status" aria-live="polite">{busy}</p>}{error && <p className="archive-error" role="alert">{error}</p>}{status && <p role="status">{status}</p>}
    {opened && <>
      <div className="archive-panel"><h3>{opened.sourceTrust === 'trusted' ? 'Source identity verified' : 'Source identity unverified'}</h3><p>{opened.manifest.preservationState === 'complete' ? 'Complete ciphertext preservation' : 'Partial preservation — some dependencies or content are omitted'}. Every listed member passed its size and hash checks.</p>
        <p className="archive-muted">A matching key proves local decryptability; it does not prove ownership or grant destination permissions. Imported release controls remain suspended until reviewed.</p>
        <dl><dt>Encrypted payloads</dt><dd>{opened.graph.payloads.length}</dd><dt>Original key generations</dt><dd>{opened.graph.catalog.length}</dd><dt>Omissions</dt><dd>{opened.manifest.inventory.omissions.length}</dd></dl>
        {opened.manifest.inventory.omissions.length > 0 && <details><summary>Review omissions</summary><pre>{JSON.stringify(opened.manifest.inventory.omissions, null, 2)}</pre></details>}
        {dependentCount > 0 && <div role="status"><p>{dependentCount} provider-dependent bodies and historical snapshots remain locked. They require the original source provider; local vault keys do not decrypt this envelope profile.</p><details><summary>Provider dependencies ({opened.graph.dependencyBoundItems.length})</summary><pre>{JSON.stringify(opened.graph.dependencyBoundItems, null, 2)}</pre></details></div>}
        {policies.length > 0 && <details><summary>Suspended release policies ({policies.length})</summary><p>These public policy records are inert. Review does not activate release, recipients, schedules, or key delivery.</p><pre>{JSON.stringify(policies, null, 2)}</pre></details>}
      </div>
      {!!opened.graph.catalog.length && <div className="archive-panel"><h3>Unlock original keys</h3><p>Load each required generation. Retired keys remain usable for historical decryption.</p>
        <label>Original context<select value={context} disabled={!!busy} onChange={e => setContext(e.target.value)}>{Array.from(new Set(opened.graph.catalog.map(k => k.contextId))).map(id => <option key={id} value={id}>{id}</option>)}</select></label>
        <ul className="archive-key-list">{opened.graph.catalog.map(key => <li key={`${key.contextId}:${key.generation}:${key.fingerprint}`}><strong>Generation {key.generation} · {key.lifecycle}</strong><code>{key.fingerprint}</code><span>{unlocked.includes(`${key.contextId}:${key.fingerprint}`) ? 'Unlocked in this session' : 'Key required'}</span></li>)}</ul>
        <label>Private key file<input ref={keyInput} type="file" disabled={!!busy} onChange={async e => { const keyFile = e.target.files?.[0]; e.target.value = ''; if (!keyFile) return; if (keyFile.size > PORTABLE_LIMITS.keyBytes) { setError('Key files are limited to 64 KiB.'); return; } const version = epoch.current; const text = await keyFile.text(); if (version === epoch.current) { setKeyText(text); setKeyLoaded(true); } }} /></label>
        {keyLoaded ? <p>Key file loaded (value hidden)</p> : <label>Or paste a private key or recovery phrase<textarea value={keyText} autoComplete="off" spellCheck={false} disabled={!!busy} onChange={e => setKeyText(e.target.value)} /></label>}
        <label>Key file passphrase<input type="password" autoComplete="off" value={keyPassphrase} disabled={!!busy} onChange={e => setKeyPassphrase(e.target.value)} /></label>
        <details><summary>Recovery phrase derivation passphrase</summary><label>Original optional derivation passphrase<input type="password" autoComplete="off" value={derivation} onChange={e => setDerivation(e.target.value)} /></label><p>This is separate from a protected key file password.</p></details>
        <button type="button" disabled={!!busy || !keyText} onClick={() => void run('Unlocking key locally…', async (signal, current) => { await ring.current!.importKey(keyText, { contextId: context, passphrase: keyPassphrase, derivationPassphrase: derivation, signal }); if (current()) setStatus('Matching original generation unlocked for this session.'); })}>Unlock matching generation</button>
      </div>}
      {opened.graph.payloads.some(payload => payload.reference.legacyUnattributed) && <div className="archive-panel"><h3>Legacy key attribution</h3><p>Select up to 16 original key generations for legacy recovery. Unlock those keys above first. Only the declared version 1 algorithm is tried. A successful key does not resolve a generation when several generations reused that key.</p>
        {opened.graph.catalog.map(key => { const id = `${key.contextId}:${key.generation}:${key.fingerprint}`; return <label className="archive-check" key={id}><input type="checkbox" data-legacy-candidate={id} checked={legacyCandidates.includes(id)} disabled={!!busy || (!legacyCandidates.includes(id) && legacyCandidates.length >= 16)} onChange={event => { setLegacyCandidates(current => event.target.checked ? [...current, id] : current.filter(value => value !== id)); setVerification(null); setLegacyAttribution(null); setPlaintext(null); }} />Generation {key.generation} · {key.contextId} · {key.fingerprint.slice(0, 12)}</label>; })}
      </div>}
      <div className="archive-panel"><h3>Verify recovery</h3><button type="button" disabled={!!busy} onClick={() => void verify()}>Verify every encrypted payload</button>
        {verification && <><p role="status"><strong>{fullRecovery ? 'Fully decrypted' : 'Recovery incomplete'}</strong>: {verification.verifiedPayloadCount}/{verification.requiredPayloadCount} encrypted payloads verified. {verification.missingKeys.length} missing keys; {verification.errors.length} failed payloads.</p><ul>{Object.entries(verification.roleCounts).map(([role, count]) => <li key={role}>{role}: {count.verified}/{count.required}</li>)}</ul></>}
        <p className="archive-muted">Verification checks every current body, field, comment, file and encrypted history payload. It never deletes the source.</p>
        {verification && (verification.missingKeys.length > 0 || verification.errors.length > 0) && <details className="archive-panel"><summary>Review recovery gaps</summary><p>Use these public payload IDs to locate the missing original key or failed payload. The archive is unchanged.</p><ul>{verification.missingKeys.map(id => <li key={`missing:${id}`}><code>{id}</code>: original key required</li>)}{verification.errors.map(({ payloadId, code }) => <li key={`failed:${payloadId}:${code}`}><code>{payloadId}</code>: {code}</li>)}</ul></details>}
        {verification && verification.legacyAttributions.length > 0 && <p>Legacy payloads authenticated: {verification.legacyAttributions.length}. Ambiguous generations: {verification.legacyAttributions.filter(value => value.generationAmbiguous).length}. Original archive attribution is unchanged.</p>}
        {verification && <button type="button" onClick={() => saveLocalDownload(new Blob([JSON.stringify({ archiveId: opened.manifest.inventory.archiveId, manifestSha256: opened.manifestSha256, sourceTrust: opened.sourceTrust, preservationState: opened.manifest.preservationState, requiredPayloadCount: verification.requiredPayloadCount + dependentCount, verifiedPayloadCount: verification.verifiedPayloadCount, complete: !!fullRecovery, providerDependentPayloadCount: dependentCount, suspendedReleasePolicyCount: policies.length, roleCounts: verification.roleCounts, missingKeys: verification.missingKeys, errors: verification.errors, legacyAttributions: verification.legacyAttributions, verifiedAt: new Date().toISOString() }, null, 2)], { type: 'application/json' }), 'knowz-client-verification.json')}>Download verification receipt</button>}
      </div>
      {opened.graph.payloads.length > 0 && <div className="archive-panel"><h3>Read encrypted content locally</h3><label>Payload<select value={selected} onChange={e => { setSelected(Number(e.target.value)); setPlaintext(null); setLegacyAttribution(null); }}>{opened.graph.payloads.map((item, index) => <option key={index} value={index}>{item.reference.ownerKind} · {item.reference.ownerId} · {item.reference.role}{item.reference.fieldName ? ` · ${item.reference.fieldName}` : ''}{item.reference.versionId ? ` · version ${item.reference.versionId}` : ''}</option>)}</select></label>
        <button type="button" disabled={!!busy || !payload} onClick={() => payload && void view(payload)}>{payload?.reference.role === 'binary' || payload?.reference.role === 'variant' ? 'Decrypt and download file' : 'Decrypt selected payload'}</button>
        {legacyAttribution && <div><p>Legacy ciphertext authenticated. {legacyAttribution.generationAmbiguous ? 'The key matches several generations; generation attribution remains ambiguous.' : `Original generation ${legacyAttribution.generation} verified.`}</p><button type="button" onClick={() => saveLocalDownload(new Blob([JSON.stringify(legacyAttribution, null, 2)], { type: 'application/json' }), 'knowz-legacy-attribution.json')}>Download public attribution metadata</button></div>}
        {plaintext !== null && <pre className="archive-plaintext" tabIndex={0}>{plaintext === '' ? '(Empty content)' : plaintext}</pre>}
      </div>}
      {ordinaryFiles.length > 0 && <div className="archive-panel"><h3>Ordinary file bytes</h3><p>These file bytes are preserved as ordinary binary data. Protected names and other metadata use the original-key payload reader above.</p><ul>{ordinaryFiles.map(file => <li key={String(file.id)}><button type="button" disabled={!!busy} onClick={() => void downloadOrdinaryFile(file)}>Download ordinary file {String(file.id)}</button></li>)}</ul></div>}
      {Object.entries(opened.graph.plainData).filter(([, rows]) => rows.length).map(([name, rows]) => <details className="archive-panel" key={name}><summary>Plain {name} ({rows.length})</summary><pre>{JSON.stringify(rows, null, 2)}</pre></details>)}
      {transport && <div className="archive-panel"><h3>Import into this account</h3><p>Only archive ciphertext and public metadata are uploaded. Local keys, passwords and decrypted content are never sent. Locked preservation is possible without keys.</p>
        <label>Destination vault ID (blank preserves archive vault mappings)<input value={target} disabled={!!busy} onChange={e => { invalidatePlan(); setTarget(e.target.value); }} /></label>
        <label>Conflict strategy<select value={strategy} disabled={!!busy} onChange={e => { invalidatePlan(); setStrategy(e.target.value); }}><option value="skip">Keep existing records</option><option value="merge">Merge missing data</option><option value="overwrite">Overwrite matching records</option></select></label>
        <label className="archive-check"><input type="checkbox" checked={allowPartial} disabled={!!busy} onChange={e => { invalidatePlan(); setAllowPartial(e.target.checked); }} />I accept an explicitly partial archive, including its listed omissions</label>
        <button type="button" disabled={!!busy || (opened.manifest.preservationState === 'partial' && !allowPartial)} onClick={() => void makePlan()}>Prepare import plan</button>
        {plan && <><h4>Review destination plan</h4><dl><dt>Destination tenant</dt><dd>{plan.destinationTenantId}</dd><dt>Preservation</dt><dd>{plan.preservationState}</dd><dt>Conflicts</dt><dd>{plan.conflicts.length}</dd><dt>Expires</dt><dd>{plan.expiresAt}</dd></dl><details><summary>Vault mapping and conflicts</summary><pre>{JSON.stringify({ mapping: plan.mapping, conflicts: plan.conflicts, omissions: plan.omissions }, null, 2)}</pre></details><label className="archive-check"><input type="checkbox" checked={reviewed} onChange={e => setReviewed(e.target.checked)} />I reviewed this destination, mapping, conflicts and omissions</label><button type="button" disabled={!!busy || !reviewed || plan.preservationState === 'blocked' || plan.preservationState === 'partial' && !allowPartial || Date.parse(plan.expiresAt) <= Date.now()} onClick={() => void commit()}>Commit reviewed import</button></>}
        {receipt && <><p role="status">{receipt.preservationState === 'complete' ? 'Archive preserved' : 'Partial archive preserved'}: {receipt.preservedPayloadCount}/{receipt.requiredPayloadCount} payloads. Destination decryption has not yet been verified.</p><button type="button" onClick={() => saveLocalDownload(new Blob([JSON.stringify(receipt, null, 2)], { type: 'application/json' }), 'knowz-destination-receipt.json')}>Download destination receipt</button></>}
      </div>}
    </>}
  </section>;
}
