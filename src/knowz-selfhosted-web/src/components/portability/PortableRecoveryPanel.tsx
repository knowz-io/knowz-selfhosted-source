import { useEffect, useState } from 'react';
import { PortableCryptoError, validateDependencyBoundItem, validateSuspendedReleasePolicy } from '../../lib/vendored/portable/portable';
import type { EncryptedRecordTransport, PortableRecoveryState } from '../../lib/encrypted-record-api';
import './archive-workbench.css';
export function PortableRecoveryPanel({ vaultId, knowledgeId, scopeKey, transport }: { vaultId: string; knowledgeId: string; scopeKey: string; transport: EncryptedRecordTransport }) {
  const [result, setResult] = useState<{ scope: string; value: PortableRecoveryState } | null>(null);
  const [error, setError] = useState<{ scope: string; message: string } | null>(null);
  const scope = `${scopeKey}:${vaultId}:${knowledgeId}`;
  useEffect(() => {
    const abort = new AbortController(); setResult(null); setError(null);
    void transport.recoveryState(knowledgeId, vaultId, abort.signal).then(value => {
      if (abort.signal.aborted) return;
      if (value.knowledgeId !== knowledgeId || !['source-provider-required', 'suspended-release'].includes(value.recoveryState) || value.canEdit !== false || value.canProcessWithAi !== false || value.activationState !== 'suspended' || !Array.isArray(value.suspendedReleasePolicies)) throw new PortableCryptoError('recovery-state', 'The inactive recovery metadata does not match this item. Reload before continuing.');
      for (const policy of value.suspendedReleasePolicies) { validateSuspendedReleasePolicy(policy); if (policy.knowledgeId !== knowledgeId) throw new PortableCryptoError('recovery-state', 'The suspended policy belongs to another item.'); }
      if (value.dependencyBoundItem) { validateDependencyBoundItem(value.dependencyBoundItem); if (value.dependencyBoundItem.id !== knowledgeId) throw new PortableCryptoError('recovery-state', 'The provider dependency belongs to another item.'); }
      setResult({ scope, value });
    }).catch(failure => { if (!abort.signal.aborted) setError({ scope, message: failure instanceof PortableCryptoError ? failure.message : 'Could not load inactive recovery metadata. Check your account access and connection.' }); });
    return () => abort.abort();
  }, [scope, vaultId, knowledgeId, transport]);
  const value = result?.scope === scope ? result.value : null;
  return <section className="archive-workbench" aria-label="Inactive archive recovery"><div className="archive-panel">
    <h3>{value?.recoveryState === 'source-provider-required' ? 'Source provider recovery required' : value ? 'Release controls suspended' : 'Locked imported content'}</h3>
    <p>This imported item is read-only. Editing and AI processing are disabled. Its original release controls remain suspended.</p>
    {value?.dependencyBoundItem && <p>The current body and {value.dependencyBoundItem.versions.length} historical snapshots require the original source provider. A locally held vault key cannot decrypt this provider envelope.</p>}
    {!value && error?.scope !== scope && <p role="status">Loading recovery metadata…</p>}
    {error?.scope === scope && <p role="alert">{error.message}</p>}
    {value && <><p>{value.suspendedReleasePolicies.length} suspended release policies preserved. Reviewing metadata does not activate release, recipients, schedules, or key delivery.</p><details><summary>Review public recovery metadata</summary><pre>{JSON.stringify({ recoveryState: value.recoveryState, suspendedReleasePolicies: value.suspendedReleasePolicies, dependencyBoundItem: value.dependencyBoundItem }, null, 2)}</pre></details></>}
  </div></section>;
}
