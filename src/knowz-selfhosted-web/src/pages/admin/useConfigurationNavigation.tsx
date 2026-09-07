import { useCallback, useEffect, useRef, useState } from 'react'
import { useBlocker, type Blocker } from 'react-router-dom'
import { createPortal } from 'react-dom'

type Draft = { save: () => Promise<unknown>; saving: boolean }
export type RegisterConfigurationDraft = (category: string, draft: Draft | null) => void

/** One blocker for all category drafts, including hidden tabs. */
export function useConfigurationNavigation() {
  const [drafts, setDrafts] = useState(new Map<string, Draft>())
  const registerDraft = useCallback<RegisterConfigurationDraft>((category, draft) => {
    setDrafts(previous => {
      const next = new Map(previous)
      if (draft) next.set(category, draft)
      else next.delete(category)
      return next
    })
  }, [])
  const dirty = drafts.size > 0
  const blocker = useBlocker(({ currentLocation, nextLocation }) =>
    dirty && currentLocation.pathname !== nextLocation.pathname)

  useEffect(() => {
    if (!dirty) return
    const warn = (event: BeforeUnloadEvent) => { event.preventDefault(); event.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [dirty])

  return {
    registerDraft,
    navigationDialog: blocker.state === 'blocked'
      ? createPortal(<ConfigurationNavigationDialog blocker={blocker} drafts={drafts} />, document.body)
      : null,
  }
}

function ConfigurationNavigationDialog({ blocker, drafts }: { blocker: Blocker; drafts: Map<string, Draft> }) {
  const dialogRef = useRef<HTMLDialogElement>(null)
  const stayRef = useRef<HTMLButtonElement>(null)
  const active = useRef(true)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const busy = saving || [...drafts.values()].some(draft => draft.saving)

  useEffect(() => {
    active.current = true
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
    const dialog = dialogRef.current!
    dialog.showModal()
    stayRef.current?.focus()
    return () => {
      active.current = false
      dialog.close()
      if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true })
    }
  }, [])

  const saveAndLeave = async () => {
    if (busy) return
    setSaving(true)
    setError(null)
    try {
      // Each existing category API remains atomic; a failed category stays dirty.
      for (const draft of drafts.values()) {
        if (!active.current) return
        await draft.save()
      }
      if (active.current) blocker.proceed?.()
    } catch (cause) {
      if (active.current) setError(cause instanceof Error ? cause.message : 'Changes could not be saved. Please try again.')
    } finally {
      if (active.current) setSaving(false)
    }
  }

  return (
    <dialog
      ref={dialogRef}
      aria-labelledby="configuration-navigation-title"
      aria-describedby="configuration-navigation-description"
      onCancel={event => { event.preventDefault(); blocker.reset?.() }}
      onKeyDown={event => {
        if (event.key !== 'Tab') return
        const buttons = [...event.currentTarget.querySelectorAll<HTMLButtonElement>('button:not(:disabled)')]
        const first = buttons[0], last = buttons[buttons.length - 1]
        if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus() }
        else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus() }
      }}
      className="fixed inset-0 m-auto w-[calc(100%-2rem)] max-w-lg rounded-xl border border-border bg-card p-6 text-foreground shadow-xl backdrop:bg-black/50"
    >
      <h2 id="configuration-navigation-title" className="text-lg font-semibold">Unsaved configuration changes</h2>
      <p id="configuration-navigation-description" className="mt-2 text-sm text-muted-foreground">
        Your edits have not been saved. Stay to keep editing, leave without saving, or save changes in all edited tabs before leaving.
      </p>
      {error && <p role="alert" className="mt-3 text-sm text-red-600 dark:text-red-400">{error} Your remaining edits are still here.</p>}
      <div className="mt-6 flex flex-wrap justify-end gap-2">
        <button ref={stayRef} onClick={() => blocker.reset?.()} className="rounded-md border border-input px-3 py-2 text-sm disabled:opacity-50">Stay</button>
        <button disabled={busy} onClick={() => blocker.proceed?.()} className="rounded-md border border-input px-3 py-2 text-sm disabled:opacity-50">Leave without saving</button>
        <button disabled={busy} onClick={saveAndLeave} className="rounded-md bg-primary px-3 py-2 text-sm text-primary-foreground disabled:opacity-50">
          {saving ? 'Saving changes…' : 'Save changes and leave'}
        </button>
      </div>
    </dialog>
  )
}
