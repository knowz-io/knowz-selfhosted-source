import { PortableCryptoError, PORTABLE_LIMITS, readBoundedStream, parsePortableJson, decodeBase64, fingerprintPublicKey } from './vendored/portable/portable';
import type { ArchiveReceipt } from '../components/portability/archive-types';
export interface ArchivePeerBinding {
  id: string; tenantId: string; destinationVaultId: string; sourceTenantId: string; sourceVaultId: string;
  sourceBaseUrl: string; signerFingerprint: string; signerPublicKey: string; credentialIdentitySha256: string;
  bindingRevision: string; remoteBindingId: string | null; remoteBindingRevision: string | null;
  localSourceBaseUrl: string | null; isEnabled: boolean; trustProvenance: 'transport-authenticated-tofu';
}
export interface ArchivePeerReview { status: 'not-registered' | 'pending' | 'registered' | 'review-required'; binding: ArchivePeerBinding | null }
export interface ArchivePeerReceipt extends ArchiveReceipt { peerTrustProvenance: 'transport-authenticated-tofu'; peerBindingId: string; peerBindingRevision: string }
export interface ArchivePeerTransport {
  review(linkId: string, signal: AbortSignal): Promise<ArchivePeerReview>;
  register(linkId: string, input: { localSourceBaseUrl: string; expectedBindingRevision?: string }, signal: AbortSignal): Promise<ArchivePeerBinding>;
  transfer(linkId: string, direction: 'pull' | 'push', binding: ArchivePeerBinding, signal: AbortSignal): Promise<ArchivePeerReceipt>;
}
interface Options { baseUrl: () => string; headers: () => Record<string, string>; fetcher?: typeof fetch }
const guid = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}$/i;
const digest = /^[a-f0-9]{64}$/;
const revision = (value: unknown) => typeof value === 'string' && !!value && value.length <= 64;
function validUrl(value: unknown): boolean {
  if (typeof value !== 'string' || !value || value.length > 2048 || value !== value.trim()) return false;
  try { const url = new URL(value); return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password && !url.search && !url.hash; } catch { return false; }
}
function requireBinding(value: ArchivePeerBinding, complete: boolean): void {
  const fail = () => { throw new PortableCryptoError('peer-binding', 'The destination returned an invalid saved peer binding.'); };
  if (!value || typeof value !== 'object' || !['id', 'tenantId', 'destinationVaultId', 'sourceTenantId', 'sourceVaultId'].every(key => guid.test(String(value[key as keyof ArchivePeerBinding])))
    || !validUrl(value.sourceBaseUrl) || !digest.test(value.signerFingerprint) || !digest.test(value.credentialIdentitySha256)
    || !revision(value.bindingRevision) || typeof value.isEnabled !== 'boolean' || value.trustProvenance !== 'transport-authenticated-tofu') fail();
  if (fingerprintPublicKey(decodeBase64(value.signerPublicKey, 80, 256)) !== value.signerFingerprint) fail();
  if (value.remoteBindingId != null && !guid.test(value.remoteBindingId) || value.remoteBindingRevision != null && !revision(value.remoteBindingRevision)
    || value.localSourceBaseUrl != null && !validUrl(value.localSourceBaseUrl)) fail();
  if (complete && (!value.isEnabled || !value.remoteBindingId || !value.remoteBindingRevision || !value.localSourceBaseUrl)) fail();
}
function requireReceipt(value: ArchivePeerReceipt, binding: ArchivePeerBinding, direction: 'pull' | 'push'): void {
  const fail = () => { throw new PortableCryptoError('peer-receipt', 'The peer receipt does not prove complete server preservation for this reviewed binding.'); };
  if (!value || !['planId', 'archiveId', 'commitId'].every(key => guid.test(String(value[key as keyof ArchivePeerReceipt])))
    || !digest.test(value.manifestSha256) || !digest.test(value.mappingSha256) || typeof value.sourceSnapshot !== 'string' || !value.sourceSnapshot
    || value.destinationTenantId !== (direction === 'pull' ? binding.tenantId : binding.sourceTenantId)
    || value.peerBindingId !== binding.id || value.peerBindingRevision !== binding.bindingRevision || value.peerTrustProvenance !== 'transport-authenticated-tofu'
    || value.preservationState !== 'complete' || value.sourceTrust !== 'trusted' || !Array.isArray(value.omissions) || value.omissions.length
    || !Number.isSafeInteger(value.requiredPayloadCount) || value.requiredPayloadCount < 0 || value.preservedPayloadCount !== value.requiredPayloadCount
    || value.clientVerificationState !== 'not-performed' || !Number.isFinite(Date.parse(value.committedAt))) fail();
}
/** Saved credentials stay on the server. This adapter accepts no original key or plaintext. */
export function createArchivePeerTransport({ baseUrl, headers, fetcher = fetch }: Options): ArchivePeerTransport {
  const json = async <T>(linkId: string, suffix: string, signal: AbortSignal, body?: unknown): Promise<T> => {
    if (!guid.test(linkId)) throw new PortableCryptoError('peer-link', 'Select a valid saved vault link.');
    const auth = new Headers(headers()); if (body !== undefined) auth.set('Content-Type', 'application/json');
    const response = await fetcher(`${baseUrl().replace(/\/$/, '').replace(/\/api\/v1$/, '')}/api/v1/sync/links/${encodeURIComponent(linkId)}${suffix}`,
      { method: body === undefined ? 'GET' : 'POST', headers: auth, body: body === undefined ? undefined : JSON.stringify(body), signal, cache: 'no-store', redirect: 'error' });
    if (!response.ok) { await response.body?.cancel().catch(() => {}); throw new PortableCryptoError('peer-api', response.status === 409
      ? 'The saved peer changed or needs registration review. Refresh its binding before continuing.' : response.status === 401 || response.status === 403
        ? 'Your account cannot manage this saved peer and vault.' : response.status === 404 || response.status === 422
          ? 'Complete archive transfer is unavailable for this saved link. Check its configuration and destination support.' : `Saved peer request failed (${response.status}).`); }
    if (!response.body) throw new PortableCryptoError('peer-api', 'The saved peer returned an empty response.');
    const bytes = await readBoundedStream(response.body, PORTABLE_LIMITS.manifestBytes, signal); try {
      const parsed = parsePortableJson(new TextDecoder().decode(bytes), PORTABLE_LIMITS.manifestBytes) as Record<string, unknown>;
      return (parsed && typeof parsed === 'object' && 'data' in parsed ? parsed.data : parsed) as T;
    } finally { bytes.fill(0); }
  };
  return {
    async review(linkId, signal) {
      const value = await json<ArchivePeerReview>(linkId, '/archive-peer', signal);
      if (!value || !['not-registered', 'pending', 'registered', 'review-required'].includes(value.status)
        || value.status === 'not-registered' && value.binding != null || value.status !== 'not-registered' && !value.binding)
        throw new PortableCryptoError('peer-review', 'The saved peer returned an invalid registration status.');
      if (value.status === 'not-registered') return { status: 'not-registered', binding: null };
      if (value.binding) requireBinding(value.binding, value.status === 'registered'); return value;
    },
    async register(linkId, input, signal) {
      if (Object.keys(input).some(key => !['localSourceBaseUrl', 'expectedBindingRevision'].includes(key)) || !validUrl(input.localSourceBaseUrl)
        || input.expectedBindingRevision != null && !revision(input.expectedBindingRevision))
        throw new PortableCryptoError('peer-metadata', 'Only public peer registration metadata is accepted; keys remain local.');
      const value = await json<ArchivePeerBinding>(linkId, '/archive-peer/register', signal, input); requireBinding(value, true); return value;
    },
    async transfer(linkId, direction, binding, signal) {
      if (!['pull', 'push'].includes(direction)) throw new PortableCryptoError('peer-direction', 'Choose an explicit pull or push direction.');
      requireBinding(binding, true); const value = await json<ArchivePeerReceipt>(linkId, '/archives/transfer', signal, { direction });
      requireReceipt(value, binding, direction); return value;
    },
  };
}
