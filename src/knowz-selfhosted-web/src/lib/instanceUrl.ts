/**
 * SH_InstanceCopyHygiene R7 — the single place that knows this instance's
 * actual base URL.
 *
 * The value is copyable (a `copy` handler may hand it to the clipboard) but is
 * never rendered as a literal string: an operator screenshot must not advertise
 * a machine-local hostname. Surfaces render `INSTANCE_URL_PLACEHOLDER` instead,
 * or the configured public base URL when one is set.
 */
export const INSTANCE_URL_PLACEHOLDER = "this instance's URL"

export function getInstanceUrl(): string {
  const configured = localStorage.getItem('apiUrl')
  if (configured && configured.trim().length > 0) return configured.trim()
  return window.location.origin
}

/** What we SHOW: the configured public base URL, else the product phrase. */
export function getDisplayInstanceUrl(): string {
  const configured = localStorage.getItem('apiUrl')
  if (configured && configured.trim().length > 0) return configured.trim()
  return INSTANCE_URL_PLACEHOLDER
}
