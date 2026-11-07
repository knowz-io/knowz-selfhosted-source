import { PortableCryptoError, parsePortableJson } from '../portable.js';
type Row = Record<string, any>;
const invalid = (message: string): never => { throw new PortableCryptoError('graph-closure', message); };
const guid = (value: unknown) => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) && !/^0{8}-0{4}-0{4}-0{4}-0{12}$/.test(value);
const index = (rows: Row[], kind: string) => { const values = new Map<string, Row>(); for (const row of rows) { if (!guid(row.id) || values.has(row.id.toLowerCase())) invalid(`Duplicate or invalid ${kind} owner.`); values.set(row.id.toLowerCase(), row); } return values; };
const owns = (values: Map<string, Row>, id: unknown) => typeof id === 'string' && values.has(id.toLowerCase());
const hasField = (row: Row, field: string) => { if (!row.encryptedFieldsJson) return false; const json = parsePortableJson(row.encryptedFieldsJson) as Row; return !!json.fields?.[field]; };
export function plainVersionFields(metadata: unknown): string | undefined {
  if (metadata == null || metadata === '') return undefined;
  if (typeof metadata !== 'string') invalid('Plain history snapshot metadata must be serialized JSON.');
  const root = parsePortableJson(metadata as string) as Row;
  if (!root || typeof root !== 'object' || Array.isArray(root)) invalid('Plain history snapshot metadata must be an object.');
  const names = new Set<string>(); let fields: string | undefined;
  for (const [key, value] of Object.entries(root)) {
    const name = key.toLowerCase();
    if (names.has(name)) invalid('Plain history snapshot metadata is ambiguous.'); names.add(name);
    if (['sealedsnapshot', 'isencrypted', 'pendingpostenrichmentencrypt'].includes(name) && value !== false && value !== null) invalid('Encrypted body history cannot be represented as ordinary history.');
    if (['encrypteddek', 'encrypteddeknonce', 'contentnonce', 'wrappeddek', 'deknonce', 'ephemeralpublickey', 'cryptoversion', 'encryptionkeyref', 'portablerecoverystate'].includes(name) && value !== null && value !== '') invalid('Encrypted body history cannot be represented as ordinary history.');
    if (name !== 'encryptedfieldsjson') continue;
    if (value !== null && typeof value !== 'string') invalid('Encrypted history fields must be serialized JSON.');
    fields = typeof value === 'string' ? value : undefined;
  }
  if (fields) {
    const values = (parsePortableJson(fields) as Row).fields;
    const protectedNames = new Set(Object.keys(values ?? {}).map(name => name.toLowerCase()));
    for (const [name, value] of Object.entries(root))
      if (protectedNames.has(name.toLowerCase()) && value !== null && value !== '')
        invalid('Protected history fields have plaintext metadata companions.');
  }
  return fields;
}
/** Full graph closure is mandatory for archives. A detached API record relies on the server's authorized owner view. */
export function validatePortableGraphClosure(input: Row, detached = false): void {
  const rows = (name: string): Row[] => input[name] ?? [];
  const data = (name: string): Row[] => input.data?.[name] ?? [];
  const knowledge = index([...rows('sealedItems'), ...data('knowledgeItems'), ...rows('dependencyBoundItems')], 'knowledge');
  const historyOwners = new Set([...rows('sealedItems'), ...data('knowledgeItems')].map(row => row.id.toLowerCase()));
  const comments = index([...rows('sealedComments'), ...data('comments')], 'comment');
  const files = index([...rows('sealedFiles'), ...data('fileRecords')], 'file');
  index([...rows('sealedVersions'), ...rows('plainVersions'), ...rows('dependencyBoundItems').flatMap(i => i.versions ?? [])], 'version');
  index(rows('sealedAttachments'), 'attachment'); const vaults = index(data('vaults'), 'vault');
  const taxonomy = Object.fromEntries(['topics', 'tags', 'persons', 'locations', 'events'].map(name => [name, index(data(name), name)]));
  for (const row of rows('sealedItems')) {
    if (!guid(row.originalItemId) || !guid(row.cryptoBindingId)) invalid('Encrypted knowledge lacks its immutable original identity.');
  }
  for (const row of [...rows('sealedItems'), ...data('knowledgeItems')]) {
    if (!detached && row.vaultIds?.some((id: string) => !owns(vaults, id))) invalid('Encrypted knowledge references an absent vault owner.');
    for (const name of ['vaultIds', 'tagIds', 'personIds', 'locationIds', 'eventIds']) if (Object.prototype.hasOwnProperty.call(row, name) && (!Array.isArray(row[name]) || row[name].some((value: unknown) => !guid(value)))) invalid('Encrypted knowledge has invalid relation links.');
    if (row.primaryVaultId != null && (!guid(row.primaryVaultId) || !row.vaultIds?.includes(row.primaryVaultId))) invalid('Encrypted knowledge primary vault is outside its owner links.');
    if (row.topicId != null && (!guid(row.topicId) || !detached && !owns(taxonomy.topics!, row.topicId))) invalid('Encrypted knowledge topic has no taxonomy owner.');
    for (const [name, catalog] of Object.entries({ tagIds: 'tags', personIds: 'persons', locationIds: 'locations', eventIds: 'events' }))
      if (!detached && (row[name] ?? []).some((id: string) => !owns(taxonomy[catalog]!, id))) invalid('Encrypted knowledge relation has no taxonomy owner.');
    for (const [name, catalog] of Object.entries({ personLinks: 'persons', locationLinks: 'locations', eventLinks: 'events' })) {
      if (row[name] != null && (!Array.isArray(row[name]) || row[name].some((link: Row | null) => !link || typeof link !== 'object' || !guid(link.entityId) || !detached && !owns(taxonomy[catalog]!, link.entityId)))) invalid('Encrypted knowledge rich relation link is absent or malformed.');
    }
  }
  for (const row of vaults.values()) if (row.personIds !== undefined && (!Array.isArray(row.personIds) || row.personIds.some((id: unknown) => !guid(id) || !detached && !owns(taxonomy.persons!, id)))) invalid('Vault subject list is missing or references an absent person.');
  if (!detached) {
    for (const row of comments.values()) {
      if (!owns(knowledge, row.knowledgeId)) invalid('Comment references an absent knowledge owner.');
      const visited = new Set<string>([row.id.toLowerCase()]); let current = row;
      while (current.parentCommentId != null) {
        if (!owns(comments, current.parentCommentId)) invalid('Comment references an absent parent owner.');
        const parent = comments.get(current.parentCommentId.toLowerCase())!;
        if (parent.knowledgeId.toLowerCase() !== row.knowledgeId.toLowerCase()) invalid('Comment parent belongs to another knowledge owner.');
        if (visited.has(parent.id.toLowerCase())) invalid('Comment parent cycle is invalid.');
        visited.add(parent.id.toLowerCase()); current = parent;
      }
    }
    for (const row of rows('sealedAttachments')) {
      if (!owns(files, row.fileRecordId) || row.knowledgeId != null && !owns(knowledge, row.knowledgeId)
        || row.commentId != null && !owns(comments, row.commentId) || row.inboxId != null && !data('inboxItems').some(i => i.id === row.inboxId)
        || row.knowledgeId == null && row.commentId == null && row.inboxId == null) invalid('Attachment references an absent file or owner.');
    }
    for (const row of rows('sealedFiles')) {
      if (row.knowledgeId != null && !owns(knowledge, row.knowledgeId) || row.vaultId != null && !owns(vaults, row.vaultId)) invalid('File references an absent owning record or vault.');
    }
    for (const row of rows('sealedVersions')) if (!owns(knowledge, row.knowledgeId) || !Number.isSafeInteger(row.versionNumber) || row.versionNumber < 1) invalid('Encrypted history lacks its owner or version.');
    for (const row of rows('plainVersions')) if (!guid(row.knowledgeId) || !historyOwners.has(row.knowledgeId.toLowerCase()) || !guid(row.originalKnowledgeId) || !Number.isSafeInteger(row.versionNumber) || row.versionNumber < 1 || row.storageStrategy !== 'full') invalid('Plain history lacks its owner or materialized full snapshot.');
  }
  for (const row of data('knowledgeItems')) {
    if (row.originalItemId != null && !guid(row.originalItemId)) invalid('Knowledge original identity is invalid.');
    if (hasField(row, 'title') && row.title && row.title !== 'Encrypted item'
      || hasField(row, 'summary') && row.summary || hasField(row, 'content') && row.content
      || hasField(row, 'briefSummary') && row.briefSummary) invalid('Protected knowledge fields have plaintext companions.');
    if (row.encryptedFieldsJson) {
      const fields = (parsePortableJson(row.encryptedFieldsJson) as Row).fields;
      const protectedNames = new Set(Object.keys(fields ?? {}).map(name => name.toLowerCase()));
      for (const [name, value] of Object.entries(row)) {
        if (['title', 'summary', 'content', 'briefsummary'].includes(name.toLowerCase())) continue;
        if (protectedNames.has(name.toLowerCase()) && value !== null && value !== undefined && value !== '')
          invalid('Protected knowledge fields have plaintext companions.');
      }
    }
  }
  for (const row of rows('plainVersions')) {
    const fields = plainVersionFields(row.snapshotJson);
    const protectedRow = { encryptedFieldsJson: fields };
    if (hasField(protectedRow, 'title') && row.title && row.title !== 'Encrypted item'
      || hasField(protectedRow, 'summary') && row.summary || hasField(protectedRow, 'content') && row.content || hasField(protectedRow, 'tags') && row.tags) invalid('Protected history fields have plaintext companions.');
  }
  const links = new Set<string>(); const primaries = new Set<string>();
  for (const row of rows('vaultCryptoContextLinks')) {
    const id = `${row.vaultId}:${row.contextId}`;
    if (links.has(id) || row.isPrimaryWriteContext && primaries.has(row.vaultId)) invalid('Duplicate links or conflicting primary write contexts.');
    links.add(id); if (row.isPrimaryWriteContext) primaries.add(row.vaultId);
    if ((!detached && !owns(vaults, row.vaultId)) || !rows('cryptoContexts').some(c => c.contextId === row.contextId)) invalid('Crypto context link references an absent vault or context.');
    if (row.isPrimaryWriteContext) {
      const keys = rows('cryptoKeyCatalog').filter(k => k.contextId === row.contextId && k.fingerprint === row.primaryWriteFingerprint && (row.primaryWriteGeneration == null || k.generation === row.primaryWriteGeneration));
      if (keys.length !== 1) invalid('Primary write key generation is absent or ambiguous.');
    }
  }
  const groups = new Map<string, Row[]>();
  const missingDeclared = (id: string) => input.preservationState !== 'complete' && rows('omissions').some(o => o.ownerKind === 'file' && o.ownerId === id && typeof o.reason === 'string' && o.reason.trim());
  for (const row of files.values()) {
    const rootId = row.rootFileRecordId ?? row.id; const version = row.fileVersion ?? 1;
    if (!guid(rootId) || row.previousFileRecordId != null && !guid(row.previousFileRecordId) || !Number.isSafeInteger(version) || version < 1 || row.isCurrentVersion != null && typeof row.isCurrentVersion !== 'boolean') invalid('File lineage has an invalid version or reference.');
    const root = files.get(rootId.toLowerCase());
    if (!root && !detached && !missingDeclared(rootId)) invalid('File lineage is missing its root.');
    if (root && ((root.fileVersion ?? 1) !== 1 || (root.rootFileRecordId ?? root.id) !== rootId || root.previousFileRecordId != null)) invalid('File lineage root is invalid.');
    if (version === 1 && (rootId !== row.id || row.previousFileRecordId != null) || version > 1 && row.previousFileRecordId == null) invalid('File lineage lacks its exact ancestry.');
    if (row.previousFileRecordId != null) {
      const previous = files.get(row.previousFileRecordId.toLowerCase());
      if (!previous && !detached && !missingDeclared(row.previousFileRecordId)) invalid('File lineage is missing its previous version.');
      if (previous && ((previous.rootFileRecordId ?? previous.id) !== rootId || (previous.fileVersion ?? 1) !== version - 1)) invalid('File lineage has a cycle, skipped version or foreign root.');
    }
    const group = groups.get(rootId) ?? []; group.push(row); groups.set(rootId, group);
    if (!guid(row.originalFileId ?? row.id)) invalid('File lacks its immutable original identity.');
  }
  for (const group of groups.values()) if (group.filter(f => f.isCurrentVersion !== false).length > 1 || new Set(group.map(f => f.fileVersion ?? 1)).size !== group.length) invalid('File lineage has duplicate current or historical versions.');
  for (const row of data('fileRecords')) if (row.fileName && row.fileName !== 'encrypted-file' && hasField(row, 'fileName')) invalid('Protected file name has a plaintext companion.');
  for (const row of rows('sealedAttachments')) if (row.title && hasField(row, 'title') || row.description && hasField(row, 'description')) invalid('Protected attachment fields have plaintext companions.');
}
