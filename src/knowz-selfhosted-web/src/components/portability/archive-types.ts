export interface ArchivePlan {
  planId: string; archiveId: string; manifestSha256: string; sourceSnapshot: string; destinationTenantId: string;
  mappingSha256: string; sourceTrust: string; preservationState: string; requiredPayloadCount: number;
  omissions: unknown[]; conflicts: unknown[]; mapping: Record<string, string>; expiresAt: string; idempotencyKey: string;
}
export interface ArchiveReceipt extends Omit<ArchivePlan, 'conflicts' | 'mapping' | 'expiresAt' | 'idempotencyKey'> {
  commitId: string; committedAt: string; preservedPayloadCount: number; clientVerificationState: string;
}
export interface ArchiveTransport {
  exportArchive(options: { vaultId?: string; allowPartial: boolean }, signal: AbortSignal): Promise<{ blob: Blob; signerFingerprint: string; receipt?: unknown }>;
  plan(blob: Blob, options: { strategy: string; targetVaultId?: string; allowPartial: boolean; expectedSignerFingerprint?: string; idempotencyKey: string; requiredOrdinaryFileBytes?: number }, signal: AbortSignal): Promise<ArchivePlan>;
  commit(planId: string, signal: AbortSignal): Promise<ArchiveReceipt>;
  cancel(planId: string): Promise<unknown>;
}
