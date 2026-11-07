import { BlobReader, ZipReader, type FileEntry } from '@zip.js/zip.js';
import { sha256 } from '@noble/hashes/sha2.js';
import { combinePortableGraphPackages } from './packages.js';
import {
  PORTABLE_LIMITS, PortableCryptoError, decodeBase64, fingerprintPublicKey,
  parsePortableJson, extractPortableGraph,
  type PortableGraph,
} from '../portable.js';

export interface PortableArchiveEntry { role: 'package' | 'file' | 'variant'; path: string; length: number; sha256: string }
export interface PortableArchiveManifest {
  formatId: 'knowz.archive'; formatVersion: 1; schemaVersion: 4; fileExtension: '.knowzarchive';
  requiredCapabilities: ['knowz.archive/v1']; sourceEdition: string; sourceTenantId: string; createdAt: string;
  preservationState: 'complete' | 'partial'; inventory: {
    archiveId: string; snapshotBoundary: string; parts: PortableArchiveEntry[]; payloadCounts: Record<string, number>;
    omissions: unknown[]; dependencyReport: null | 'partial';
  };
}
export interface OpenedPortableArchive {
  manifest: PortableArchiveManifest; manifestSha256: string; sourceTrust: 'trusted' | 'unverified';
  signerFingerprint?: string; graph: PortableGraph; packages: unknown[];
  readBinary(path: string, signal?: AbortSignal): Promise<Uint8Array>;
  /** Chunks are provisional until successful EOF. Publish recovered output only after EOF. */
  streamBinary(path: string, signal?: AbortSignal): ReadableStream<Uint8Array>; close(): Promise<void>;
}
export interface OpenPortableArchiveOptions {
  expectedSignerFingerprint?: string; signal?: AbortSignal;
  onProgress?: (verifiedEntries: number, totalEntries: number) => void;
}
const hashPattern = /^[0-9a-f]{64}$/;
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
function invalid(message = 'The archive inventory or encrypted graph is invalid.'): never { throw new PortableCryptoError('archive-invalid', message); }
function record(value: unknown): value is Record<string, unknown> { return !!value && typeof value === 'object' && !Array.isArray(value); }
function safePath(value: unknown): value is string {
  return typeof value === 'string' && value.length <= 240 && /^[a-zA-Z0-9._/-]+$/.test(value)
    && !value.startsWith('/') && !value.endsWith('/') && !value.split('/').some(part => !part || part === '.' || part === '..');
}
function manifestFromBytes(bytes: Uint8Array): PortableArchiveManifest {
  const parsed = parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes), PORTABLE_LIMITS.manifestBytes);
  if (!record(parsed) || parsed.formatId !== 'knowz.archive' || parsed.formatVersion !== 1 || parsed.schemaVersion !== 4
    || parsed.fileExtension !== '.knowzarchive' || !Array.isArray(parsed.requiredCapabilities) || parsed.requiredCapabilities.length !== 1
    || parsed.requiredCapabilities[0] !== 'knowz.archive/v1' || !record(parsed.inventory)) invalid('Unsupported archive format or required capability.');
  const inventory = parsed.inventory;
  if (!guidPattern.test(String(inventory.archiveId)) || typeof inventory.snapshotBoundary !== 'string' || !inventory.snapshotBoundary
    || !guidPattern.test(String(parsed.sourceTenantId)) || typeof parsed.sourceEdition !== 'string'
    || typeof parsed.createdAt !== 'string' || !Number.isFinite(Date.parse(parsed.createdAt))
    || !['complete', 'partial'].includes(String(parsed.preservationState)) || !Array.isArray(inventory.parts)
    || inventory.parts.length > PORTABLE_LIMITS.members - 1 || !Array.isArray(inventory.omissions) || !record(inventory.payloadCounts)
    || (inventory.dependencyReport !== null && inventory.dependencyReport !== 'partial')) invalid();
  if (parsed.preservationState === 'complete' && (inventory.omissions.length || inventory.dependencyReport !== null)) invalid('A complete archive cannot contain omissions or unresolved dependencies.');
  let total = 0; let metadata = 0; let packages = 0; const paths = new Set<string>();
  for (const value of inventory.parts) {
    if (!record(value) || !['package', 'file', 'variant'].includes(String(value.role)) || !safePath(value.path)
      || !Number.isSafeInteger(value.length) || Number(value.length) < 0 || !hashPattern.test(String(value.sha256))) invalid();
    const path = value.path;
    if (path === 'manifest.json' || path === 'signature.json' || paths.has(path.toLowerCase())) invalid('Duplicate or reserved archive member.');
    if (value.role === 'package' ? !/^packages\/[0-9]{6}\.json$/.test(path) : !path.startsWith('files/')) invalid('Archive member role and path disagree.');
    if (value.role === 'package' && Number(value.length) > PORTABLE_LIMITS.packageBytes) invalid('Archive package exceeds its 8 MiB JSON limit.');
    if (value.role === 'variant' && Number(value.length) > PORTABLE_LIMITS.encryptedFileBytes) invalid('Archive variant exceeds its 64 MiB file limit.');
    if (value.role === 'package') {
      if (++packages > PORTABLE_LIMITS.packages) invalid('Archive package count exceeds its limit.');
      metadata += Number(value.length);
      if (metadata > PORTABLE_LIMITS.metadataBytes) invalid('Archive metadata exceeds its 64 MiB limit.');
    }
    paths.add(path.toLowerCase()); total += Number(value.length);
  }
  if (!Number.isSafeInteger(total) || total > PORTABLE_LIMITS.archiveBytes) invalid('Archive expanded size exceeds its limit.');
  for (const value of Object.values(inventory.payloadCounts)) if (!Number.isSafeInteger(value) || Number(value) < 0) invalid('Invalid payload count inventory.');
  return parsed as unknown as PortableArchiveManifest;
}

/** Seekable Blob/File input, streaming member hashing, no plaintext/key persistence. */
export async function openPortableArchive(blob: Blob, options: OpenPortableArchiveOptions = {}): Promise<OpenedPortableArchive> {
  options.signal?.throwIfAborted();
  if (blob.size > PORTABLE_LIMITS.archiveBytes) invalid('Archive exceeds the supported size limit.');
  if (options.expectedSignerFingerprint && !hashPattern.test(options.expectedSignerFingerprint)) invalid('Supply the full trusted exporter fingerprint.');
  const zip = new ZipReader(new BlobReader(blob), { useWebWorkers: false, strictness: 'strict', checkSignature: true, checkOverlappingEntry: true });
  const entries = new Map<string, FileEntry>();
  const active = new Set<AbortController>();
  let closed = false;
  const close = async () => { if (!closed) { closed = true; for (const abort of active) abort.abort(new PortableCryptoError('archive-closed', 'The archive has been closed.')); entries.clear(); await zip.close(); } };
  try {
    const names = new Set<string>(); let total = 0;
    for await (const entry of zip.getEntriesGenerator()) {
      options.signal?.throwIfAborted();
      if (entry.directory || !safePath(entry.filename) || names.has(entry.filename.toLowerCase()) || names.size >= PORTABLE_LIMITS.members
        || !Number.isSafeInteger(entry.uncompressedSize) || !Number.isSafeInteger(entry.compressedSize)
        || entry.uncompressedSize < 0 || entry.compressedSize < 0 || entry.encrypted || ![0, 8].includes(entry.compressionMethod)
        || entry.uncompressedSize > Math.max(entry.compressedSize, 1) * PORTABLE_LIMITS.expansionRatio) invalid('Unsupported, duplicate or oversized ZIP entry.');
      total += entry.uncompressedSize;
      if (total > PORTABLE_LIMITS.archiveBytes) invalid('Archive expanded size exceeds its limit.');
      names.add(entry.filename.toLowerCase()); entries.set(entry.filename, entry as FileEntry);
    }
    const readEntry = async (path: string, maximumBytes: number, expected?: PortableArchiveEntry, retain = true, signal = options.signal): Promise<Uint8Array> => {
      signal?.throwIfAborted();
      if (closed) invalid('The archive has been closed.');
      const entry = entries.get(path);
      if (!entry || entry.uncompressedSize > maximumBytes || (expected && entry.uncompressedSize !== expected.length)) invalid('Missing archive member or incorrect declared length.');
      const chunks: Uint8Array[] = []; let length = 0; const hash = sha256.create();
      const sink = new WritableStream<Uint8Array>({ write(chunk) {
        signal?.throwIfAborted(); length += chunk.length;
        if (length > maximumBytes || length > entry.uncompressedSize) invalid('Archive member expanded beyond its declared limit.');
        hash.update(chunk); if (retain) chunks.push(chunk.slice());
      } });
      try {
        await entry.getData(sink, { signal, checkSignature: true, checkOverlappingEntry: true, useWebWorkers: false });
        if (length !== entry.uncompressedSize) invalid('Archive member is truncated.');
        const digest = Array.from(hash.digest(), b => b.toString(16).padStart(2, '0')).join('');
        if (expected && digest !== expected.sha256) invalid('Archive member hash does not match the signed inventory.');
        const result = new Uint8Array(retain ? length : 0); let offset = 0;
        for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.length; }
        return result;
      } finally { hash.destroy(); for (const chunk of chunks) chunk.fill(0); }
    };
    const manifestBytes = await readEntry('manifest.json', PORTABLE_LIMITS.manifestBytes);
    const manifest = manifestFromBytes(manifestBytes);
    const manifestSha256 = fingerprintPublicKey(manifestBytes);
    if (!entries.has('signature.json')) invalid('The required archive manifest signature is missing.');
    let signerFingerprint: string | undefined;
    if (entries.has('signature.json')) {
      const bytes = await readEntry('signature.json', PORTABLE_LIMITS.manifestBytes);
      const signature = parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes), PORTABLE_LIMITS.manifestBytes);
      if (!record(signature) || signature.algorithm !== 'ecdsa-p256-sha256-p1363' || !hashPattern.test(String(signature.publicKeyFingerprint))) invalid('Unsupported archive signature.');
      const publicKeyBytes = decodeBase64(signature.publicKey, 80, 256);
      signerFingerprint = fingerprintPublicKey(publicKeyBytes);
      if (signature.publicKeyFingerprint !== signerFingerprint || (options.expectedSignerFingerprint && signerFingerprint !== options.expectedSignerFingerprint)) invalid('Archive exporter identity does not match the trusted source.');
      const key = await crypto.subtle.importKey('spki', new Uint8Array(publicKeyBytes), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
      if (!await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, key, new Uint8Array(decodeBase64(signature.signature, 64)), new Uint8Array(manifestBytes))) invalid('Archive manifest signature is invalid.');
    } else if (options.expectedSignerFingerprint) invalid('The trusted archive signature is missing.');
    const listed = new Map(manifest.inventory.parts.map(part => [part.path, part]));
    if (entries.size !== listed.size + 1 + Number(entries.has('signature.json'))) invalid('Archive contains missing or unlisted members.');
    for (const path of entries.keys()) if (path !== 'manifest.json' && path !== 'signature.json' && !listed.has(path)) invalid('Archive contains an unlisted member.');
    const packages: unknown[] = []; let packageBytes = 0;
    for (const part of manifest.inventory.parts) {
      if (part.role === 'package') {
        packageBytes += part.length;
        if (packageBytes > PORTABLE_LIMITS.metadataBytes) invalid('Archive metadata exceeds its 64 MiB limit.');
        const bytes = await readEntry(part.path, PORTABLE_LIMITS.packageBytes, part);
        const pkg = parsePortableJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
        if (!record(pkg) || pkg.sourceTenantId !== manifest.sourceTenantId || pkg.sourceEdition !== manifest.sourceEdition) invalid('Package source identity disagrees with its signed inventory.');
        packages.push(pkg);
      }
    }
    if (!packages.length) invalid('Archive has no portable graph package.');
    const graph = extractPortableGraph(combinePortableGraphPackages(packages));
    if (manifest.preservationState === 'complete' && graph.preservationState !== 'complete') invalid('Archive completeness disagrees with its graph.');
    const referenced = new Map<string, number>();
    const files = [...graph.records.filter(row => row.ownerKind === 'file' && row.payloads.some(payload => payload.reference.role === 'binary')).map(row => ({ file: row.record, maximum: PORTABLE_LIMITS.encryptedFileBytes })),
      ...(graph.plainData.fileRecords ?? []).map(file => ({ file, maximum: PORTABLE_LIMITS.ordinaryFileBytes }))];
    for (const { file, maximum } of files) {
      if (!record(file) || typeof file.binaryEntryPath !== 'string' || file.binaryContentBase64 != null) invalid('File is outside the authenticated external binary graph.');
      const binary = listed.get(file.binaryEntryPath);
      if (!binary || binary.role !== 'file' || file.binarySha256 !== binary.sha256 || file.sizeBytes !== binary.length || binary.length > maximum) invalid('File metadata disagrees with the authenticated binary inventory or its protection-specific limit.');
      if (referenced.has(file.binaryEntryPath) && referenced.get(file.binaryEntryPath) !== maximum) invalid('Archive binary ownership is ambiguous across protection modes.');
      referenced.set(file.binaryEntryPath, maximum);
    }
    if (manifest.inventory.parts.some(part => part.role !== 'package' && !referenced.has(part.path))) invalid('Archive contains an unowned binary member.');
    const counts = manifest.inventory.payloadCounts;
    const required = graph.payloads.length + graph.dependentPayloadCount + ['knowledgeItems', 'comments', 'fileRecords', 'plainVersions'].reduce((total, name) => total + (graph.plainData[name]?.length ?? 0), 0);
    if (counts.required !== required || counts.packages !== packages.length || counts.files !== manifest.inventory.parts.filter(part => part.role !== 'package').length) invalid('Archive payload counts disagree with the verified contents.');
    // Authenticate all bounded metadata and classify live binary ownership before
    // decompressing any large member. A file role alone never grants the plain limit.
    for (const [index, part] of manifest.inventory.parts.entries()) {
      if (part.role !== 'package') await readEntry(part.path, referenced.get(part.path)!, part, false);
      options.onProgress?.(index + 1, manifest.inventory.parts.length);
    }
    const streamBinary = (path: string, signal = options.signal): ReadableStream<Uint8Array> => {
      signal?.throwIfAborted(); if (closed) invalid('The archive has been closed.');
      const expected = listed.get(path); const entry = entries.get(path); const maximum = referenced.get(path);
      if (!expected || !entry || maximum == null || entry.uncompressedSize !== expected.length) invalid('File is not in the verified archive inventory.');
      const abort = new AbortController(); const onAbort = () => abort.abort(signal?.reason);
      const hash = sha256.create(); let length = 0;
      let controller!: TransformStreamDefaultController<Uint8Array>;
      const stream = new TransformStream<Uint8Array, Uint8Array>({
        start(value) { controller = value; },
        transform(chunk, controller) {
          abort.signal.throwIfAborted(); length += chunk.length;
          if (length > expected.length || length > maximum) invalid('Archive member expanded beyond its declared limit.');
          hash.update(chunk); controller.enqueue(chunk);
        },
        flush() {
          abort.signal.throwIfAborted();
          if (length !== expected.length) invalid('Archive member is truncated.');
          const actual = Array.from(hash.digest(), b => b.toString(16).padStart(2, '0')).join('');
          if (actual !== expected.sha256) invalid('Archive member hash does not match the signed inventory.');
        },
      });
      // ZIP may be awaiting the underlying Blob while its writer is locked.
      // Error the readable directly so cancellation never waits for that I/O.
      abort.signal.addEventListener('abort', () => controller.error(abort.signal.reason), { once: true });
      signal?.addEventListener('abort', onAbort, { once: true }); active.add(abort);
      void entry.getData(stream.writable, { signal: abort.signal, checkSignature: true, checkOverlappingEntry: true, useWebWorkers: false })
        .catch(error => controller.error(error))
        .finally(() => { hash.destroy(); active.delete(abort); signal?.removeEventListener('abort', onAbort); });
      return stream.readable;
    };
    return { manifest, manifestSha256, sourceTrust: options.expectedSignerFingerprint && signerFingerprint ? 'trusted' : 'unverified', signerFingerprint, graph, packages,
      readBinary: async (path, signal) => {
        const entry = listed.get(path);
        if (!entry || entry.role === 'package') invalid('File is not in the verified archive inventory.');
        if (entry.length > PORTABLE_LIMITS.encryptedFileBytes) throw new PortableCryptoError('binary-stream-required', 'This ordinary file exceeds the 64 MiB buffered limit. Use streamBinary or the offline CLI archive extract command.');
        return readEntry(path, PORTABLE_LIMITS.encryptedFileBytes, entry, true, signal);
      }, streamBinary, close };
  } catch (error) {
    await close().catch(() => {});
    if (options.signal?.aborted) throw options.signal.reason;
    if (error instanceof PortableCryptoError) throw error;
    throw new PortableCryptoError('archive-invalid', 'The archive could not be verified. It may be corrupt, truncated or unsupported.');
  }
}
