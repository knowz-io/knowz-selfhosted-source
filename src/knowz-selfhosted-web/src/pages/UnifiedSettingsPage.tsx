import { useSearchParams } from 'react-router-dom'
import { Settings, UserCircle, Key, Database, Plug } from 'lucide-react'
import TabContainer from '../components/TabContainer'
import SettingsPage from './SettingsPage'
import AccountPage from './AccountPage'
import ApiKeysPage from './ApiKeysPage'
import DataPortabilityPage from './DataPortabilityPage'
import McpSetupPage from './McpSetupPage'
import PageHeader from '../components/ui/PageHeader'

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
      <TabContainer tabs={tabs} activeTab={activeTab} onTabChange={handleTabChange} />
      <ActiveComponent />
    </div>
  )
}
