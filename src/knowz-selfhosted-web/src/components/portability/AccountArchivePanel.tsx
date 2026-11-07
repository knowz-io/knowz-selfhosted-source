import { ArchiveWorkbench } from './ArchiveWorkbench';
import { createArchiveTransport } from '../../lib/archive-api';
import { useAuth } from '../../lib/auth';
const transport = createArchiveTransport({
  baseUrl: () => localStorage.getItem('apiUrl') || window.location.origin,
  headers: () => {
    const headers: Record<string, string> = {}; const token = sessionStorage.getItem('authToken'); const apiKey = localStorage.getItem('apiKey'); const tenant = localStorage.getItem('activeTenantId');
    if (token) headers.Authorization = `Bearer ${token}`; else if (apiKey) headers['X-Api-Key'] = apiKey;
    if (tenant) headers['X-Tenant-Id'] = tenant;
    return headers;
  },
});
export function AccountArchivePanel({ vaultId }: { vaultId?: string }) {
  const { user, activeTenantId } = useAuth();
  return <ArchiveWorkbench transport={transport} scopeKey={`${activeTenantId ?? user?.tenantId}:${user?.id}:${localStorage.getItem('apiUrl') ?? ''}`} vaultId={vaultId} />;
}
