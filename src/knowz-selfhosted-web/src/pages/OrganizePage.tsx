import { useSearchParams } from 'react-router-dom'
import { Tag, Tags, Users } from 'lucide-react'
import TabContainer from '../components/TabContainer'
import TagsPage from './TagsPage'
import TopicsPage from './TopicsPage'
import EntitiesPage from './EntitiesPage'

const tabs = [
  { key: 'tags', label: 'Tags', icon: Tag },
  { key: 'topics', label: 'Topics', icon: Tags },
  { key: 'entities', label: 'Entities', icon: Users },
]

export default function OrganizePage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const requestedTab = searchParams.get('tab') || 'tags'
  const activeTab = tabs.some((tab) => tab.key === requestedTab) ? requestedTab : 'tags'

  const handleTabChange = (key: string) => {
    setSearchParams(key === 'tags' ? {} : { tab: key }, { replace: true })
  }

  return (
    <div className="space-y-4">
      <TabContainer tabs={tabs} activeTab={activeTab} onTabChange={handleTabChange} />

      {activeTab === 'tags' && <TagsPage />}
      {activeTab === 'topics' && <TopicsPage />}
      {activeTab === 'entities' && <EntitiesPage />}
    </div>
  )
}
