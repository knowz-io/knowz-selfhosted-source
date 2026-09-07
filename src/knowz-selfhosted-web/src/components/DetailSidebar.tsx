import { Link } from 'react-router-dom'
import { Tag, Info, Paperclip, Calendar, FolderOpen } from 'lucide-react'
import SidebarCard from './SidebarCard'
import { useFormatters } from '../hooks/useFormatters'

interface DetailSidebarProps {
  briefSummary?: string
  tags: string[]
  type: string
  vaults: { id: string; name: string; isPrimary: boolean }[]
  source?: string
  createdAt: string
  updatedAt: string
  isIndexed: boolean
  indexedAt?: string
  attachmentCount: number
}

export default function DetailSidebar({
  tags,
  type,
  vaults,
  source,
  createdAt,
  updatedAt,
  attachmentCount,
}: DetailSidebarProps) {
  const fmt = useFormatters()
  return (
    <div className="space-y-3">
      {/* Vault */}
      <SidebarCard
        title="Vault"
        icon={<FolderOpen size={12} />}
        defaultOpen
      >
        {vaults.length > 0 ? (
          <div className="flex flex-wrap gap-1.5">
            {vaults.map((v) => (
              <Link
                key={v.id}
                to={`/vaults/${v.id}`}
                className="inline-flex items-center gap-1 px-2 py-0.5 text-[11px] font-medium bg-blue-50 dark:bg-blue-900/30 text-blue-700 dark:text-blue-300 rounded-full hover:underline"
              >
                {v.name}
                {v.isPrimary && (
                  <span className="t-meta text-blue-500 dark:text-blue-400">(primary)</span>
                )}
              </Link>
            ))}
          </div>
        ) : (
          <p className="text-[10px] text-muted-foreground">No vault assigned</p>
        )}
      </SidebarCard>

      {/* Tags */}
      <SidebarCard
        title="Tags"
        icon={<Tag size={12} />}
        count={tags.length}
        defaultOpen
      >
        {tags.length > 0 ? (
          <div className="flex flex-wrap gap-1.5">
            {tags.map((tag) => (
              <span
                key={tag}
                className="px-2 py-0.5 text-[10px] font-medium bg-blue-50 dark:bg-blue-900/30 text-blue-700 dark:text-blue-300 rounded-full"
              >
                {tag}
              </span>
            ))}
          </div>
        ) : (
          <p className="text-[10px] text-muted-foreground">No tags</p>
        )}
      </SidebarCard>

      {/* Metadata */}
      <SidebarCard
        title="Metadata"
        icon={<Info size={12} />}
        defaultOpen
      >
        <dl className="space-y-2 text-[11px]">
          <div>
            <dt className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider">Type</dt>
            <dd className="mt-0.5">
              <span className="px-2 py-0.5 bg-muted rounded text-[10px] font-medium">{type}</span>
            </dd>
          </div>

          {source && (
            <div>
              <dt className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider">Source</dt>
              <dd className="mt-0.5 text-muted-foreground truncate" title={source}>{source}</dd>
            </div>
          )}

          <div>
            <dt className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider">Created</dt>
            <dd className="mt-0.5 flex items-center gap-1 text-muted-foreground">
              <Calendar size={10} />
              {fmt.dateTime(createdAt)}
            </dd>
          </div>

          <div>
            <dt className="text-[10px] font-medium text-muted-foreground uppercase tracking-wider">Updated</dt>
            <dd className="mt-0.5 flex items-center gap-1 text-muted-foreground">
              <Calendar size={10} />
              {fmt.dateTime(updatedAt)}
            </dd>
          </div>
        </dl>
      </SidebarCard>

      {/* Attachments count */}
      <SidebarCard
        title="Attachments"
        icon={<Paperclip size={12} />}
        count={attachmentCount}
        defaultOpen={false}
      >
        <p className="text-[10px] text-muted-foreground">
          {attachmentCount > 0
            ? `${attachmentCount} file${attachmentCount === 1 ? '' : 's'} attached. View in the Attachments tab.`
            : 'No files attached.'}
        </p>
      </SidebarCard>
    </div>
  )
}
