import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../lib/api-client'
import { Tags, ArrowLeft, BookOpen } from 'lucide-react'
import SurfaceCard from '../components/ui/SurfaceCard'

export default function TopicsPage() {
  const [selectedTopicId, setSelectedTopicId] = useState<string | null>(null)

  const { data, isLoading, error } = useQuery({
    queryKey: ['topics'],
    queryFn: () => api.listTopics(),
  })

  const topicDetail = useQuery({
    queryKey: ['topic', selectedTopicId],
    queryFn: () => api.getTopicDetails(selectedTopicId!),
    enabled: !!selectedTopicId,
  })

  if (selectedTopicId) {
    return (
      <div className="space-y-4">
        <button
          onClick={() => setSelectedTopicId(null)}
          className="inline-flex items-center gap-1 rounded-2xl border border-border/70 bg-card/70 px-3 py-2 text-sm text-muted-foreground transition-colors hover:bg-card hover:text-foreground"
        >
          <ArrowLeft size={16} /> Back to Topics
        </button>

        {topicDetail.isLoading ? (
          <div className="space-y-2">
            <div className="sh-surface h-32 animate-pulse" />
            {Array.from({ length: 3 }).map((_, i) => (
              <div key={i} className="sh-surface h-14 animate-pulse" />
            ))}
          </div>
        ) : topicDetail.error ? (
          <SurfaceCard className="border-red-200/90 bg-red-50/80 p-4 text-sm text-red-700 dark:border-red-900/60 dark:bg-red-950/20 dark:text-red-300">
            {topicDetail.error instanceof Error ? topicDetail.error.message : 'Failed to load topic'}
          </SurfaceCard>
        ) : topicDetail.data ? (
          <>
            <SurfaceCard className="p-5">
              <p className="sh-kicker">Topic</p>
              <h3 className="mt-2 text-xl font-semibold tracking-tight">{topicDetail.data.name}</h3>
              {topicDetail.data.description && (
                <p className="mt-2 text-sm leading-6 text-muted-foreground">{topicDetail.data.description}</p>
              )}
            </SurfaceCard>
            <div className="space-y-2">
              {topicDetail.data.knowledgeItems.map((item) => (
                <Link
                  key={item.id}
                  to={`/knowledge/${item.id}`}
                  className="sh-surface flex items-center gap-3 p-4 transition-all duration-200 hover:-translate-y-0.5 hover:bg-card"
                >
                  <BookOpen size={16} className="text-muted-foreground flex-shrink-0" />
                  <div className="min-w-0 flex-1">
                    <p className="font-medium truncate">{item.title}</p>
                    {item.summary && (
                      <p className="text-sm text-muted-foreground truncate">
                        {item.summary}
                      </p>
                    )}
                  </div>
                  <span className="px-2 py-0.5 text-xs bg-muted rounded flex-shrink-0">
                    {item.type}
                  </span>
                </Link>
              ))}
              {topicDetail.data.knowledgeItems.length === 0 && (
                <SurfaceCard className="p-10 text-center">
                  <p className="text-sm text-muted-foreground">No knowledge items in this topic.</p>
                </SurfaceCard>
              )}
            </div>
          </>
        ) : null}
      </div>
    )
  }

  return (
    <div className="space-y-4">
      {error && (
        <SurfaceCard className="border-red-200/90 bg-red-50/80 p-4 text-sm text-red-700 dark:border-red-900/60 dark:bg-red-950/20 dark:text-red-300">
          {error instanceof Error ? error.message : 'Failed to load topics'}
        </SurfaceCard>
      )}

      {isLoading ? (
        <div className="space-y-2">
          {Array.from({ length: 5 }).map((_, i) => (
            <div key={i} className="sh-surface h-16 animate-pulse" />
          ))}
        </div>
      ) : (
        <>
          <div className="space-y-2">
            {data?.topics.map((topic) => (
              <button
                key={topic.id}
                onClick={() => setSelectedTopicId(topic.id)}
                className="sh-surface w-full text-left flex items-center gap-3 p-4 transition-all duration-200 hover:-translate-y-0.5 hover:bg-card"
              >
                <Tags size={16} className="text-muted-foreground flex-shrink-0" />
                <div className="min-w-0 flex-1">
                  <p className="font-medium">{topic.name}</p>
                  {topic.description && (
                    <p className="text-sm text-muted-foreground truncate">
                      {topic.description}
                    </p>
                  )}
                </div>
                <span className="text-sm text-muted-foreground flex-shrink-0">
                  {topic.knowledgeCount} items
                </span>
              </button>
            ))}
            {data?.topics.length === 0 && (
              <SurfaceCard className="p-10 text-center">
                <p className="text-sm text-muted-foreground">No topics found.</p>
              </SurfaceCard>
            )}
          </div>
        </>
      )}
    </div>
  )
}
