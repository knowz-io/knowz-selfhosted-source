import { PortableCryptoError, PORTABLE_LIMITS, readBoundedStream, parsePortableJson, fingerprintPublicKey, type PortablePayloadReference, type CustodyChallenge, type RecoveredCustodyAcceptance, type PortableDependencyBoundKnowledge, type PortableSuspendedReleasePolicy } from './vendored/portable/portable';
export interface EncryptedCatalogReply { vaultId: string; revision: string; cryptoContexts: Record<string, unknown>[]; cryptoKeyCatalog: Record<string, unknown>[]; vaultCryptoContextLinks: Record<string, unknown>[]; acceptedCustodyKeys?: Array<{ contextId: string; generation: number; fingerprint: string }>; importAnchors?: Array<{ contextId: string; planId: string; archiveId: string; sourceTrust: string; manifestSha256: string; mappingSha256: string }> }
export interface PortableRecoveryState { knowledgeId: string; recoveryState: 'source-provider-required' | 'suspended-release'; revision: string; suspendedReleasePolicies: PortableSuspendedReleasePolicy[]; dependencyBoundItem?: PortableDependencyBoundKnowledge | null; canEdit: false; canProcessWithAi: false; activationState: 'suspended' }
export interface EncryptedRecordReply extends EncryptedCatalogReply { ownerKind: string; ownerId: string; record: Record<string, unknown>; payloadKeyReferences: PortablePayloadReference[]; related?: { comments: string[]; files: string[]; attachments: string[]; versions: string[] } }
export interface EncryptedRecordWrite { vaultId?: string; record: Record<string, unknown>; payloadKeyReferences: PortablePayloadReference[] }
export interface LocalAiPolicyReply { supported: boolean; reason?: string; provider?: { displayOrigin: string; model: string; policyRevision: string }; limits?: { inputBytes: number; instructionBytes: number; outputBytes: number; timeoutSeconds: number }; itemRevision?: string; bodyMode?: 'ordinary' | 'encrypted' }
export interface LocalAiChallengeInput { vaultId: string; operation: 'amend-body'; contextId: string; fingerprint: string; generation: number; providerPolicyRevision: string; inputSha256: string; instructionSha256: string }
export interface LocalAiChallengeReply { challengeId: string; expiresAt: string; consentBinding: { operation: 'amend-body'; providerPolicyRevision: string; inputSha256: string; instructionSha256: string; owner: { actorId: string; tenantId: string; itemId: string; vaultId: string; itemRevision: string; publicStateSha256: string; bodyMode: 'ordinary' | 'encrypted'; contextId: string; fingerprint: string; generation: number } } }
export interface LocalAiDraftReply { operationId: string; itemRevision: string; providerPolicyRevision: string; content: string; saved: false }
export interface EncryptedRecordTransport {
  localAiPolicy?(id: string, vaultId: string, signal: AbortSignal): Promise<LocalAiPolicyReply>;
  createLocalAiChallenge?(id: string, value: LocalAiChallengeInput, revision: string, signal: AbortSignal): Promise<LocalAiChallengeReply>;
  runLocalAi?(id: string, value: { vaultId: string; challengeId: string; consent: true; content: string; instruction: string }, revision: string, signal: AbortSignal): Promise<LocalAiDraftReply>;
  contexts(vaultId: string, signal: AbortSignal): Promise<EncryptedCatalogReply>;
  payload(vaultId: string, kind: string, id: string, signal: AbortSignal): Promise<EncryptedRecordReply>;
  file(vaultId: string, id: string, signal: AbortSignal): Promise<Uint8Array>;
  write(kind: string, id: string, value: EncryptedRecordWrite, revision: string, signal: AbortSignal): Promise<unknown>;
  remove(kind: string, id: string, revision: string, signal: AbortSignal, vaultId: string): Promise<unknown>;
  createItem(value: EncryptedRecordWrite, signal: AbortSignal, revision: string): Promise<unknown>;
  createComment(id: string, value: EncryptedRecordWrite, signal: AbortSignal, revision: string): Promise<unknown>;
  restore(id: string, versionId: string, revision: string, signal: AbortSignal, vaultId: string): Promise<unknown>;
  selectPrimary(vaultId: string, contextId: string, fingerprint: string, revision: string, signal: AbortSignal, generation?: number): Promise<unknown>;
  replaceFile(id: string, value: EncryptedRecordWrite, binary: Uint8Array, revision: string, signal: AbortSignal): Promise<unknown>;
  stageFile(vaultId: string, value: EncryptedRecordWrite & { attachments?: Record<string, unknown>[] }, binary: Uint8Array, revision: string, signal: AbortSignal): Promise<unknown>;
  createCustodyChallenge(vaultId: string, contextId: string, value: { planId: string; generation: number; fingerprint: string }, signal: AbortSignal): Promise<CustodyChallenge>;
  acceptRecoveredCustody(vaultId: string, contextId: string, value: { challengeId: string; proof: string }, signal: AbortSignal): Promise<RecoveredCustodyAcceptance>;
  recoveryState(id: string, vaultId: string, signal: AbortSignal): Promise<PortableRecoveryState>;
}
interface Options { baseUrl: () => string; headers: () => Record<string, string>; fetcher?: typeof fetch }
const part = (value: string) => encodeURIComponent(value);
const route = (kind: string) => { const value = ({ knowledge: 'items', comment: 'comments', file: 'files', attachment: 'attachments' } as Record<string, string>)[kind]; if (!value) throw new PortableCryptoError('record-kind', 'This record is read-only.'); return value; };
function localOnly(value: unknown, depth = 0): void {
  if (depth > 64) throw new PortableCryptoError('record-depth', 'The encrypted record is too deeply nested.');
  if (value && typeof value === 'object') for (const [key, child] of Object.entries(value)) { if (/^(privatekey|privatekeybase64|mnemonic|seedphrase|passphrase|password)$/i.test(key)) throw new PortableCryptoError('local-secret', 'Private keys and passwords must remain local.'); localOnly(child, depth + 1); }
}
export function createEncryptedRecordTransport({ baseUrl, headers, fetcher = fetch }: Options): EncryptedRecordTransport {
  const request = async (path: string, init: RequestInit, allowMissing = false) => {
    const auth = new Headers(headers()); new Headers(init.headers).forEach((value, key) => auth.set(key, value));
    if (init.body instanceof FormData) auth.delete('Content-Type');
    const response = await fetcher(`${baseUrl().replace(/\/$/, '').replace(/\/api\/v1$/, '')}/api/v1${path}`, { ...init, headers: auth, redirect: 'error', cache: 'no-store' });
    if (!response.ok && !(allowMissing && response.status === 404)) { await response.body?.cancel().catch(() => {}); throw new PortableCryptoError('record-api', response.status === 409 ? 'This record changed. Reload it before editing again.' : response.status === 401 || response.status === 403 ? 'You no longer have permission to use this encrypted record.' : `Encrypted record request failed (${response.status}).`); }
    return response;
  };
  const json = async <T>(path: string, init: RequestInit): Promise<T> => {
    const response = await request(path, init); if (response.status === 204) return undefined as T;
    if (!response.body) throw new PortableCryptoError('record-api', 'The encrypted record response was empty.');
    const bytes = await readBoundedStream(response.body, PORTABLE_LIMITS.packageBytes, init.signal ?? undefined);
    const parsed = parsePortableJson(new TextDecoder().decode(bytes)) as Record<string, unknown>;
    return (parsed && typeof parsed === 'object' && 'data' in parsed ? parsed.data : parsed) as T;
  };
  const send = async (path: string, method: string, value: unknown, revision: string | undefined, signal: AbortSignal) => {
    localOnly(value); return json(path, { method, signal, body: value === undefined ? undefined : JSON.stringify(value), headers: { 'Content-Type': 'application/json', ...(revision ? { 'If-Match': revision } : {}) } });
  };
  const localAiJson = async <T>(path: string, init: RequestInit, allowMissing = false): Promise<T> => {
    const response = await request(path, init, allowMissing);
    if (allowMissing && response.status === 404) { await response.body?.cancel(); return { supported: false, reason: 'edition-unsupported' } as T; }
    if (!response.body) throw new PortableCryptoError('local-ai-response', 'The local AI response was empty.');
    const bytes = await readBoundedStream(response.body, 512 * 1024, init.signal ?? undefined);
    try { return parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes)) as T; }
    finally { bytes.fill(0); }
  };
  const localAiSend = async <T>(path: string, value: unknown, fields: readonly string[], revision: string, signal: AbortSignal): Promise<T> => {
    localOnly(value);
    if (!value || typeof value !== 'object' || Array.isArray(value) || Object.keys(value).some(key => !fields.includes(key)) || fields.some(key => !(key in value)) || !revision) throw new PortableCryptoError('local-ai-input', 'The local AI request is invalid.');
    const input = value as Record<string, unknown>;
    if ('content' in input) {
      if (input.consent !== true || typeof input.content !== 'string' || typeof input.instruction !== 'string' || !input.instruction.trim()) throw new PortableCryptoError('local-ai-consent', 'Explicit consent and an instruction are required.');
      const content = new TextEncoder().encode(input.content), instruction = new TextEncoder().encode(input.instruction);
      try { if (content.length > 65536 || instruction.length > 4096) throw new PortableCryptoError('local-ai-size', 'The local AI request exceeds its text limit.'); }
      finally { content.fill(0); instruction.fill(0); }
    }
    return localAiJson(path, { method: 'POST', signal, headers: { 'Content-Type': 'application/json', 'If-Match': revision }, body: JSON.stringify(value) });
  };
  return {
    localAiPolicy: (id, vaultId, signal) => localAiJson(`/encrypted/items/${part(id)}/ai-session-policy?vaultId=${part(vaultId)}`, { signal }, true),
    createLocalAiChallenge: (id, value, revision, signal) => localAiSend(`/encrypted/items/${part(id)}/ai-session-challenges`, value, ['vaultId', 'operation', 'contextId', 'fingerprint', 'generation', 'providerPolicyRevision', 'inputSha256', 'instructionSha256'], revision, signal),
    runLocalAi: (id, value, revision, signal) => localAiSend(`/encrypted/items/${part(id)}/ai-sessions`, value, ['vaultId', 'challengeId', 'consent', 'content', 'instruction'], revision, signal),
    contexts: (vaultId, signal) => json(`/vaults/${part(vaultId)}/encryption/contexts`, { signal }),
    payload: (vaultId, kind, id, signal) => json(`/vaults/${part(vaultId)}/encryption/payloads/${part(kind)}/${part(id)}`, { signal }),
    file: async (vaultId, id, signal) => { const response = await request(`/vaults/${part(vaultId)}/encryption/files/${part(id)}`, { signal }); if (!response.body) throw new PortableCryptoError('file-missing', 'Encrypted file bytes are missing.'); return readBoundedStream(response.body, PORTABLE_LIMITS.encryptedFileBytes, signal); },
    write: (kind, id, value, revision, signal) => send(`/encrypted/${route(kind)}/${part(id)}`, 'PUT', value, revision, signal),
    remove: async (kind, id, revision, signal, vaultId) => {
      if (!vaultId) throw new PortableCryptoError('record-vault', 'The destination vault is required to delete this record.');
      return send(`/encrypted/${route(kind)}/${part(id)}?vaultId=${part(vaultId)}`, 'DELETE', undefined, revision, signal);
    },
    createItem: (value, signal, revision) => send('/encrypted/items', 'POST', value, revision, signal),
    createComment: (id, value, signal, revision) => send(`/encrypted/items/${part(id)}/comments`, 'POST', value, revision, signal),
    restore: (id, versionId, revision, signal, vaultId) => send(`/encrypted/items/${part(id)}/versions/${part(versionId)}/restore`, 'POST', { vaultId }, revision, signal),
    selectPrimary: (vaultId, contextId, fingerprint, revision, signal, generation) => send(`/vaults/${part(vaultId)}/encryption/primary-context`, 'PUT', { contextId, fingerprint, generation, expectedRevision: revision }, undefined, signal),
    createCustodyChallenge: (vaultId, contextId, value, signal) => send(`/vaults/${part(vaultId)}/encryption/contexts/${part(contextId)}/custody-challenges`, 'POST', value, undefined, signal) as Promise<CustodyChallenge>,
    acceptRecoveredCustody: (vaultId, contextId, value, signal) => send(`/vaults/${part(vaultId)}/encryption/contexts/${part(contextId)}/recovered-custody`, 'POST', value, undefined, signal) as Promise<RecoveredCustodyAcceptance>,
    recoveryState: (id, vaultId, signal) => json(`/encrypted/items/${part(id)}/recovery-state?vaultId=${part(vaultId)}`, { signal }),
    replaceFile: async (id, value, binary, revision, signal) => {
      localOnly(value); const body = new FormData(); body.set('record', JSON.stringify(value.record)); body.set('payloadKeyReferences', JSON.stringify(value.payloadKeyReferences)); body.set('vaultId', value.vaultId ?? ''); body.set('binary', new Blob([new Uint8Array(binary)], { type: 'application/octet-stream' }), 'ciphertext.bin');
      return json(`/encrypted/files/${part(id)}`, { method: 'PUT', signal, headers: { 'If-Match': revision }, body });
    },
    stageFile: async (vaultId, value, binary, revision, signal) => {
      localOnly(value); const staged = await json<{ id?: string; ownerId?: string; record?: { id?: string }; revision: string }>('/encrypted/files', { method: 'POST', signal, headers: { 'Content-Type': 'application/json', 'If-Match': revision, 'Idempotency-Key': crypto.randomUUID() }, body: JSON.stringify({ ...value, vaultId }) });
      const stagedId = staged.ownerId ?? staged.id ?? staged.record?.id;
      if (!stagedId || stagedId !== value.record.id || typeof staged.revision !== 'string' || !staged.revision || [staged.id, staged.ownerId, staged.record?.id].some(id => id !== undefined && id !== stagedId)) throw new PortableCryptoError('file-stage', 'The staged encrypted file identity or revision is invalid.');
      return request(`/encrypted/files/${part(stagedId)}/ciphertext`, { method: 'PUT', signal, headers: { 'Content-Type': 'application/octet-stream', 'If-Match': staged.revision, 'X-Knowz-Content-SHA256': fingerprintPublicKey(binary) }, body: new Uint8Array(binary) });
    },
  };
}
