import { Encrypter, Decrypter } from 'age-encryption';
import { PortableCryptoError, PORTABLE_LIMITS } from '../portable.js';

const failure = () => new PortableCryptoError('archive-password', 'Incorrect archive password, or the archive is corrupt or has been changed.');
const sizeFailure = () => new PortableCryptoError('size-limit', 'The archive exceeds the supported size limit. Use the CLI for larger archives.');

/** Consumes through EOF, including the final authentication tag, before returning bytes. */
export async function readBoundedStream(source: ReadableStream<Uint8Array>, maximumBytes: number, signal?: AbortSignal): Promise<Uint8Array> {
  signal?.throwIfAborted();
  if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 0 || maximumBytes > PORTABLE_LIMITS.archiveBytes) throw sizeFailure();
  const reader = source.getReader();
  const chunks: Uint8Array[] = []; let length = 0;
  const abort = () => { void reader.cancel(signal?.reason).catch(() => {}); };
  signal?.addEventListener('abort', abort, { once: true });
  try {
    while (true) {
      signal?.throwIfAborted();
      const { value, done } = await reader.read();
      signal?.throwIfAborted();
      if (done) break;
      length += value.byteLength;
      if (!Number.isSafeInteger(length) || length > maximumBytes) throw sizeFailure();
      chunks.push(value.slice());
    }
    const result = new Uint8Array(length); let offset = 0;
    for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.length; }
    return result;
  } catch (error) { await reader.cancel().catch(() => {}); throw error; }
  finally { signal?.removeEventListener('abort', abort); reader.releaseLock(); for (const chunk of chunks) chunk.fill(0); }
}

function safeStream(source: ReadableStream<Uint8Array>, signal?: AbortSignal): ReadableStream<Uint8Array> {
  const reader = source.getReader();
  let abort: (() => void) | undefined;
  const cleanup = () => { if (abort) signal?.removeEventListener('abort', abort); };
  return new ReadableStream({
    start(controller) {
      abort = () => { cleanup(); void reader.cancel().catch(() => {}); controller.error(signal?.reason); };
      signal?.addEventListener('abort', abort, { once: true });
      if (signal?.aborted) abort();
    },
    async pull(controller) {
      try {
        const result = await reader.read();
        signal?.throwIfAborted();
        if (result.done) { cleanup(); controller.close(); }
        else controller.enqueue(result.value);
      } catch { cleanup(); controller.error(signal?.aborted ? signal.reason : failure()); }
    },
    async cancel(reason) { cleanup(); await reader.cancel(reason).catch(() => {}); },
  });
}

/** Header syntax/cost gate only. All cryptography and authentication belong to the age library. */
async function guardPasswordHeader(source: ReadableStream<Uint8Array>, signal?: AbortSignal): Promise<ReadableStream<Uint8Array>> {
  const reader = source.getReader(); const buffered: Uint8Array[] = [];
  const abort = () => { void reader.cancel(signal?.reason).catch(() => {}); };
  const cleanup = () => signal?.removeEventListener('abort', abort);
  signal?.addEventListener('abort', abort, { once: true });
  let prefix = '';
  try {
    while (true) {
      signal?.throwIfAborted();
      const { done, value } = await reader.read();
      signal?.throwIfAborted();
      if (done) throw failure();
      buffered.push(value);
      const available = value.subarray(0, 16384 - prefix.length);
      prefix += Array.from(available, b => String.fromCharCode(b)).join('');
      const stanza = /\n-> scrypt ([A-Za-z0-9+/]{22}) ([^\n]*)\n/.exec(prefix);
      if (stanza && (!/^[1-9][0-9]*$/.test(stanza[2]!) || Number(stanza[2]) > PORTABLE_LIMITS.scryptLogN)) {
        throw new PortableCryptoError('archive-kdf', 'Archive password derivation cost is unsupported or too high.');
      }
      const end = /\n--- [A-Za-z0-9+/]{43}\n/.exec(prefix);
      if (end) {
        const header = prefix.slice(0, end.index + end[0].length);
        if (!/^age-encryption\.org\/v1\n-> scrypt [A-Za-z0-9+/]{22} [1-9][0-9]*\n[A-Za-z0-9+/]{43}\n--- [A-Za-z0-9+/]{43}\n$/.test(header)) throw failure();
        break;
      }
      if (prefix.length >= 16384) throw new PortableCryptoError('archive-header', 'Archive protection header is invalid or too large.');
    }
  } catch (error) { cleanup(); await reader.cancel().catch(() => {}); reader.releaseLock(); throw error; }
  return new ReadableStream({
    start(controller) { for (const chunk of buffered) controller.enqueue(chunk); buffered.length = 0; },
    async pull(controller) { try { const item = await reader.read(); if (item.done) { cleanup(); reader.releaseLock(); controller.close(); } else controller.enqueue(item.value); } catch (error) { cleanup(); controller.error(error); } },
    async cancel(reason) { cleanup(); await reader.cancel(reason).catch(() => {}); },
  });
}

/** Browser callers run this synchronous-KDF operation in a bundled, cancellable worker. */
export async function protectArchiveStream(source: ReadableStream<Uint8Array>, password: string, signal?: AbortSignal): Promise<ReadableStream<Uint8Array>> {
  signal?.throwIfAborted();
  if (!password) throw new PortableCryptoError('archive-password-required', 'Enter an archive password.');
  const encrypter = new Encrypter(); encrypter.setScryptWorkFactor(PORTABLE_LIMITS.scryptLogN); encrypter.setPassphrase(password);
  try { const stream = await encrypter.encrypt(source); signal?.throwIfAborted(); return safeStream(stream, signal); }
  catch (error) { if (signal?.aborted) throw signal.reason; throw error; }
}
export async function unprotectArchiveStream(source: ReadableStream<Uint8Array>, password: string, signal?: AbortSignal): Promise<ReadableStream<Uint8Array>> {
  signal?.throwIfAborted();
  if (!password) throw new PortableCryptoError('archive-password-required', 'Enter the archive password.');
  const guarded = await guardPasswordHeader(source, signal);
  const decrypter = new Decrypter(); decrypter.addPassphrase(password);
  try { return safeStream(await decrypter.decrypt(guarded), signal); }
  catch { await guarded.cancel().catch(() => {}); if (signal?.aborted) throw signal.reason; throw failure(); }
}
