import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Cloud } from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import ConnectionCard from '../../components/platform-sync/ConnectionCard'
import VaultLinksTable from '../../components/platform-sync/VaultLinksTable'
import SyncHistoryTable from '../../components/platform-sync/SyncHistoryTable'
import BrowsePlatformModal from '../../components/platform-sync/BrowsePlatformModal'

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
          <h1 className="text-2xl font-bold">Connect to Knowz Cloud</h1>
          <p className="text-sm text-muted-foreground">
            Connect a Knowz Cloud API key and sync vaults with your Knowz Cloud account.
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

      {isBrowseOpen && (
        <BrowsePlatformModal links={links} onClose={() => setIsBrowseOpen(false)} />
      )}
    </div>
  )
}
