import { useState, useRef, useEffect } from 'react'
import { toUserError } from '../lib/toUserError'
import { Link } from 'react-router-dom'
import { api } from '../lib/api-client'
import type { AskResponse } from '../lib/types'
import { Send, Square } from 'lucide-react'
import AiUnavailableNotice from '../components/AiUnavailableNotice'

export default function AskPage() {
  const [question, setQuestion] = useState('')
  const [result, setResult] = useState<AskResponse | null>(null)
  const [isStreaming, setIsStreaming] = useState(false)
  const [streamingAnswer, setStreamingAnswer] = useState('')
  const [streamingSources, setStreamingSources] = useState<{ knowledgeId: string }[]>([])
  const [streamingConfidence, setStreamingConfidence] = useState(0)
  const [streamError, setStreamError] = useState<string | null>(null)
  // SH_OfflineAiHonesty R5: rendered beside the form, never as an answer card.
  const [aiUnavailable, setAiUnavailable] = useState(false)
  const abortControllerRef = useRef<AbortController | null>(null)
  const lastEscapeRef = useRef<number>(0)

  // Double-Escape handler to abort streaming
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        const now = Date.now()
        if (now - lastEscapeRef.current < 300) {
          abortControllerRef.current?.abort()
          lastEscapeRef.current = 0
        } else {
          lastEscapeRef.current = now
        }
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!question.trim() || isStreaming) return

    const controller = new AbortController()
    abortControllerRef.current = controller
    setIsStreaming(true)
    setStreamError(null)
    setResult(null)
    setStreamingAnswer('')
    setStreamingSources([])
    setStreamingConfidence(0)
    setAiUnavailable(false)

    let answer = ''
    let sources: { knowledgeId: string }[] = []
    let confidence = 0

    try {
      await api.askStream(
        { question: question.trim() },
        (event) => {
          switch (event.type) {
            case 'token':
              answer += event.content
              setStreamingAnswer(answer)
              break
            case 'sources':
              sources = event.sources
              confidence = event.confidence
              setStreamingSources(sources)
              setStreamingConfidence(confidence)
              setAiUnavailable(event.aiUnavailable === true)
              break
            case 'done':
              break
            case 'error':
              setStreamError(event.message)
              break
          }
        },
        controller.signal,
      )
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') {
        // User cancelled - keep partial content
      } else if (err instanceof Error) {
        setStreamError(toUserError(err))
      }
    } finally {
      // Finalize result
      if (answer) {
        setResult({
          answer,
          sources,
          confidence,
        })
      }
      setStreamingAnswer('')
      setIsStreaming(false)
      abortControllerRef.current = null
    }
  }

  // Show streaming content or final result
  const displayAnswer = isStreaming ? streamingAnswer : result?.answer
  const displaySources = isStreaming ? streamingSources : result?.sources
  const displayConfidence = isStreaming ? streamingConfidence : result?.confidence

  return (
    <div className="space-y-4 max-w-3xl">
      <form onSubmit={handleSubmit} className="space-y-3">
        <textarea
          value={question}
          onChange={(e) => setQuestion(e.target.value)}
          placeholder="Ask a question about your knowledge..."
          rows={3}
          className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm"
        />
        <div className="flex items-center gap-2">
          <button
            type={isStreaming ? 'button' : 'submit'}
            onClick={isStreaming ? () => abortControllerRef.current?.abort() : undefined}
            disabled={!isStreaming && !question.trim()}
            className={`inline-flex items-center gap-2 px-5 py-2 rounded-md text-sm font-medium transition-opacity ${
              isStreaming
                ? 'bg-red-600 text-white hover:bg-red-700'
                : 'bg-primary text-primary-foreground disabled:opacity-50'
            }`}
          >
            {isStreaming ? <Square size={16} /> : <Send size={16} />}
            {isStreaming ? 'Stop' : 'Ask'}
          </button>
          {isStreaming && (
            <span className="text-[10px] text-muted-foreground">
              Press Esc Esc to stop
            </span>
          )}
        </div>
      </form>

      {aiUnavailable && <AiUnavailableNotice />}

      {streamError && (
        <p className="text-red-600 dark:text-red-400 text-sm">
          {streamError}
        </p>
      )}

      {displayAnswer && !aiUnavailable && (
        <div className="space-y-4 pt-2">
          <div className="bg-card border border-border/60 rounded-xl p-5">
            <div className="flex items-center justify-between mb-3">
              <h2 className="text-sm font-medium text-muted-foreground">Answer</h2>
              {displayConfidence != null && displayConfidence > 0 && (
                <span className="text-xs px-2 py-0.5 bg-green-50 dark:bg-green-900/30 text-green-700 dark:text-green-300 rounded">
                  {Math.round(displayConfidence * 100)}% confidence
                </span>
              )}
            </div>
            <div className="whitespace-pre-wrap text-sm">{displayAnswer}</div>
          </div>

          {displaySources && displaySources.length > 0 && (
            <div>
              <h3 className="text-sm font-medium text-muted-foreground mb-2">
                Sources ({displaySources.length})
              </h3>
              <div className="flex flex-wrap gap-2">
                {displaySources.map((s) => (
                  <Link
                    key={s.knowledgeId}
                    to={`/knowledge/${s.knowledgeId}`}
                    className="text-sm text-blue-600 dark:text-blue-400 hover:underline"
                  >
                    {s.knowledgeId.slice(0, 8)}...
                  </Link>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  )
}
