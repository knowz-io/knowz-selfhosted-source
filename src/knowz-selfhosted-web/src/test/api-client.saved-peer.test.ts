import { describe, expect, it, vi } from 'vitest';
const methods = vi.hoisted(() => ({ review: vi.fn().mockResolvedValue({ status: 'not-registered', binding: null }), register: vi.fn().mockResolvedValue({}), transfer: vi.fn().mockResolvedValue({}) }));
vi.mock('../lib/archive-peer-api', () => ({ createArchivePeerTransport: vi.fn(() => methods) }));
import { api } from '../lib/api-client';
describe('Self-Hosted saved-peer public client uses the shared typed protocol', () => {
  it('uses saved link IDs and explicit directions without a legacy full run', async () => {
    const signal = new AbortController().signal; const input = { localSourceBaseUrl: 'https://local.test' }; const binding = {} as never;
    await api.getSavedPeer('link', signal); await api.registerSavedPeer('link', input, signal); await api.transferSavedPeer('link', 'push', binding, signal);
    expect(methods.review).toHaveBeenCalledWith('link', signal); expect(methods.register).toHaveBeenCalledWith('link', input, signal); expect(methods.transfer).toHaveBeenCalledWith('link', 'push', binding, signal);
  });
});
