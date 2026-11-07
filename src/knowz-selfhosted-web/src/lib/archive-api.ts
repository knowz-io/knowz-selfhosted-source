import { PortableCryptoError, PORTABLE_LIMITS, readBoundedStream, parsePortableJson, fingerprintPublicKey, decodeBase64 } from './vendored/portable/portable';
import type { ArchiveTransport, ArchivePlan, ArchiveReceipt } from '../components/portability/archive-types';
interface Options { baseUrl: () => string; headers: () => Record<string, string>; fetcher?: typeof fetch }
const BASE = '/api/v1/portability';
/** Authenticated ciphertext transport. It accepts no key, password or decrypted content. */
export function createArchiveTransport({ baseUrl, headers, fetcher = fetch }: Options): ArchiveTransport {
  const request = async (path: string, init: RequestInit = {}) => {
    const auth = new Headers(headers()); new Headers(init.headers).forEach((value, key) => auth.set(key, value));
    const response = await fetcher(`${baseUrl().replace(/\/$/, '').replace(/\/api\/v1$/, '')}${path}`, { ...init, headers: auth, redirect: 'error', cache: 'no-store' });
    if (!response.ok) {
      await response.body?.cancel().catch(() => {});
      throw new PortableCryptoError('archive-api', response.status === 409 ? 'The destination changed. Prepare and review a fresh import plan.' : response.status === 401 || response.status === 403 ? 'Your account is not authorized for this archive operation.' : `Archive request failed (${response.status}).`);
    }
    return response;
  };
  const json = async <T>(path: string, init?: RequestInit): Promise<T> => {
    const response = await request(path, init); if (!response.body) throw new PortableCryptoError('archive-api', 'The destination returned an empty response.');
    const bytes = await readBoundedStream(response.body, PORTABLE_LIMITS.manifestBytes, init?.signal ?? undefined);
    const value = parsePortableJson(new TextDecoder().decode(bytes), PORTABLE_LIMITS.manifestBytes) as Record<string, unknown>;
    return (value && typeof value === 'object' && 'data' in value ? value.data : value) as T;
  };
  const requireCapability = async (signal: AbortSignal, ordinaryBytes = 0) => {
    if (!Number.isSafeInteger(ordinaryBytes) || ordinaryBytes < 0 || ordinaryBytes > PORTABLE_LIMITS.ordinaryFileBytes) throw new PortableCryptoError('archive-capability', 'The inspected ordinary-file requirement is invalid.');
    const value = await json<{ archiveSchemaVersion: number; graphSchemaVersion: number; capabilities: string[]; limits?: Record<string, number> }>(`${BASE}/capabilities`, { signal });
    if (value.archiveSchemaVersion !== 4 || value.graphSchemaVersion !== 5 || !value.capabilities?.includes('knowz.archive/v1') || !value.capabilities?.includes('knowz.encrypted-graph/v1')) throw new PortableCryptoError('archive-capability', 'This destination does not support complete original-key archives. Upgrade it before continuing.');
    const maximum = value.limits?.ordinaryFileBytes; const archive = value.limits?.archiveBytes;
    if (ordinaryBytes > PORTABLE_LIMITS.encryptedFileBytes && (!Number.isSafeInteger(maximum) || !Number.isSafeInteger(archive)
      || maximum! <= 0 || maximum! > archive! || archive! > PORTABLE_LIMITS.archiveBytes || maximum! < ordinaryBytes))
      throw new PortableCryptoError('archive-capability', 'This destination does not declare sufficient capacity for the inspected large ordinary file. Upgrade it before importing.');
  };
  return {
    async exportArchive(options, signal) {
      await requireCapability(signal);
      const identity = await json<{ signerFingerprint: string; signerPublicKey: string }>(`${BASE}/archives/identity`, { signal });
      if (fingerprintPublicKey(decodeBase64(identity.signerPublicKey, 80, 256)) !== identity.signerFingerprint) throw new PortableCryptoError('archive-identity', 'The source returned an invalid public signing identity.');
      const response = await request(`${BASE}/archives/export`, { method: 'POST', signal, headers: { 'Content-Type': 'application/json', Accept: 'application/vnd.knowz.archive+zip' }, body: JSON.stringify({ scopeType: options.vaultId ? 'Vault' : 'Tenant', vaultIds: options.vaultId ? [options.vaultId] : [], allowPartial: options.allowPartial }) });
      if (!response.body) throw new PortableCryptoError('archive-api', 'The source returned an empty archive.');
      const declared = Number(response.headers.get('Content-Length') ?? 0);
      if (declared > PORTABLE_LIMITS.protectedBrowserBytes) { await response.body.cancel(); throw new PortableCryptoError('archive-size', 'Browser downloads are limited to 128 MiB. Use the CLI to stream larger archives.'); }
      const bytes = await readBoundedStream(response.body, PORTABLE_LIMITS.protectedBrowserBytes, signal);
      const blob = new Blob([new Uint8Array(bytes)], { type: 'application/vnd.knowz.archive+zip' }); bytes.fill(0);
      return { blob, signerFingerprint: identity.signerFingerprint };
    },
    async plan(blob, options, signal) {
      await requireCapability(signal, options.requiredOrdinaryFileBytes);
      const query = new URLSearchParams({ strategy: options.strategy, allowPartial: String(options.allowPartial) });
      if (options.targetVaultId) query.set('targetVaultId', options.targetVaultId);
      if (options.expectedSignerFingerprint) query.set('expectedSignerFingerprint', options.expectedSignerFingerprint);
      const plan = await json<ArchivePlan>(`${BASE}/imports/plan?${query}`, { method: 'POST', body: blob, headers: { 'Content-Type': 'application/vnd.knowz.archive+zip', 'Idempotency-Key': options.idempotencyKey }, signal });
      validateArchiveResult(plan, 'plan');
      if (plan.idempotencyKey !== options.idempotencyKey) throw new PortableCryptoError('archive-plan', 'The destination returned an invalid import plan.');
      return plan;
    },
    commit: async (planId, signal) => {
      const receipt = await json<ArchiveReceipt>(`${BASE}/imports/${encodeURIComponent(planId)}/commit`, { method: 'POST', signal });
      validateArchiveResult(receipt, 'receipt');
      if (receipt.planId !== planId) throw new PortableCryptoError('archive-receipt', 'The destination returned an invalid commit receipt.');
      return receipt;
    },
    cancel: async planId => { await request(`${BASE}/imports/${encodeURIComponent(planId)}`, { method: 'DELETE' }); },
  };
}

function validateArchiveResult(value: ArchivePlan | ArchiveReceipt, kind: 'plan' | 'receipt'): void {
  const fail = () => { throw new PortableCryptoError(`archive-${kind}`, `The destination returned an invalid ${kind === 'plan' ? 'import plan' : 'commit receipt'}.`); };
  if (!value || typeof value !== 'object') fail();
  for (const key of ['planId', 'archiveId', 'sourceSnapshot', 'destinationTenantId'] as const) if (typeof value[key] !== 'string' || !value[key] || value[key].length > 4096) fail();
  if (![value.manifestSha256, value.mappingSha256].every(hash => typeof hash === 'string' && /^[a-f0-9]{64}$/.test(hash))) fail();
  if (!['trusted', 'unverified'].includes(value.sourceTrust) || !['complete', 'partial', 'blocked'].includes(value.preservationState)) fail();
  if (!Number.isSafeInteger(value.requiredPayloadCount) || value.requiredPayloadCount < 0 || !Array.isArray(value.omissions)) fail();
  if (value.preservationState === 'complete' && value.omissions.length !== 0) fail();
  if (kind === 'plan') {
    const plan = value as ArchivePlan;
    if (typeof plan.expiresAt !== 'string' || !Number.isFinite(Date.parse(plan.expiresAt)) || Date.parse(plan.expiresAt) <= Date.now()) fail();
    if (!plan.mapping || typeof plan.mapping !== 'object' || Array.isArray(plan.mapping) || Object.values(plan.mapping).some(id => typeof id !== 'string' || !id)) fail();
    if (!Array.isArray(plan.conflicts) || typeof plan.idempotencyKey !== 'string' || !plan.idempotencyKey) fail();
  } else {
    const receipt = value as ArchiveReceipt;
    if (!receipt.commitId || typeof receipt.commitId !== 'string' || typeof receipt.committedAt !== 'string' || !Number.isFinite(Date.parse(receipt.committedAt))) fail();
    if (!Number.isSafeInteger(receipt.preservedPayloadCount) || receipt.preservedPayloadCount < 0 || receipt.preservedPayloadCount > receipt.requiredPayloadCount || receipt.clientVerificationState !== 'not-performed') fail();
    if (receipt.preservationState === 'complete' && receipt.preservedPayloadCount !== receipt.requiredPayloadCount) fail();
  }
}
