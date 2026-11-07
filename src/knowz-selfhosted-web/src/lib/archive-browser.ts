import PasswordWorker from './archive-password-worker?worker&inline';
import { openPortableArchive, PORTABLE_LIMITS, PortableCryptoError, type OpenPortableArchiveOptions } from './vendored/portable/portable';
function passwordOperation(file: Blob, password: string, mode: 'protect' | 'unprotect', signal?: AbortSignal): Promise<Blob> {
  signal?.throwIfAborted();
  if (file.size > PORTABLE_LIMITS.protectedBrowserBytes) throw new PortableCryptoError('size-limit', 'Protected browser archives are limited to 128 MiB. Use the CLI for larger archives.');
  return new Promise((resolve, reject) => {
    const worker = new PasswordWorker();
    const finish = () => { worker.terminate(); signal?.removeEventListener('abort', abort); };
    const abort = () => { finish(); reject(signal?.reason ?? new DOMException('Cancelled', 'AbortError')); };
    signal?.addEventListener('abort', abort, { once: true });
    worker.onmessage = (event: MessageEvent<{ bytes?: Uint8Array; error?: string }>) => {
      finish();
      if (signal?.aborted) { event.data.bytes?.fill(0); reject(signal.reason); return; }
      if (!event.data.bytes) { reject(new PortableCryptoError('archive-password', event.data.error ?? 'Could not process archive protection.')); return; }
      const blob = new Blob([new Uint8Array(event.data.bytes)], { type: 'application/octet-stream' });
      event.data.bytes.fill(0); resolve(blob);
    };
    worker.onerror = () => { finish(); reject(new PortableCryptoError('archive-worker', 'The local password worker could not run. Check browser support, or use the CLI.')); };
    worker.postMessage({ file, password, mode }); password = '';
  });
}
export async function openBrowserArchive(file: Blob, password: string, options: OpenPortableArchiveOptions = {}) {
  const header = new TextDecoder().decode(await file.slice(0, 22).arrayBuffer());
  options.signal?.throwIfAborted();
  const blob = header.startsWith('age-encryption.org/v1') ? await passwordOperation(file, password, 'unprotect', options.signal) : file;
  return { archive: await openPortableArchive(blob, options), blob };
}
export const protectBrowserArchive = (blob: Blob, password: string, signal?: AbortSignal) => passwordOperation(blob, password, 'protect', signal);
export function saveLocalDownload(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob); const anchor = document.createElement('a');
  anchor.href = url; anchor.download = filename; anchor.rel = 'noopener'; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
