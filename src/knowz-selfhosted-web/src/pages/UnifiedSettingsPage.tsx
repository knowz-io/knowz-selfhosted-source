import { Link, useSearchParams } from 'react-router-dom'
import { Settings, UserCircle, Key, Database, Plug } from 'lucide-react'
import TabContainer from '../components/TabContainer'
import SettingsPage from './SettingsPage'
import AccountPage from './AccountPage'
import ApiKeysPage from './ApiKeysPage'
import DataPortabilityPage from './DataPortabilityPage'
import McpSetupPage from './McpSetupPage'
import PageHeader from '../components/ui/PageHeader'
import { useAuth } from '../lib/auth'
import { UserRole } from '../lib/types'

const tabs = [
  { key: 'connection', label: 'Connection', icon: Settings },
  { key: 'account', label: 'Account', icon: UserCircle },
  { key: 'api-keys', label: 'API Keys', icon: Key },
  { key: 'data', label: 'Data', icon: Database },
  { key: 'mcp', label: 'MCP Setup', icon: Plug },
]

const tabComponents: Record<string, React.ComponentType> = {
  connection: SettingsPage,
  account: AccountPage,
  'api-keys': ApiKeysPage,
  data: DataPortabilityPage,
  mcp: McpSetupPage,
}

export default function UnifiedSettingsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const { user } = useAuth()
  const activeTab = searchParams.get('tab') || 'connection'

  const handleTabChange = (key: string) => {
    setSearchParams(key === 'connection' ? {} : { tab: key }, { replace: true })
  }

  const ActiveComponent = tabComponents[activeTab] ?? SettingsPage

  return (
    <div className="space-y-6">
      <PageHeader
        eyebrow="Control"
        title="Settings and access"
        titleAs="h2"
        description="Tune connection details, account preferences, portability, and MCP access from one place."
      />
      {user?.role === UserRole.SuperAdmin && (
        <section aria-label="Instance configuration" className="rounded-2xl border border-border bg-card p-4 sm:p-5">
          <h3 className="text-base font-semibold">Instance configuration</h3>
          <p className="mt-1 text-sm text-muted-foreground">
            Manage AI providers, application services and restart status for this instance.
          </p>
          <Link to="/admin/settings" className="mt-3 inline-flex rounded-lg border border-input px-3 py-2 text-sm font-medium hover:bg-muted focus-visible:outline focus-visible:outline-2 focus-visible:outline-offset-2">
            Configure this instance
          </Link>
          <p className="mt-3 text-sm text-muted-foreground">
            For host-managed credentials and dependencies, open the local operator GUI with <code>knowz runtime configure</code> on the computer running Knowz.
          </p>
        </section>
      )}
      <TabContainer tabs={tabs} activeTab={activeTab} onTabChange={handleTabChange} />
      <ActiveComponent />
    </div>
  )
}
