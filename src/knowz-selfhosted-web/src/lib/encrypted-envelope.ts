/** Heuristic: mothership ciphertext must never render as markdown. */
export function looksLikeEncryptedEnvelope(content: string | null | undefined): boolean {
  if (!content) return false
  const trimmed = content.trim()
  if (trimmed.length === 0) return false
  return (
    trimmed.startsWith('knowz-encrypted-message-v2') ||
    trimmed.includes('"typ":"knowz-encrypted-message-v2"') ||
    (trimmed.startsWith('{') && trimmed.includes('"alg":"A256GCM"') && trimmed.includes('"ciphertext"'))
  )
}
