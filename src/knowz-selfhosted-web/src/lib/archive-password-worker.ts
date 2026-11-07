/** Dedicated local worker only. Never accepts or sends window/channel messages. */
import { protectArchiveStream, unprotectArchiveStream, readBoundedStream, PORTABLE_LIMITS, PortableCryptoError } from './vendored/portable/portable';
type Input = { file: Blob; password: string; mode: 'protect' | 'unprotect' };
self.onmessage = async (event: MessageEvent<Input>) => {
  const { file, mode } = event.data; let password = event.data.password; event.data.password = '';
  try {
    if (!(file instanceof Blob) || file.size > PORTABLE_LIMITS.protectedBrowserBytes) throw new PortableCryptoError('size-limit', 'Protected browser archives are limited to 128 MiB. Use the CLI for larger archives.');
    const stream = await (mode === 'protect' ? protectArchiveStream : unprotectArchiveStream)(file.stream(), password);
    const bytes = await readBoundedStream(stream, PORTABLE_LIMITS.protectedBrowserBytes);
    self.postMessage({ bytes }, { transfer: [bytes.buffer] });
  } catch (error) {
    self.postMessage({ error: error instanceof PortableCryptoError ? error.message : 'Could not process the archive password. The password may be incorrect, or the archive is corrupt or has been changed.' });
  } finally { password = ''; self.close(); }
};
