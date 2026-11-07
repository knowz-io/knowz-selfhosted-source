import { useRef } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { EncryptedRecordWorkspace } from './EncryptedRecordWorkspace';
import { PortableRecoveryPanel } from './PortableRecoveryPanel';
import { createEncryptedRecordTransport } from '../../lib/encrypted-record-api';
import { useAuth } from '../../lib/auth';
const transport = createEncryptedRecordTransport({ baseUrl: () => localStorage.getItem('apiUrl') || window.location.origin, headers: () => {
  const headers: Record<string, string> = {}; const token = sessionStorage.getItem('authToken'); const apiKey = localStorage.getItem('apiKey'); const tenant = localStorage.getItem('activeTenantId');
  if (token) headers.Authorization = `Bearer ${token}`; else if (apiKey) headers['X-Api-Key'] = apiKey; if (tenant) headers['X-Tenant-Id'] = tenant; return headers;
} });
export function AccountEncryptedRecord({ vaultId, knowledgeId }: { vaultId: string; knowledgeId: string }) {
  const { user, activeTenantId } = useAuth();
  const tenantId = activeTenantId ?? user?.tenantId;
  const queryClient = useQueryClient();
  const scopeKey = `${tenantId}:${user?.id}:${localStorage.getItem('apiUrl') ?? ''}`;
  const mutationScope = `${scopeKey}:${vaultId}:${knowledgeId}`;
  const currentScope = useRef(mutationScope); currentScope.current = mutationScope;
  const refreshKnowledge = () => {
    if (currentScope.current !== mutationScope) return;
    void queryClient.invalidateQueries({ queryKey: ['knowledge', knowledgeId] });
    void queryClient.invalidateQueries({ queryKey: ['knowledge-versions', knowledgeId] });
    void queryClient.invalidateQueries({ queryKey: ['knowledge-commit-history', knowledgeId] });
  };
  return <EncryptedRecordWorkspace transport={transport} vaultId={vaultId} knowledgeId={knowledgeId} custodyActor={user?.id && tenantId ? { actorId: user.id, tenantId } : undefined} scopeKey={scopeKey} onKnowledgeChanged={refreshKnowledge} />;
}
export function AccountPortableRecovery({ vaultId, knowledgeId }: { vaultId: string; knowledgeId: string }) {
  const { user, activeTenantId } = useAuth();
  return <PortableRecoveryPanel transport={transport} vaultId={vaultId} knowledgeId={knowledgeId} scopeKey={`${activeTenantId ?? user?.tenantId}:${user?.id}:${localStorage.getItem('apiUrl') ?? ''}`} />;
}
