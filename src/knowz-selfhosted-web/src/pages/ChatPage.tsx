import { useState, useRef, useEffect } from 'react'
import { toUserError } from '../lib/toUserError'
import { Link } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from '../lib/api-client'
import { useConversations } from '../hooks/useConversations'
import MarkdownContent from '../components/MarkdownContent'
import AiUnavailableNotice from '../components/AiUnavailableNotice'
import type { ChatMessage, ChatRequestData } from '../lib/types'
import {
  Plus,
  Send,
  Trash2,
  MessagesSquare,
  ChevronLeft,
  ChevronRight,
  FlaskConical,
  Square,
  CheckSquare,
  X,
  Loader2,
} from 'lucide-react'

export default function ChatPage() {
  const {
    conversations,
    activeConversation,
    createConversation,
    selectConversation,
    addMessage,
    deleteConversation,
  } = useConversations()

  const [input, setInput] = useState('')
  const [selectedVaultId, setSelectedVaultId] = useState<string>('')
  const [researchMode, setResearchMode] = useState(false)
  const [sidebarOpen, setSidebarOpen] = useState(true)
  const [isStreaming, setIsStreaming] = useState(false)
  const [streamingMessage, setStreamingMessage] = useState<ChatMessage | null>(null)
  const [streamError, setStreamError] = useState<string | null>(null)
  // SH_OfflineAiHonesty R5: out-of-transcript signal, never a chat message.
  const [aiUnavailable, setAiUnavailable] = useState(false)
  const messagesEndRef = useRef<HTMLDivElement>(null)
  const textareaRef = useRef<HTMLTextAreaElement>(null)
  const abortControllerRef = useRef<AbortController | null>(null)
  const lastEscapeRef = useRef<number>(0)

  // Multi-select state
  const [selectMode, setSelectMode] = useState(false)
  const [selectedConvIds, setSelectedConvIds] = useState<Set<string>>(new Set())
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false)
  const [isDeleting, setIsDeleting] = useState(false)

  const toggleSelectConv = (id: string) => {
    setSelectedConvIds((prev) => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }

  const toggleSelectAllConvs = () => {
    if (selectedConvIds.size === conversations.length) {
      setSelectedConvIds(new Set())
    } else {
      setSelectedConvIds(new Set(conversations.map((c) => c.id)))
    }
  }

  const exitSelectMode = () => {
    setSelectMode(false)
    setSelectedConvIds(new Set())
  }

  const handleBulkDelete = () => {
    setIsDeleting(true)
    for (const id of selectedConvIds) {
      deleteConversation(id)
    }
    setSelectedConvIds(new Set())
    setShowDeleteConfirm(false)
    setSelectMode(false)
    setIsDeleting(false)
  }

  const { data: vaultsData } = useQuery({
    queryKey: ['vaults'],
    queryFn: () => api.listVaults(false),
    staleTime: 60_000,
  })

  // Scroll to bottom when messages change or streaming updates
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [activeConversation?.messages.length, streamingMessage?.content])

  // Sync vault selector when switching conversations
  useEffect(() => {
    if (activeConversation?.vaultId) {
      setSelectedVaultId(activeConversation.vaultId)
    }
  }, [activeConversation?.id, activeConversation?.vaultId])

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

  const handleSend = async () => {
    const trimmed = input.trim()
    if (!trimmed || isStreaming) return

    let conv = activeConversation
    if (!conv) {
      conv = createConversation(selectedVaultId || undefined)
    }

    const userMsg: ChatMessage = {
      id: crypto.randomUUID(),
      role: 'user',
      content: trimmed,
      timestamp: new Date().toISOString(),
    }
    addMessage(conv.id, userMsg)
    setInput('')

    // Build conversation history from existing messages (before the just-sent message)
    const history = conv.messages.map((m) => ({
      role: m.role,
      content: m.content,
    }))

    const requestData: ChatRequestData = {
      question: trimmed,
      conversationHistory: history.length > 0 ? history : undefined,
      vaultId: selectedVaultId || undefined,
      researchMode,
    }

    const controller = new AbortController()
    abortControllerRef.current = controller
    setIsStreaming(true)
    setStreamError(null)

    let content = ''
    let sources: { knowledgeId: string }[] = []
    let confidence = 0
    let unavailable = false

    // SH_OfflineAiHonesty R5 (GAP-4): the assistant bubble is created only once
    // the first token arrives. On the AI-unavailable path no token ever arrives,
    // so no bubble — not even a transient empty one — is ever rendered.

    try {
      await api.chatStream(
        requestData,
        (event) => {
          switch (event.type) {
            case 'token':
              content += event.content
              setStreamingMessage((prev) =>
                prev
                  ? { ...prev, content }
                  : {
                      id: crypto.randomUUID(),
                      role: 'assistant',
                      content,
                      timestamp: new Date().toISOString(),
                    },
              )
              break
            case 'sources':
              sources = event.sources
              confidence = event.confidence
              unavailable = event.aiUnavailable === true
              setAiUnavailable(unavailable)
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
      // Add the final message to conversation.
      // SH_OfflineAiHonesty R5: when AI is unavailable there is no content, so
      // nothing is ever appended to history — the notice lives outside it.
      if (content && !unavailable) {
        const assistantMsg: ChatMessage = {
          id: crypto.randomUUID(),
          role: 'assistant',
          content,
          sources: sources.length > 0 ? sources : undefined,
          confidence: confidence > 0 ? confidence : undefined,
          timestamp: new Date().toISOString(),
        }
        addMessage(conv!.id, assistantMsg)
      }
      setStreamingMessage(null)
      setIsStreaming(false)
      abortControllerRef.current = null
    }
  }

  const handleKeyDown = (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === 'Enter' && !e.shiftKey) {
      e.preventDefault()
      handleSend()
    }
  }

  const handleNewChat = () => {
    createConversation(selectedVaultId || undefined)
    setInput('')
    setStreamError(null)
  }

  const formatTime = (timestamp: string) => {
    const d = new Date(timestamp)
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  }

  const formatDate = (timestamp: string) => {
    const d = new Date(timestamp)
    const now = new Date()
    if (d.toDateString() === now.toDateString()) return 'Today'
    const yesterday = new Date(now)
    yesterday.setDate(yesterday.getDate() - 1)
    if (d.toDateString() === yesterday.toDateString()) return 'Yesterday'
    return d.toLocaleDateString()
  }

  const renderAssistantBubble = (msg: ChatMessage) => (
    <div className="flex justify-start" key={msg.id}>
      <div className="max-w-[80%] rounded-2xl px-4 py-3 bg-card border border-border/40 shadow-card">
        <MarkdownContent content={msg.content} compact className="text-sm" />

        {msg.role === 'assistant' && (
          <div className="mt-2 flex items-center gap-2 flex-wrap">
            {msg.confidence != null && (
              <span className="text-[10px] px-1.5 py-0.5 bg-green-50 dark:bg-green-900/30 text-green-700 dark:text-green-300 rounded">
                {Math.round(msg.confidence * 100)}% confidence
              </span>
            )}
            {msg.sources && msg.sources.length > 0 && (
              <div className="flex items-center gap-1 flex-wrap">
                <span className="text-[10px] text-muted-foreground">
                  Sources:
                </span>
                {msg.sources.map((s, idx) => {
                  const id = s.knowledgeId ?? (s as any).KnowledgeId ?? ''
                  return (
                    <Link
                      key={id || idx}
                      to={`/knowledge/${id}`}
                      className="text-[10px] text-primary hover:underline"
                    >
                      {id.slice(0, 8)}
                    </Link>
                  )
                })}
              </div>
            )}
          </div>
        )}

        <p className="text-[10px] mt-1 opacity-50">
          {formatTime(msg.timestamp)}
        </p>
      </div>
    </div>
  )

  return (
    <div className="sh-surface relative flex min-h-[calc(100vh-12rem)] overflow-hidden">
      {/* Conversation Sidebar */}
      <div
        className={`${
          sidebarOpen ? 'w-64' : 'w-0'
        } flex flex-col overflow-hidden border-r border-white/10 bg-sidebar text-sidebar-foreground transition-all duration-200`}
      >
        <div className="space-y-2 border-b border-white/10 p-4">
          <button
            onClick={handleNewChat}
            className="flex w-full items-center justify-center gap-2 rounded-2xl bg-sidebar-accent px-3 py-3 text-sm font-semibold text-slate-950 shadow-sm shadow-black/20 transition-all duration-200 hover:-translate-y-0.5"
          >
            <Plus size={16} />
            New Chat
          </button>
          {conversations.length > 0 && (
            <button
              onClick={() => (selectMode ? exitSelectMode() : setSelectMode(true))}
              className={`flex w-full items-center justify-center gap-1.5 rounded-2xl px-3 py-2 text-xs font-medium transition-colors duration-150 ${
                selectMode
                  ? 'bg-white/12 text-sidebar-foreground ring-1 ring-white/10'
                  : 'text-sidebar-foreground/72 hover:bg-white/6 hover:text-sidebar-foreground'
              }`}
            >
              <CheckSquare size={12} />
              {selectMode ? 'Cancel Select' : 'Select'}
            </button>
          )}
        </div>

        {selectMode && conversations.length > 0 && (
          <div className="flex items-center gap-2 border-b border-white/10 px-4 py-3">
            <input
              type="checkbox"
              checked={selectedConvIds.size === conversations.length && conversations.length > 0}
              onChange={toggleSelectAllConvs}
              className="rounded border-input"
            />
            <span className="text-xs text-sidebar-foreground/68">
              {selectedConvIds.size > 0
                ? `${selectedConvIds.size} selected`
                : 'Select all'}
            </span>
          </div>
        )}

        <div className="flex-1 space-y-1 overflow-y-auto px-3 py-3">
          {conversations.length === 0 && (
            <p className="py-8 text-center text-xs text-sidebar-foreground/60">
              No conversations yet
            </p>
          )}
          {conversations.map((conv) => (
            <div
              key={conv.id}
              className={`group flex items-center gap-2 px-3 py-2.5 rounded-xl cursor-pointer text-sm transition-all duration-200 ${
                activeConversation?.id === conv.id && !selectMode
                  ? 'bg-white/12 text-sidebar-foreground shadow-sm ring-1 ring-white/10'
                  : selectedConvIds.has(conv.id)
                    ? 'bg-white/8 text-sidebar-foreground'
                    : 'text-sidebar-foreground/72 hover:bg-white/6 hover:text-sidebar-foreground'
              }`}
              onClick={() => (selectMode ? toggleSelectConv(conv.id) : selectConversation(conv.id))}
            >
              {selectMode ? (
                <input
                  type="checkbox"
                  checked={selectedConvIds.has(conv.id)}
                  onChange={() => toggleSelectConv(conv.id)}
                  onClick={(e) => e.stopPropagation()}
                  className="rounded border-input flex-shrink-0"
                />
              ) : (
                <MessagesSquare size={14} className="flex-shrink-0" />
              )}
              <div className="flex-1 min-w-0">
                <p className="truncate">{conv.title}</p>
                <p className="text-[10px] text-sidebar-foreground/52">
                  {formatDate(conv.updatedAt)}
                </p>
              </div>
              {!selectMode && (
                <button
                  onClick={(e) => {
                    e.stopPropagation()
                    deleteConversation(conv.id)
                  }}
                  className="opacity-0 group-hover:opacity-100 p-1 rounded hover:bg-muted transition-opacity"
                  title="Delete conversation"
                >
                  <Trash2 size={12} />
                </button>
              )}
            </div>
          ))}
        </div>
      </div>

      {/* Toggle sidebar button */}
      <button
        onClick={() => setSidebarOpen(!sidebarOpen)}
        className="absolute left-0 top-1/2 z-10 rounded-r-2xl border border-border/60 bg-card/90 p-2 text-muted-foreground shadow-sm transition-colors hover:bg-card"
        style={{ left: sidebarOpen ? '16rem' : '0' }}
      >
        {sidebarOpen ? <ChevronLeft size={14} /> : <ChevronRight size={14} />}
      </button>

      {/* Main Chat Area */}
      <div className="flex-1 flex flex-col min-w-0">
        {/* Top bar: vault selector + research mode */}
        <div className="flex items-center gap-3 border-b border-border bg-card px-5 py-4">
          <select
            value={selectedVaultId}
            onChange={(e) => setSelectedVaultId(e.target.value)}
            className="rounded-2xl border border-input bg-card/90 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-ring/20"
          >
            <option value="">All Vaults</option>
            {vaultsData?.vaults.map((v) => (
              <option key={v.id} value={v.id}>
                {v.name}
              </option>
            ))}
          </select>

          <label className="flex items-center gap-1.5 text-sm cursor-pointer">
            <button
              onClick={() => setResearchMode(!researchMode)}
              disabled={aiUnavailable}
              title={aiUnavailable ? 'Research needs an AI provider, which is not configured on this instance.' : undefined}
              className={`flex items-center gap-1 rounded-2xl px-3 py-2 text-xs font-medium transition-colors duration-150 disabled:cursor-not-allowed disabled:opacity-50 ${
                researchMode
                  ? 'bg-purple-100 dark:bg-purple-900/30 text-purple-700 dark:text-purple-300'
                  : 'bg-card text-muted-foreground hover:bg-accent'
              }`}
            >
              <FlaskConical size={12} />
              Research
            </button>
          </label>
        </div>

        {/* Messages area */}
        <div className="flex-1 space-y-4 overflow-y-auto bg-[radial-gradient(circle_at_top,rgba(59,130,246,0.06),transparent_30%)] px-5 py-5">
          {!activeConversation || activeConversation.messages.length === 0 ? (
            <div className="flex flex-col items-center justify-center h-full text-center animate-fade-in">
              <div className="sh-surface mb-5 p-4">
                <MessagesSquare
                  size={40}
                  className="text-muted-foreground/40"
                />
              </div>
              <h2 className="text-lg font-semibold text-foreground/80 mb-2">
                Start a new conversation
              </h2>
              <p className="text-sm text-muted-foreground max-w-md leading-relaxed">
                Ask questions about your knowledge base. Conversation history
                provides context for follow-up questions.
              </p>
            </div>
          ) : (
            activeConversation.messages.map((msg) => (
              <div
                key={msg.id}
                className={`flex ${
                  msg.role === 'user' ? 'justify-end' : 'justify-start'
                }`}
              >
                <div
                  className={`max-w-[80%] rounded-2xl px-4 py-3 ${
                    msg.role === 'user'
                      ? 'bg-primary text-primary-foreground shadow-sm shadow-primary/20'
                      : 'bg-card border border-border/40 shadow-card'
                  }`}
                >
                  {msg.role === 'assistant' ? (
                    <MarkdownContent content={msg.content} compact className="text-sm" />
                  ) : (
                    <div className="whitespace-pre-wrap text-sm">{msg.content}</div>
                  )}

                  {msg.role === 'assistant' && (
                    <div className="mt-2 flex items-center gap-2 flex-wrap">
                      {msg.confidence != null && (
                        <span className="text-[10px] px-1.5 py-0.5 bg-green-50 dark:bg-green-900/30 text-green-700 dark:text-green-300 rounded">
                          {Math.round(msg.confidence * 100)}% confidence
                        </span>
                      )}
                      {msg.sources && msg.sources.length > 0 && (
                        <div className="flex items-center gap-1 flex-wrap">
                          <span className="text-[10px] text-muted-foreground">
                            Sources:
                          </span>
                          {msg.sources.map((s, idx) => {
                            const id = s.knowledgeId ?? (s as any).KnowledgeId ?? ''
                            return (
                              <Link
                                key={id || idx}
                                to={`/knowledge/${id}`}
                                className="text-[10px] text-primary hover:underline"
                              >
                                {id.slice(0, 8)}
                              </Link>
                            )
                          })}
                        </div>
                      )}
                    </div>
                  )}

                  <p className="text-[10px] mt-1 opacity-50">
                    {formatTime(msg.timestamp)}
                  </p>
                </div>
              </div>
            ))
          )}

          {streamingMessage && renderAssistantBubble(streamingMessage)}

          {aiUnavailable && <AiUnavailableNotice />}

          {streamError && (
            <div className="flex justify-start">
              <div className="bg-red-50 dark:bg-red-900/20 text-red-600 dark:text-red-400 rounded-xl px-4 py-3 text-sm">
                {streamError}
              </div>
            </div>
          )}

          <div ref={messagesEndRef} />
        </div>

        {/* Input area */}
        <div className="border-t border-border bg-card px-5 py-4">
          <div className="flex items-end gap-2 max-w-3xl mx-auto">
            <textarea
              ref={textareaRef}
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={handleKeyDown}
              placeholder="Ask a question... (Shift+Enter for newline)"
              rows={1}
              className="max-h-32 flex-1 resize-none rounded-2xl border border-input bg-card/90 px-4 py-3 text-sm shadow-sm transition-all duration-200 focus:border-primary/30 focus:outline-none focus:ring-2 focus:ring-ring/30"
              style={{
                height: 'auto',
                minHeight: '2.5rem',
              }}
              onInput={(e) => {
                const target = e.target as HTMLTextAreaElement
                target.style.height = 'auto'
                target.style.height = Math.min(target.scrollHeight, 128) + 'px'
              }}
            />
            <button
              onClick={isStreaming ? () => abortControllerRef.current?.abort() : handleSend}
              disabled={!isStreaming && !input.trim()}
              className={`inline-flex items-center justify-center px-3.5 py-2.5 rounded-xl text-sm font-medium transition-all duration-200 active:scale-95 ${
                isStreaming
                  ? 'bg-red-600 text-white hover:bg-red-700 shadow-sm shadow-red-600/20'
                  : 'bg-primary text-primary-foreground disabled:opacity-40 hover:brightness-110 shadow-sm shadow-primary/20'
              }`}
              title={isStreaming ? 'Stop generating' : 'Send message'}
            >
              {isStreaming ? <Square size={16} /> : <Send size={16} />}
            </button>
          </div>
          {isStreaming && (
            <p className="text-[10px] text-muted-foreground text-center mt-1">
              Press Esc Esc to stop
            </p>
          )}
        </div>
      </div>

      {/* Floating action bar for bulk conversation operations */}
      {selectMode && selectedConvIds.size > 0 && (
        <div className="fixed bottom-6 left-1/2 -translate-x-1/2 z-40 animate-in slide-in-from-bottom-4 duration-200">
          <div className="flex items-center gap-3 px-5 py-3 bg-foreground text-background rounded-lg shadow-xl">
            <span className="text-sm font-medium whitespace-nowrap">
              {selectedConvIds.size} conversation{selectedConvIds.size !== 1 ? 's' : ''} selected
            </span>
            <div className="w-px h-5 bg-border" />
            <button
              onClick={() => setShowDeleteConfirm(true)}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 text-sm bg-red-600 hover:bg-red-700 text-white rounded transition-colors"
            >
              <Trash2 size={14} /> Delete
            </button>
            <button
              onClick={exitSelectMode}
              className="inline-flex items-center gap-1 px-2 py-1.5 text-sm hover:bg-primary/70 rounded transition-colors"
              title="Deselect all"
            >
              <X size={14} />
            </button>
          </div>
        </div>
      )}

      {/* Bulk delete confirmation modal */}
      {showDeleteConfirm && (
        <div className="fixed inset-0 bg-black/50 z-50 flex items-center justify-center p-4">
          <div className="bg-card rounded-xl p-6 max-w-sm w-full space-y-4 shadow-sm">
            <h2 className="text-lg font-semibold">
              Delete {selectedConvIds.size} conversation{selectedConvIds.size !== 1 ? 's' : ''}?
            </h2>
            <p className="text-sm text-muted-foreground">
              Are you sure you want to delete {selectedConvIds.size} conversation{selectedConvIds.size !== 1 ? 's' : ''}?
              This will remove all messages in these conversations. This action cannot be undone.
            </p>
            <div className="flex gap-2 justify-end">
              <button
                onClick={() => setShowDeleteConfirm(false)}
                disabled={isDeleting}
                className="px-4 py-2 border border-input rounded-md text-sm transition-colors"
              >
                Cancel
              </button>
              <button
                onClick={handleBulkDelete}
                disabled={isDeleting}
                className="inline-flex items-center gap-2 px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium disabled:opacity-50"
              >
                {isDeleting ? (
                  <>
                    <Loader2 size={14} className="animate-spin" /> Deleting...
                  </>
                ) : (
                  'Delete'
                )}
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
