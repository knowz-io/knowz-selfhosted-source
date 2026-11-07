import { PortableCryptoError, PORTABLE_LIMITS } from '../portable.js';
type Row = Record<string, any>;
const invalid = (message: string): never => { throw new PortableCryptoError('archive-packages', message); };
const object = (value: unknown): value is Row => !!value && typeof value === 'object' && !Array.isArray(value);
const canonical = (value: unknown): string => {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (object(value)) return `{${Object.keys(value).sort().map(key => `${JSON.stringify(key)}:${canonical(value[key])}`).join(',')}}`;
  return JSON.stringify(value) ?? 'null';
};
const shared = ['cryptoContexts', 'cryptoKeyCatalog', 'vaultCryptoContextLinks'];
const collections = ['sealedItems', 'sealedVersions', 'sealedFiles', 'sealedComments', 'omissions', 'payloadKeyReferences', 'sealedAttachments', 'suspendedReleasePolicies', 'dependencyBoundItems', 'plainVersions'];
const taxonomies = ['vaults', 'topics', 'tags', 'persons', 'locations', 'events'];
/** All members are hashed before assembly. No fragment may relax aggregate owner/root closure. */
export function combinePortableGraphPackages(packages: readonly unknown[]): Row {
  if (!packages.length || packages.length > PORTABLE_LIMITS.packages) invalid('Archive package count exceeds its limit.');
  const result: Row = { data: {} }; const headers = new Map<string, string>(); const identities = new Map<string, Map<string, string>>();
  const add = (name: string, values: unknown, target: Row, metadata: boolean, identity: (row: Row) => string) => {
    if (values == null) return;
    if (!Array.isArray(values) || values.length > PORTABLE_LIMITS.members || values.some(row => !object(row))) invalid('Invalid archive package collection.');
    const seen = identities.get(name) ?? new Map<string, string>(); identities.set(name, seen);
    const output = target[name] ??= [];
    for (const value of values as Row[]) {
      const id = identity(value); const serialized = canonical(value);
      if (!id) invalid('Invalid archive package record identity.');
      if (seen.has(id)) { if (!metadata || seen.get(id) !== serialized) invalid(`Conflicting or duplicate archive ${name} record.`); continue; }
      seen.set(id, serialized); output.push(value);
      if (output.length > PORTABLE_LIMITS.members) invalid('Aggregate graph collection exceeds its member limit.');
    }
  };
  const recordId = (row: Row) => typeof row.id === 'string' ? row.id.toLowerCase() : '';
  for (const [index, input] of packages.entries()) {
    if (!object(input) || input.schemaVersion !== 5 || !object(input.data) || !Array.isArray(input.requiredCapabilities)
      || !input.requiredCapabilities.includes('knowz.encrypted-graph/v1') || input.requiredCapabilities.some((cap: unknown) => !['knowz.encrypted-graph/v1', 'knowz.suspended-release/v1'].includes(String(cap)))
      || new Set(input.requiredCapabilities).size !== input.requiredCapabilities.length || 'detachedRecord' in input) invalid('Unsupported archive package envelope or relaxation flag.');
    const value = input as Row;
    const header = Object.fromEntries(Object.entries(value).filter(([name]) => name !== 'data' && !shared.includes(name) && !collections.includes(name)));
    if (index === 0) { Object.assign(result, header); for (const [name, value] of Object.entries(header)) headers.set(name, canonical(value)); }
    else if (Object.keys(header).length !== headers.size || Object.entries(header).some(([name, value]) => headers.get(name) !== canonical(value))) invalid('Archive package immutable headers disagree.');
    for (const name of shared) add(name, value[name], result, true, row => name === 'cryptoContexts' ? String(row.contextId ?? '') : name === 'cryptoKeyCatalog' ? `${row.contextId}:${row.generation}` : `${row.vaultId}:${row.contextId}`);
    for (const name of collections) add(name, value[name], result, false, row => name === 'payloadKeyReferences' ? canonical([row.ownerKind, row.ownerId, row.role, row.fieldName ?? null, row.versionId ?? null]) : name === 'omissions' ? canonical(row) : name === 'suspendedReleasePolicies' ? String(row.originalLockId ?? '') : recordId(row));
    for (const [name, values] of Object.entries(value.data as Row)) {
      if (name === 'archives') {
        if (!object(values)) invalid('Invalid archived record collections.');
        const archived = result.data.archives ??= {};
        for (const [kind, records] of Object.entries(values as Row)) {
          if (!Array.isArray(records)) invalid('Invalid archived record collections.');
          const output = archived[kind] ??= []; output.push(...records); if (output.length > PORTABLE_LIMITS.members) invalid('Archived record collection exceeds its member limit.');
        }
      } else add(name, values, result.data, taxonomies.includes(name), recordId);
    }
  }
  return result;
}
