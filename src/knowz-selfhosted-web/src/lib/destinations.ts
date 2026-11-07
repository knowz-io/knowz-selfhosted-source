import type { PlatformConnectionDto, VaultSyncStatusDto } from './types'

/**
 * UI_SH_PeerDestination (ADR_G6, #1043) — display helpers for the Destinations UI.
 *
 * A destination is one stored `PlatformConnection`. Two kinds exist in user copy:
 *  - "Knowz Cloud" — the built-in hosts, always allowed by the server;
 *  - "Allowlisted peer" — any other host the operator put in `Sync:AllowedPlatformHosts`.
 *
 * Classification here is for LABELS only. The server (`PlatformUrlValidator`) decides what
 * is allowed; nothing in this module widens or narrows that.
 */

/** Mirrors `PlatformUrlValidator.DefaultAllowlistedHosts` — label classification only. */
const KNOWZ_CLOUD_HOSTS: ReadonlySet<string> = new Set(['api.knowz.io', 'api.dev.knowz.io'])

export const CLOUD_LABEL = 'Knowz Cloud'
export const PEER_KIND_LABEL = 'Allowlisted peer'

export type DestinationKind = 'cloud' | 'peer'

export interface DestinationDescriptor {
  kind: DestinationKind
  /** What the operator sees: display name when set, else "Knowz Cloud" or the peer host. */
  label: string
  /** Kind badge text: "Knowz Cloud" or "Allowlisted peer". */
  kindLabel: string
  /** Lower-cased host of the API URL ("" when unparsable / absent). */
  host: string
}

type UrlAndName = Pick<PlatformConnectionDto, 'platformApiUrl' | 'displayName'>

/** Lower-cased host of a URL; tolerant of a bare host or a trailing path. */
export function hostOf(url: string | null | undefined): string {
  const raw = (url ?? '').trim()
  if (raw.length === 0) return ''
  try {
    return new URL(raw).host.toLowerCase()
  } catch {
    return raw
      .replace(/^[a-z][a-z0-9+.-]*:\/\//i, '')
      .replace(/[/?#].*$/, '')
      .toLowerCase()
  }
}

export function isKnowzCloudUrl(url: string | null | undefined): boolean {
  return KNOWZ_CLOUD_HOSTS.has(hostOf(url))
}

/** Server normalisation (`PlatformConnectionService.NormalizeUrl`) plus case-folding for matching. */
export function normalizeDestinationUrl(url: string | null | undefined): string {
  return (url ?? '').trim().replace(/\/+$/, '').toLowerCase()
}

export function describeDestination(
  connection: UrlAndName | null | undefined,
): DestinationDescriptor {
  if (!connection) {
    return { kind: 'cloud', label: CLOUD_LABEL, kindLabel: CLOUD_LABEL, host: '' }
  }
  const host = hostOf(connection.platformApiUrl)
  const kind: DestinationKind = isKnowzCloudUrl(connection.platformApiUrl) ? 'cloud' : 'peer'
  const kindLabel = kind === 'cloud' ? CLOUD_LABEL : PEER_KIND_LABEL
  const displayName = connection.displayName?.trim() ?? ''
  const label = displayName.length > 0 ? displayName : kind === 'cloud' ? CLOUD_LABEL : host || 'Peer'
  return { kind, label, kindLabel, host }
}

/**
 * The server lists connections oldest-first and the legacy `/connection*` routes address
 * that first row, so "primary" is simply the head of the list.
 */
export function primaryConnection(
  connections: readonly PlatformConnectionDto[],
): PlatformConnectionDto | null {
  return connections[0] ?? null
}

export function isPrimaryConnection(
  connection: PlatformConnectionDto,
  connections: readonly PlatformConnectionDto[],
): boolean {
  const head = connections[0]
  if (!head) return false
  if (connection.id && head.id) return connection.id === head.id
  return head === connection
}

/** True when the link is bound to the given connection (by id, else by normalised URL). */
export function linkBelongsTo(
  link: Pick<VaultSyncStatusDto, 'platformApiUrl' | 'platformConnectionId'>,
  connection: PlatformConnectionDto | null | undefined,
): boolean {
  if (!connection) return false
  if (link.platformConnectionId && connection.id) {
    return link.platformConnectionId === connection.id
  }
  return (
    normalizeDestinationUrl(link.platformApiUrl) ===
    normalizeDestinationUrl(connection.platformApiUrl)
  )
}

/** The stored connection a link uses, or null when none of the known connections matches. */
export function resolveLinkConnection(
  link: Pick<VaultSyncStatusDto, 'platformApiUrl' | 'platformConnectionId'>,
  connections: readonly PlatformConnectionDto[],
): PlatformConnectionDto | null {
  return connections.find((c) => linkBelongsTo(link, c)) ?? null
}

/**
 * Label for the destination a link points at. Falls back to the link's own URL so a
 * legacy row (no connection match) still gets an honest host label.
 */
export function describeLinkDestination(
  link: Pick<VaultSyncStatusDto, 'platformApiUrl' | 'platformConnectionId'>,
  connections: readonly PlatformConnectionDto[],
): DestinationDescriptor {
  const resolved = resolveLinkConnection(link, connections)
  return describeDestination(resolved ?? { platformApiUrl: link.platformApiUrl, displayName: null })
}

export function hasPeerConnections(connections: readonly PlatformConnectionDto[]): boolean {
  return connections.some((c) => describeDestination(c).kind === 'peer')
}
