import { PortableCryptoError, decodeBase64, fingerprintPublicKey, parsePortableJson } from '../portable.js';
export interface PortableCustodyDependency {
  profile: 'tenant-content-release' | 'recipient-item-x25519'; sourceTenantId: string; sourceKeyRef?: string | null;
  recoveryState: 'source-provider-required' | 'recipient-key-required' | 'missing-recipient-public-key'; originalItemBindingId?: string | null; publicKeyFingerprint?: string | null; recipientPublicKey?: string | null;
}
export interface PortableDependencyBoundVersion {
  formatVersion: 1; id: string; knowledgeId: string; originalKnowledgeId: string; versionNumber: number;
  sourceTenantId: string; algorithmProfile: 'platform-managed-v1'; ciphertext: string; contentNonce: string;
  encryptedFieldsJson?: string | null; custody: PortableCustodyDependency; originalEnvelopeMetadata: string; createdAt?: string; updatedAt?: string;
}
export interface PortableDependencyBoundKnowledge {
  formatVersion: 1; id: string; originalItemId: string; sourceTenantId: string; originalOwnerUserId: string; originalIsSealed?: boolean;
  vaultIds: string[]; primaryVaultId?: string | null; algorithmProfile: 'platform-managed-v1'; ciphertext: string; contentNonce: string;
  encryptedFieldsJson?: string | null; custody: PortableCustodyDependency; publicRecord?: Record<string, unknown> | null;
  versions: PortableDependencyBoundVersion[]; createdAt?: string; updatedAt?: string;
}
export interface PortableSuspendedReleasePolicy {
  formatVersion: 1; activationState: 'suspended'; originalLockId: string; knowledgeId: string; originalKnowledgeId: string;
  sourceTenantId: string; originalOwnerUserId: string; originalIsSealed?: boolean; originalStatus?: string; securityLevel?: string;
  gracePeriodHours?: number; conditions: Record<string, unknown>[]; recipients: Record<string, unknown>[]; custodyDependencies: PortableCustodyDependency[];
  [key: string]: unknown;
}
type Row = Record<string, any>;
const invalid = (message = 'Invalid suspended release policy or provider dependency.'): never => { throw new PortableCryptoError('suspended-release', message); };
const guid = (value: unknown) => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
const digest = (value: unknown) => typeof value === 'string' && /^[a-f0-9]{64}$/.test(value);
const object = (value: unknown): value is Row => !!value && typeof value === 'object' && !Array.isArray(value);
const allowed = (value: unknown, names: string[]): Row => { if (!object(value) || Object.keys(value).some(key => !names.includes(key))) invalid('Unknown suspended release fields or secret authority.'); return value as Row; };
const array = (value: unknown, maximum: number): Row[] => { if (!Array.isArray(value) || value.length > maximum || value.some(row => !object(row))) invalid(); return value as Row[]; };
const nonnegative = (value: unknown) => value == null || typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
export function validateCustodyDependency(input: unknown): PortableCustodyDependency {
  const value = allowed(input, ['profile', 'sourceTenantId', 'sourceKeyRef', 'recoveryState', 'originalItemBindingId', 'publicKeyFingerprint', 'recipientPublicKey']);
  if (!guid(value.sourceTenantId)) invalid();
  if (value.profile === 'tenant-content-release') {
    const prefix = `tenant:${value.sourceTenantId}:purpose:content-release:v`; const reference = value.sourceKeyRef;
    if (typeof reference !== 'string' || !reference.startsWith(prefix) || !/^[1-9][0-9]*$/.test(reference.slice(prefix.length)) || !Number.isSafeInteger(Number(reference.slice(prefix.length)))
      || value.recoveryState !== 'source-provider-required' || value.recipientPublicKey != null) invalid('Foreign or unsupported source provider key reference.');
  } else if (value.profile === 'recipient-item-x25519') {
    if (!guid(value.originalItemBindingId) || !digest(value.publicKeyFingerprint) || !['recipient-key-required', 'missing-recipient-public-key'].includes(value.recoveryState)) invalid();
    if (value.recipientPublicKey != null) { if (fingerprintPublicKey(decodeBase64(value.recipientPublicKey, 32)) !== value.publicKeyFingerprint) invalid(); }
    else if (value.recoveryState !== 'missing-recipient-public-key') invalid();
  } else invalid('Unknown custody dependency profile.');
  return value as PortableCustodyDependency;
}
export function validateDependencyBoundItem(input: unknown): PortableDependencyBoundKnowledge {
  const value = allowed(input, ['formatVersion', 'id', 'originalItemId', 'sourceTenantId', 'originalOwnerUserId', 'originalIsSealed', 'vaultIds', 'primaryVaultId', 'algorithmProfile', 'ciphertext', 'contentNonce', 'encryptedFieldsJson', 'custody', 'publicRecord', 'createdAt', 'updatedAt', 'versions']);
  if (value.formatVersion !== 1 || !guid(value.id) || !guid(value.originalItemId) || !guid(value.sourceTenantId) || value.algorithmProfile !== 'platform-managed-v1') invalid();
  decodeBase64(value.ciphertext, 16, 8 * 1024 ** 2); decodeBase64(value.contentNonce, 12);
  const custody = validateCustodyDependency(value.custody);
  if (custody.profile !== 'tenant-content-release' || custody.sourceTenantId !== value.sourceTenantId || !Array.isArray(value.vaultIds) || value.vaultIds.some((id: unknown) => !guid(id))
    || value.primaryVaultId != null && !value.vaultIds.includes(value.primaryVaultId)) invalid();
  if (value.publicRecord != null && (!object(value.publicRecord) || value.publicRecord.id !== value.id || value.publicRecord.content)) invalid('Dependency-bound ciphertext cannot masquerade as plain content.');
  const seen = new Set<string>();
  for (const version of array(value.versions, 10_000)) {
    allowed(version, ['formatVersion', 'id', 'knowledgeId', 'originalKnowledgeId', 'versionNumber', 'sourceTenantId', 'algorithmProfile', 'ciphertext', 'contentNonce', 'encryptedFieldsJson', 'custody', 'originalEnvelopeMetadata', 'createdAt', 'updatedAt']);
    if (version.formatVersion !== 1 || !guid(version.id) || seen.has(version.id) || version.knowledgeId !== value.id || version.originalKnowledgeId !== value.originalItemId
      || !Number.isSafeInteger(version.versionNumber) || version.versionNumber < 1 || !guid(version.sourceTenantId) || version.algorithmProfile !== 'platform-managed-v1') invalid('Dependency-bound history has an invalid owner or envelope.');
    seen.add(version.id); decodeBase64(version.ciphertext, 16, 8 * 1024 ** 2); decodeBase64(version.contentNonce, 12);
    const versionCustody = validateCustodyDependency(version.custody);
    if (versionCustody.profile !== 'tenant-content-release' || versionCustody.sourceTenantId !== version.sourceTenantId || typeof version.originalEnvelopeMetadata !== 'string') invalid();
    const metadata = allowed(parsePortableJson(version.originalEnvelopeMetadata, 1024 ** 2), ['sealedSnapshot', 'encryptionEnvelopeVersion', 'encryptedDek', 'encryptedDekNonce', 'ephemeralPublicKey', 'contentNonce', 'encryptionKeyRef', 'encryptedFieldsJson', 'cryptoBindingVaultId', 'wrappedDek', 'dekNonce', 'cryptoVersion']);
    if (metadata.sealedSnapshot !== true || metadata.cryptoVersion !== 'platform-managed-v1' || metadata.contentNonce !== version.contentNonce || metadata.encryptionKeyRef !== versionCustody.sourceKeyRef
      || ['encryptedDek', 'encryptedDekNonce', 'ephemeralPublicKey', 'wrappedDek', 'dekNonce', 'encryptionEnvelopeVersion'].some(name => metadata[name] != null)
      || metadata.encryptedFieldsJson != null && metadata.encryptedFieldsJson !== version.encryptedFieldsJson) invalid('Dependency-bound original history mixes another envelope scheme.');
  }
  return value as PortableDependencyBoundKnowledge;
}
export function validateSuspendedReleasePolicy(input: unknown): PortableSuspendedReleasePolicy {
  const value = allowed(input, ['formatVersion', 'activationState', 'originalLockId', 'knowledgeId', 'originalKnowledgeId', 'sourceTenantId', 'originalOwnerUserId', 'originalIsSealed', 'originalStatus', 'securityLevel', 'gracePeriodHours', 'gracePeriodEndsAt', 'conditionMetAt', 'releasedAt', 'excludeAiWhileSealed', 'decryptOnFetch', 'encryptedDeliveryEnabled', 'encryptedCopySentAt', 'recipientKeyDeliveredAt', 'originalCollectionId', 'originalCollectionVaultId', 'collectionName', 'collectionDescription', 'conditions', 'recipients', 'custodyDependencies', 'recipientEnvelopeJson']);
  if (value.formatVersion !== 1 || value.activationState !== 'suspended' || ['originalLockId', 'knowledgeId', 'originalKnowledgeId', 'sourceTenantId', 'originalOwnerUserId'].some(name => !guid(value[name]))
    || !['Sealed', 'ConditionMet', 'InGracePeriod', 'Released', 'Cancelled', 'Expired', 'Revoked'].includes(value.originalStatus ?? 'Sealed')
    || !['AccessControl', 'Encrypted', 'PlatformManaged'].includes(value.securityLevel ?? 'AccessControl') || !nonnegative(value.gracePeriodHours) || (value.gracePeriodHours ?? 48) > 87600) invalid();
  const conditions = array(value.conditions, 1024); const conditionIds = new Set<string>();
  for (const condition of conditions) {
    allowed(condition, ['originalConditionId', 'originalParentConditionId', 'type', 'status', 'releaseAt', 'originalPersonId', 'originalTrustCircleId', 'relativeOffsetDays', 'requiredConfirmations', 'currentConfirmations', 'conditionDescription', 'originalDesignatedUserId', 'compositeOperator', 'lastEvaluatedAt', 'evaluationNotes']);
    if (!guid(condition.originalConditionId) || conditionIds.has(condition.originalConditionId) || !['Time', 'Death', 'CircleQuorum', 'AIEvent', 'Manual', 'Composite'].includes(condition.type ?? 'Time')
      || !['Pending', 'Met', 'Failed', 'Cancelled'].includes(condition.status ?? 'Pending') || condition.compositeOperator != null && !['And', 'Or', 'AND', 'OR'].includes(condition.compositeOperator)
      || !nonnegative(condition.requiredConfirmations) || !nonnegative(condition.currentConfirmations)) invalid();
    conditionIds.add(condition.originalConditionId);
    const seen = new Set([condition.originalConditionId]); let current = condition;
    while (current.originalParentConditionId != null) { const parent = conditions.find(c => c.originalConditionId === current.originalParentConditionId); if (!parent || seen.has(parent.originalConditionId)) invalid('Suspended release condition parent is absent or cyclic.'); seen.add(parent!.originalConditionId); current = parent!; }
  }
  const recipientIds = new Set<string>();
  for (const recipient of array(value.recipients, 1024)) {
    allowed(recipient, ['originalRecipientId', 'recipientEmail', 'recipientName', 'originalPersonId', 'originalIdentityId', 'originalIdentityNoteKeyId', 'identityNoteKeyFingerprint', 'accessExpiresAfterHours', 'originalTokenExpiresAt', 'notifiedAt', 'accessedAt']);
    if (!guid(recipient.originalRecipientId) || recipientIds.has(recipient.originalRecipientId) || typeof recipient.recipientEmail !== 'string' || !recipient.recipientEmail.trim() || recipient.recipientEmail.length > 320 || !nonnegative(recipient.accessExpiresAfterHours)
      || recipient.identityNoteKeyFingerprint != null && !digest(recipient.identityNoteKeyFingerprint)) invalid(); recipientIds.add(recipient.originalRecipientId);
  }
  const dependencies = array(value.custodyDependencies, 256).map(validateCustodyDependency);
  if (value.recipientEnvelopeJson != null) {
    if (typeof value.recipientEnvelopeJson !== 'string') invalid();
    const message = allowed(parsePortableJson(value.recipientEnvelopeJson, 1024 ** 2), ['format', 'version', 'alg', 'binding', 'contentAadBound', 'envelope', 'fingerprint', 'createdAt']);
    const binding = allowed(message.binding, ['vaultId', 'keyType', 'itemId']); const algorithms = allowed(message.alg, ['kdf', 'keyAgreement', 'dekWrap', 'content']); const envelope = allowed(message.envelope, ['ephemeralPublicKey', 'encryptedDek', 'dekNonce', 'contentNonce', 'ciphertext']);
    if (message.format !== 'knowz-encrypted-message-v2' || message.version !== 2 || message.contentAadBound !== true || binding.vaultId !== value.originalLockId || binding.itemId !== value.originalLockId || binding.keyType !== 'x25519-item-public'
      || algorithms.kdf !== 'hkdf-sha256' || algorithms.keyAgreement !== 'x25519' || algorithms.dekWrap !== 'aes-256-gcm' || algorithms.content !== 'aes-256-gcm'
      || !dependencies.some(d => d.profile === 'recipient-item-x25519' && d.originalItemBindingId === value.originalLockId && d.publicKeyFingerprint === message.fingerprint)) invalid();
    decodeBase64(envelope.ciphertext, 16, 1024 ** 2); decodeBase64(envelope.contentNonce, 12); decodeBase64(envelope.encryptedDek, 48); decodeBase64(envelope.dekNonce, 12); decodeBase64(envelope.ephemeralPublicKey, 32);
  }
  return value as PortableSuspendedReleasePolicy;
}
