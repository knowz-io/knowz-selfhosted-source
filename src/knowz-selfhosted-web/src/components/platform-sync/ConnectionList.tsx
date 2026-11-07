import { useState } from 'react'
import { Plus, ShieldCheck } from 'lucide-react'
import ConnectionCard from './ConnectionCard'
import { isPrimaryConnection, linkBelongsTo } from '../../lib/destinations'
import type { PlatformConnectionDto, VaultSyncStatusDto } from '../../lib/types'

/**
 * UI_SH_PeerDestination (ADR_G6, #1043) — every stored connection for this tenant.
 *
 * The server returns connections oldest-first; the head of the list is the PRIMARY row (the
 * one the legacy `/api/v1/sync/connection*` routes address and the default for new vault
 * links). It is rendered with the `primary` card variant so an instance with zero peer
 * configuration behaves exactly as before. Every other row is an allowlisted peer on the
 * `/connections/{id}` routes.
 */
interface ConnectionListProps {
  connections: PlatformConnectionDto[]
  links: VaultSyncStatusDto[]
  isLoading: boolean
}

export default function ConnectionList({ connections, links, isLoading }: ConnectionListProps) {
  const [isAddingPeer, setIsAddingPeer] = useState(false)

  const linkCountFor = (connection: PlatformConnectionDto): number =>
    links.filter((l) => linkBelongsTo(l, connection)).length

  return (
    <section data-testid="connection-list" className="space-y-4">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-lg font-semibold">Connections</h2>
        <span className="text-xs text-muted-foreground">
          {isLoading
            ? 'Loading…'
            : `${connections.length} configured`}
        </span>
        {!isAddingPeer && (
          <button
            data-testid="add-peer-button"
            onClick={() => setIsAddingPeer(true)}
            className="ml-auto inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors"
          >
            <Plus size={14} />
            Add allowlisted peer
          </button>
        )}
      </div>

      <p
        data-testid="peer-honesty"
        className="flex items-start gap-2 text-xs text-muted-foreground"
      >
        <ShieldCheck size={14} className="mt-0.5 shrink-0" />
        <span>
          Knowz Cloud works out of the box. Any other destination must first be allowlisted by
          the operator of this instance in Sync:AllowedPlatformHosts. Private and LAN peers
          require explicit operator opt-in. Every run is manual — nothing syncs continuously between
          destinations.
        </span>
      </p>

      {isLoading ? (
        <div data-testid="connection-list-loading" className="space-y-3">
          <div className="h-24 bg-muted rounded-xl animate-pulse" />
        </div>
      ) : (
        <>
          {connections.length === 0 && (
            <ConnectionCard connection={null} linkCount={0} variant="primary" />
          )}
          {connections.map((connection, index) => {
            const primary = isPrimaryConnection(connection, connections)
            return (
              <ConnectionCard
                key={connection.id ?? `${index}:${connection.platformApiUrl}`}
                connection={connection}
                linkCount={linkCountFor(connection)}
                variant={primary ? 'primary' : 'peer'}
                isPrimary={primary}
              />
            )
          })}
        </>
      )}

      {isAddingPeer && (
        <ConnectionCard
          connection={null}
          linkCount={0}
          variant="peer"
          onCreated={() => setIsAddingPeer(false)}
          onCancel={() => setIsAddingPeer(false)}
        />
      )}
    </section>
  )
}
