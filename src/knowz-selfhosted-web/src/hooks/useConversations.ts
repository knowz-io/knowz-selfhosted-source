import { useState, useCallback, useEffect } from 'react'
import type { ChatConversation, ChatMessage } from '../lib/types'

const STORAGE_KEY = 'knowz-conversations'
const MAX_CONVERSATIONS = 50

function loadConversations(): ChatConversation[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY)
    if (!raw) return []
    const parsed = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed
  } catch {
    return []
  }
}

function saveConversations(conversations: ChatConversation[]): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(conversations))
}

export function useConversations() {
  const [conversations, setConversations] = useState<ChatConversation[]>(() =>
    loadConversations(),
  )
  const [activeConversationId, setActiveConversationId] = useState<string | null>(null)

  // Sync state to localStorage on every mutation
  useEffect(() => {
    saveConversations(conversations)
  }, [conversations])

  const activeConversation =
    conversations.find((c) => c.id === activeConversationId) ?? null

  const createConversation = useCallback(
    (vaultId?: string): ChatConversation => {
      const now = new Date().toISOString()
      const newConv: ChatConversation = {
        id: crypto.randomUUID(),
        title: 'New Chat',
        vaultId,
        messages: [],
        createdAt: now,
        updatedAt: now,
      }

      setConversations((prev) => {
        // Prepend new conversation, prune if over limit
        const updated = [newConv, ...prev]
        if (updated.length > MAX_CONVERSATIONS) {
          return updated.slice(0, MAX_CONVERSATIONS)
        }
        return updated
      })
      setActiveConversationId(newConv.id)
      return newConv
    },
    [],
  )

  const selectConversation = useCallback((id: string) => {
    setActiveConversationId(id)
  }, [])

  const addMessage = useCallback(
    (conversationId: string, message: ChatMessage) => {
      setConversations((prev) =>
        prev.map((c) => {
          if (c.id !== conversationId) return c
          const updated = {
            ...c,
            messages: [...c.messages, message],
            updatedAt: new Date().toISOString(),
          }
          // Auto-title from first user message
          if (
            message.role === 'user' &&
            c.messages.length === 0
          ) {
            updated.title =
              message.content.length > 40
                ? message.content.slice(0, 40) + '...'
                : message.content
          }
          return updated
        }),
      )
    },
    [],
  )

  const deleteConversation = useCallback(
    (id: string) => {
      setConversations((prev) => prev.filter((c) => c.id !== id))
      if (activeConversationId === id) {
        setActiveConversationId(null)
      }
    },
    [activeConversationId],
  )

  const renameConversation = useCallback(
    (id: string, title: string) => {
      setConversations((prev) =>
        prev.map((c) =>
          c.id === id
            ? { ...c, title, updatedAt: new Date().toISOString() }
            : c,
        ),
      )
    },
    [],
  )

  return {
    conversations,
    activeConversation,
    createConversation,
    selectConversation,
    addMessage,
    deleteConversation,
    renameConversation,
  }
}
