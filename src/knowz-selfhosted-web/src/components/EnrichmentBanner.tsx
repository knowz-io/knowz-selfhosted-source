import { Sparkles } from 'lucide-react'

interface EnrichmentBannerProps {
  status: string | null | undefined
}

export default function EnrichmentBanner({ status }: EnrichmentBannerProps) {
  if (status !== 'pending' && status !== 'processing') {
    return null
  }

  return (
    <div
      data-testid="enrichment-banner"
      className="animate-pulse rounded-lg bg-gradient-to-r from-blue-500/10 via-purple-500/10 to-blue-500/10 dark:from-blue-500/20 dark:via-purple-500/20 dark:to-blue-500/20 border border-blue-200/50 dark:border-blue-700/50 px-4 py-3 flex items-center gap-3"
    >
      <Sparkles size={16} className="text-blue-500 dark:text-blue-400 flex-shrink-0" />
      <span className="text-sm font-medium text-blue-700 dark:text-blue-300">
        AI enrichment in progress...
      </span>
    </div>
  )
}
