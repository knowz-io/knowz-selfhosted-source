/**
 * SH_InstanceCopyHygiene R6 — `Knowz Self-Hosted · v{version}`.
 * The version is injected at build time (`VITE_APP_VERSION`, defaulted in
 * vite.config.ts from package.json) because no public version endpoint exists;
 * `/api/v1/admin/config/status` is SuperAdmin-gated. Recorded as Debt.
 */
export const APP_VERSION: string =
  (import.meta.env?.VITE_APP_VERSION as string | undefined) || '1.0.0'

export default function AppVersionFooter({ className = '' }: { className?: string }) {
  return (
    <p data-testid="sh-version-footer" className={`t-meta text-muted-foreground ${className}`}>
      Knowz Self-Hosted &middot; v{APP_VERSION}
    </p>
  )
}
