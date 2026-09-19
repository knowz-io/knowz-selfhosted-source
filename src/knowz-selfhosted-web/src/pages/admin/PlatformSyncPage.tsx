import { useState } from 'react'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { Cloud, FileArchive } from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import ConnectionCard from '../../components/platform-sync/ConnectionCard'
import VaultLinksTable from '../../components/platform-sync/VaultLinksTable'
import SyncHistoryTable from '../../components/platform-sync/SyncHistoryTable'
import BrowsePlatformModal from '../../components/platform-sync/BrowsePlatformModal'
import SurfaceCard from '../../components/ui/SurfaceCard'

const HISTORY_LIMITS = [25, 50, 100, 500] as const
type HistoryLimit = (typeof HISTORY_LIMITS)[number]

export default function PlatformSyncPage() {
  const [isBrowseOpen, setIsBrowseOpen] = useState(false)
  const [historyLimit, setHistoryLimit] = useState<HistoryLimit>(50)
  const [historyLinkFilter, setHistoryLinkFilter] = useState<string>('')

  const connectionQuery = useQuery({
    queryKey: ['platform-sync', 'connection'],
    queryFn: async () => {
      try {
        return await api.getPlatformConnection()
      } catch (err) {
        if (err instanceof ApiError && err.status === 404) return null
        throw err
      }
    },
  })

  const linksQuery = useQuery({
    queryKey: ['platform-sync', 'links'],
    queryFn: () => api.listSyncLinks(),
  })

  const historyQuery = useQuery({
    queryKey: [
      'platform-sync',
      'history',
      historyLimit,
      historyLinkFilter || null,
    ],
    queryFn: () =>
      api.getPlatformSyncHistory(
        1,
        historyLimit,
        historyLinkFilter || undefined,
      ),
    // Auto-refresh every 5s while any row is still In Progress; stop once terminal.
    refetchInterval: (query) => {
      const data = query.state.data
      if (!data || data.length === 0) return false
      return data.some((r) => r.status === 'InProgress') ? 5000 : false
    },
  })

  const links = linksQuery.data ?? []
  const history = historyQuery.data ?? []

  return (
    <div className="space-y-6">
      <div className="flex items-center gap-3">
        <Cloud size={24} className="text-muted-foreground" />
        <div>
          <h1 className="text-2xl font-bold">Destinations</h1>
          <p className="text-sm text-muted-foreground">
            Connect a destination, link vaults, then run Pull, Push, or Full when you
            choose. Nothing syncs automatically.
          </p>
        </div>
      </div>

      <ConnectionCard
        connection={connectionQuery.data ?? null}
        linkCount={links.length}
      />

      <VaultLinksTable
        links={links}
        isLoading={linksQuery.isLoading}
        onBrowsePlatform={() => setIsBrowseOpen(true)}
      />

      <SyncHistoryTable
        history={history}
        isLoading={historyQuery.isLoading}
        isFetching={historyQuery.isFetching}
        onRefresh={() => historyQuery.refetch()}
        limit={historyLimit}
        limitOptions={HISTORY_LIMITS}
        onLimitChange={(n) => setHistoryLimit(n as HistoryLimit)}
        linkFilter={historyLinkFilter}
        onLinkFilterChange={setHistoryLinkFilter}
        links={links}
      />

      <SurfaceCard className="p-4">
        <div className="flex items-start gap-3">
          <FileArchive size={18} className="mt-0.5 shrink-0 text-muted-foreground" />
          <div>
            <h2 className="text-base font-semibold">File export / import</h2>
            <p className="mt-1 text-sm text-muted-foreground">
              Moving data as files instead of a live connection? Export or import a
              portable package from the data tab.
            </p>
            <Link
              to="/settings?tab=data"
              className="mt-2 inline-block text-sm font-medium text-primary underline"
            >
              Open file export / import
            </Link>
          </div>
        </div>
      </SurfaceCard>

      {isBrowseOpen && (
        <BrowsePlatformModal links={links} onClose={() => setIsBrowseOpen(false)} />
      )}
    </div>
  )
}
