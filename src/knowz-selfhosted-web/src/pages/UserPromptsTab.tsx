import { useState, useEffect } from 'react'
import { Save, Trash2, CheckCircle2, AlertCircle } from 'lucide-react'
import { api, ApiError } from '../lib/api-client'
import type { PromptTemplateDto } from '../lib/types'

export default function UserPromptsTab() {
  const [prompt, setPrompt] = useState<PromptTemplateDto | null>(null)
  const [text, setText] = useState('')
  const [loading, setLoading] = useState(true)
  const [saving, setSaving] = useState(false)
  const [success, setSuccess] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    loadUserPrompts()
  }, [])

  const loadUserPrompts = async () => {
    try {
      setLoading(true)
      const prompts = await api.getUserPrompts()
      const sp = prompts.find((p) => p.promptKey === 'SystemPrompt')
      setPrompt(sp ?? null)
      setText(sp?.templateText ?? '')
    } catch {
      // No user prompts yet - that's fine
    } finally {
      setLoading(false)
    }
  }

  const handleSave = async () => {
    setError(null)
    setSaving(true)
    try {
      const result = await api.upsertUserPrompt('SystemPrompt', text)
      setPrompt(result)
      setSuccess('Saved')
      setTimeout(() => setSuccess(null), 3000)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Save failed')
    } finally {
      setSaving(false)
    }
  }

  const handleDelete = async () => {
    setError(null)
    setSaving(true)
    try {
      await api.deleteUserPrompt('SystemPrompt')
      setPrompt(null)
      setText('')
      setSuccess('Removed')
      setTimeout(() => setSuccess(null), 3000)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Delete failed')
    } finally {
      setSaving(false)
    }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center py-12">
        <div className="animate-spin rounded-full h-6 w-6 border-b-2 border-primary" />
      </div>
    )
  }

  return (
    <div className="space-y-4">
      <div className="bg-card border border-border/60 rounded-xl shadow-sm p-5 space-y-4">
        <div>
          <h2 className="text-sm font-semibold text-foreground">Personal System Prompt Supplement</h2>
          <p className="text-xs text-muted-foreground mt-1">
            Add custom instructions that are appended to the system prompt for Q&A and chat.
            This supplements (does not replace) the platform and tenant prompts.
          </p>
        </div>

        <textarea
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="e.g., Always respond in bullet points. Focus on technical details."
          rows={6}
          className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm text-foreground font-mono placeholder-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent resize-y"
        />

        {error && (
          <div className="flex items-start gap-2 p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded-lg">
            <AlertCircle size={16} className="text-red-600 dark:text-red-400 mt-0.5 shrink-0" />
            <p className="text-sm text-red-600 dark:text-red-400">{error}</p>
          </div>
        )}

        <div className="flex items-center gap-3">
          <button
            onClick={handleSave}
            disabled={saving || !text.trim()}
            className="inline-flex items-center gap-1.5 px-3 py-1.5 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            <Save size={14} />
            Save
          </button>
          {prompt && (
            <button
              onClick={handleDelete}
              disabled={saving}
              className="inline-flex items-center gap-1.5 px-3 py-1.5 border border-border rounded-md text-sm font-medium text-muted-foreground hover:text-red-600 hover:border-red-300 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
            >
              <Trash2 size={14} />
              Remove
            </button>
          )}
          {success && (
            <span className="inline-flex items-center gap-1 text-sm text-green-600 dark:text-green-400">
              <CheckCircle2 size={14} /> {success}
            </span>
          )}
        </div>
      </div>
    </div>
  )
}
