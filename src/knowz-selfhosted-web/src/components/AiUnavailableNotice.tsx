import { useContext } from 'react'
import { Link } from 'react-router-dom'
import { AuthContext } from '../lib/auth'
import { UserRole } from '../lib/types'
import { AlertCircle } from 'lucide-react'

/**
 * SH_OfflineAiHonesty R5 — the unavailable-AI state is rendered as an
 * out-of-transcript notice, never as a successful assistant answer.
 * Nothing here is persisted to conversation history.
 */
export default function AiUnavailableNotice({ className = '' }: { className?: string }) {
  const auth = useContext(AuthContext)
  return (
    <div
      data-testid="ai-unavailable-notice"
      role="status"
      className={`flex items-start gap-2 rounded-2xl border border-amber-300/60 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-700/50 dark:bg-amber-950/30 dark:text-amber-200 ${className}`}
    >
      <AlertCircle size={16} className="mt-0.5 shrink-0" />
      <span>
        AI is not configured on this instance. Search and capture still work.{' '}
        <Link className="underline" to="/search">Search knowledge</Link>{' or '}
        <Link className="underline" to="/knowledge/new">capture knowledge</Link>.{' '}
        {auth?.user?.role === UserRole.SuperAdmin
          ? <Link className="underline" to="/admin/settings">Configure AI</Link>
          : 'Ask your instance administrator to configure an AI provider.'}
      </span>
    </div>
  )
}
