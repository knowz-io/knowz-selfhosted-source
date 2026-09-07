import { useState, useEffect } from 'react'
import { Save, RotateCcw, CheckCircle2, AlertCircle } from 'lucide-react'
import { api, ApiError } from '../../lib/api-client'
import type { PromptTemplateDto } from '../../lib/types'

const PROMPT_LABELS: Record<string, { label: string; description: string }> = {
  SystemPrompt: {
    label: 'Q&A System Prompt',
    description: 'System instructions for the knowledge Q&A and chat assistant.',
  },
  TitlePrompt: {
    label: 'Title Generation',
    description: 'Instructions for auto-generating titles from content during enrichment.',
  },
  SummarizePrompt: {
    label: 'Summarization',
    description: 'Instructions for summarizing content. Use {0} as placeholder for max words.',
  },
  TagsPrompt: {
    label: 'Tag Extraction',
    description: 'Instructions for extracting tags from content. Use {0} as placeholder for max tags.',
  },
  DocumentEditorPrompt: {
    label: 'Document Editor',
    description: 'System instructions for AI-powered content amendments.',
  },
  NoContextResponse: {
    label: 'No Context Response',
    description: 'Message shown when no relevant knowledge is found for a question.',
  },
}

export default function PromptsPage() {
  const [prompts, setPrompts] = useState<PromptTemplateDto[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [editState, setEditState] = useState<Record<string, string>>({})
  const [saving, setSaving] = useState<Record<string, boolean>>({})
  const [saveSuccess, setSaveSuccess] = useState<Record<string, boolean>>({})
  const [saveError, setSaveError] = useState<Record<string, string | null>>({})

  useEffect(() => {
    loadPrompts()
  }, [])

  const loadPrompts = async () => {
    try {
      setLoading(true)
      const data = await api.getPlatformPrompts()
      setPrompts(data)
      const initial: Record<string, string> = {}
      data.forEach((p) => (initial[p.promptKey] = p.templateText))
      setEditState(initial)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Failed to load prompts')
    } finally {
      setLoading(false)
    }
  }

  const handleSave = async (key: string) => {
    setSaving((s) => ({ ...s, [key]: true }))
    setSaveError((s) => ({ ...s, [key]: null }))
    try {
      const result = await api.updatePlatformPrompt(key, editState[key])
      setPrompts((prev) => prev.map((p) => (p.promptKey === key ? result : p)))
      setSaveSuccess((s) => ({ ...s, [key]: true }))
      setTimeout(() => setSaveSuccess((s) => ({ ...s, [key]: false })), 3000)
    } catch (err) {
      setSaveError((s) => ({
        ...s,
        [key]: err instanceof ApiError ? err.message : 'Save failed',
      }))
    } finally {
      setSaving((s) => ({ ...s, [key]: false }))
    }
  }

  const handleReset = async (key: string) => {
    setSaving((s) => ({ ...s, [key]: true }))
    setSaveError((s) => ({ ...s, [key]: null }))
    try {
      const result = await api.resetPlatformPrompt(key)
      setPrompts((prev) => prev.map((p) => (p.promptKey === key ? result : p)))
      setEditState((s) => ({ ...s, [key]: result.templateText }))
      setSaveSuccess((s) => ({ ...s, [key]: true }))
      setTimeout(() => setSaveSuccess((s) => ({ ...s, [key]: false })), 3000)
    } catch (err) {
      setSaveError((s) => ({
        ...s,
        [key]: err instanceof ApiError ? err.message : 'Reset failed',
      }))
    } finally {
      setSaving((s) => ({ ...s, [key]: false }))
    }
  }

  const hasChanges = (key: string) => {
    const original = prompts.find((p) => p.promptKey === key)
    return original ? editState[key] !== original.templateText : false
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center py-20">
        <div className="animate-spin rounded-full h-8 w-8 border-b-2 border-primary" />
      </div>
    )
  }

  if (error) {
    return (
      <div className="flex items-start gap-2 p-4 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded-lg">
        <AlertCircle size={16} className="text-red-600 dark:text-red-400 mt-0.5 shrink-0" />
        <p className="text-sm text-red-600 dark:text-red-400">{error}</p>
      </div>
    )
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-xl font-semibold text-foreground">AI Prompts</h1>
        <p className="text-sm text-muted-foreground mt-1">
          Manage the system prompts used for Q&A, enrichment, and content editing. Changes apply to all tenants (platform-level defaults).
        </p>
      </div>

      {prompts.map((prompt) => {
        const meta = PROMPT_LABELS[prompt.promptKey] || {
          label: prompt.promptKey,
          description: '',
        }
        return (
          <div
            key={prompt.promptKey}
            className="bg-card border border-border/60 rounded-xl shadow-sm"
          >
            <div className="px-5 py-4 border-b border-border/60">
              <h2 className="text-sm font-semibold text-foreground">{meta.label}</h2>
              <p className="text-xs text-muted-foreground mt-0.5">{meta.description}</p>
            </div>
            <div className="px-5 py-4">
              <textarea
                value={editState[prompt.promptKey] || ''}
                onChange={(e) =>
                  setEditState((s) => ({ ...s, [prompt.promptKey]: e.target.value }))
                }
                rows={prompt.promptKey === 'NoContextResponse' ? 3 : 8}
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm text-foreground font-mono placeholder-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent resize-y"
              />
              {prompt.isSystemSeeded && (
                <p className="text-xs text-muted-foreground mt-1">System default (unmodified)</p>
              )}
            </div>
            <div className="flex items-center justify-between px-5 py-3 border-t border-border/60 bg-muted/50 rounded-b-xl">
              <div className="flex items-center gap-3">
                <button
                  onClick={() => handleSave(prompt.promptKey)}
                  disabled={saving[prompt.promptKey] || !hasChanges(prompt.promptKey)}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  <Save size={14} />
                  Save
                </button>
                <button
                  onClick={() => handleReset(prompt.promptKey)}
                  disabled={saving[prompt.promptKey]}
                  className="inline-flex items-center gap-1.5 px-3 py-1.5 border border-border rounded-md text-sm font-medium text-muted-foreground hover:text-foreground hover:border-foreground/30 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  <RotateCcw size={14} />
                  Reset to Default
                </button>
              </div>
              <div className="flex items-center gap-2">
                {saveSuccess[prompt.promptKey] && (
                  <span className="inline-flex items-center gap-1 text-sm text-green-600 dark:text-green-400">
                    <CheckCircle2 size={14} /> Saved
                  </span>
                )}
                {saveError[prompt.promptKey] && (
                  <span className="text-sm text-red-600 dark:text-red-400">
                    {saveError[prompt.promptKey]}
                  </span>
                )}
              </div>
            </div>
          </div>
        )
      })}
    </div>
  )
}
