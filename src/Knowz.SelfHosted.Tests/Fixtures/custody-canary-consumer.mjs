// Local synthetic fixture only; no private key leaves this process or is printed.
import { createInterface } from 'node:readline';
import { decryptEnvelopeToBytes } from '../../../../cli/packages/crypto/dist/portable/vault-crypto.js';
const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
for await (const line of lines) {
  const input = JSON.parse(line); const response = input.response;
  const key = Uint8Array.from(Buffer.from(input.privateKey, 'base64')); let plain; let secret; let proof;
  try {
    const e = response.envelope;
    plain = await decryptEnvelopeToBytes(key, { ciphertext: e.ciphertext, contentNonce: e.contentNonce, encryptedDek: e.wrappedDek, dekNonce: e.dekNonce, ephemeralPublicKey: e.ephemeralPublicKey }, new TextEncoder().encode(`knowz-msg-v2|${response.challengeId}|x25519-vault-public|2`));
    const payload = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(plain)); const binding = Uint8Array.from(Buffer.from(response.bindingRawBase64, 'base64'));
    const digest = Buffer.from(await crypto.subtle.digest('SHA-256', binding)).toString('hex'); if (digest !== payload.bindingSha256 || payload.formatVersion !== 1) throw new Error('Canary binding mismatch');
    secret = Uint8Array.from(Buffer.from(payload.secret, 'base64')); payload.secret = ''; input.privateKey = '';
    const hmac = await crypto.subtle.importKey('raw', secret, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
    proof = new Uint8Array(await crypto.subtle.sign('HMAC', hmac, binding)); process.stdout.write(`${Buffer.from(proof).toString('base64')}\n`);
  } finally { key.fill(0); plain?.fill(0); secret?.fill(0); proof?.fill(0); }
}
