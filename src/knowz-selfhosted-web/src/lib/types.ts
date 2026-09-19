export interface KnowledgeItem {
  id: string
  title: string
  content: string
  summary?: string
  briefSummary?: string
  type: string
  source?: string
  filePath?: string
  topic?: { id: string; name: string }
  tags: string[]
  vaults: { id: string; name: string; isPrimary: boolean }[]
  createdAt: string
  updatedAt: string
  isIndexed: boolean
  indexedAt?: string
  summaryRefinementGuidance?: string
}

export interface KnowledgeListItem {
  id: string
  title: string
  summary: string
  type: string
  filePath?: string
  vaultId?: string
  vaultName?: string
  createdByUserId?: string
  createdByUserName?: string
  createdAt: string
  updatedAt: string
  isIndexed: boolean
}

export interface CreatorRef {
  id: string
  name: string
}

export interface KnowledgeListResponse {
  items: KnowledgeListItem[]
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
}

export interface KnowledgeStats {
  totalKnowledgeItems: number
  byType: { type: string; count: number }[]
  byVault: { vault: string; count: number }[]
  dateRange?: { earliest: string; latest: string }
}

export interface Vault {
  id: string
  name: string
  description?: string
  vaultType?: string
  isDefault: boolean
  parentVaultId?: string
  knowledgeCount?: number
  createdAt: string
}

export interface VaultContentsResponse {
  vaultId: string
  items: KnowledgeListItem[]
  totalItems: number
}

export interface SearchResult {
  knowledgeId: string
  title: string
  content: string
  summary?: string
  vaultName?: string
  topicName?: string
  tags: string[]
  knowledgeType?: string
  filePath?: string
  score: number
}

export interface SearchResponse {
  items: SearchResult[]
  totalResults: number
}

export interface AskResponse {
  answer: string
  sources: { knowledgeId: string }[]
  confidence: number
  /** SH_OfflineAiHonesty: true when no AI provider is configured on this instance. */
  aiUnavailable?: boolean
}

export interface Topic {
  id: string
  name: string
  description?: string
  knowledgeCount: number
}

export interface TopicDetails {
  id: string
  name: string
  description?: string
  knowledgeItems: KnowledgeListItem[]
}

export interface CreateKnowledgeData {
  title?: string
  content: string
  type?: string
  vaultId?: string
  tags?: string[]
  source?: string
  attachmentFileRecordIds?: string[]
}

export interface UpdateKnowledgeData {
  title?: string
  content?: string
  source?: string
  tags?: string[]
  vaultId?: string
  summaryRefinementGuidance?: string
}

// --- Auth & Admin Types ---

export enum UserRole {
  User = 0,
  Admin = 1,
  SuperAdmin = 2,
}

const roleStringToNumber: Record<string, UserRole> = {
  SuperAdmin: UserRole.SuperAdmin,
  Admin: UserRole.Admin,
  User: UserRole.User,
}

/** Normalize role from API (may be string "SuperAdmin" or number 0) to numeric enum */
export function normalizeRole(role: number | string): UserRole {
  if (typeof role === 'string') {
    return roleStringToNumber[role] ?? UserRole.User
  }
  return role
}

/** Normalize a UserDto's role field from API response */
export function normalizeUser(u: UserDto): UserDto {
  return { ...u, role: normalizeRole(u.role) }
}

export interface UserDto {
  id: string
  username: string
  email: string | null
  displayName: string | null
  role: number
  tenantId: string
  tenantName: string | null
  isActive: boolean
  mustChangePassword?: boolean
  apiKey: string | null
  createdAt: string
  lastLoginAt: string | null
  /**
   * User's preferred IANA timezone (e.g. "America/New_York"). Null/undefined
   * means no preference has been set — the UI should fall back to
   * DEFAULT_USER_TIMEZONE from useFormatters.
   */
  timeZonePreference?: string | null
}

export interface UserPreferencesDto {
  timeZonePreference: string | null
}

export interface UpdateUserPreferencesRequest {
  timeZonePreference: string | null
}

export interface TenantDto {
  id: string
  name: string
  slug: string
  description: string | null
  isActive: boolean
  userCount: number
  createdAt: string
}

export interface LoginResponse {
  token: string
  expiresAt: string
  user: UserDto
}

export interface ChangePasswordResponse extends LoginResponse {
  message: string
}

export interface TenantMembershipDto {
  tenantId: string
  tenantName: string
  tenantSlug: string
  role: number
  isActive: boolean
}

export interface MultiTenantLoginResponse {
  token: string
  expiresAt: string | null
  user: UserDto | null
  requiresTenantSelection: boolean
  userId: string | null
  availableTenants: TenantMembershipDto[]
  selectionToken: string | null
}

export interface SelectTenantData {
  userId: string
  tenantId: string
  selectionToken: string
}

export interface SwitchTenantData {
  tenantId: string
}

export interface CreateTenantData {
  name: string
  slug: string
  description?: string
}

export interface UpdateTenantData {
  name?: string
  slug?: string
  description?: string
  isActive?: boolean
}

export interface CreateUserData {
  tenantId: string
  username: string
  password: string
  email?: string
  displayName?: string
  role: number
}

export interface UpdateUserData {
  email?: string
  displayName?: string
  role?: number
  isActive?: boolean
}

export interface ResetPasswordData {
  newPassword: string
}

// --- Vault Access Types ---

export interface UserPermissionsDto {
  userId: string
  hasAllVaultsAccess: boolean
  canCreateVaults: boolean
}

export interface UserVaultAccessDto {
  id: string
  vaultId: string
  vaultName: string
  canRead: boolean
  canWrite: boolean
  canDelete: boolean
  canManage: boolean
}

export interface VaultAccessGrant {
  vaultId: string
  canRead: boolean
  canWrite: boolean
  canDelete: boolean
  canManage: boolean
}

// --- Tag & Entity Types ---

export interface TagItem {
  id: string
  name: string
  knowledgeCount: number
  createdAt: string
}

export interface EntityItem {
  id: string
  name: string
  createdAt: string
}

// --- Inbox Types ---

export interface InboxItemDto {
  id: string
  body: string
  type: string
  createdByUserId?: string | null
  createdAt: string
  updatedAt: string
}

export interface InboxListResponse {
  items: InboxItemDto[]
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
  inboxVisibilityScope: string
}

export interface ConvertResult {
  inboxItemId: string
  knowledgeId: string
  title: string
  converted: boolean
}

export interface BatchConvertResult {
  requested: number
  converted: number
  failed: number
  results: ConvertResult[]
}

// --- File Storage Types ---

export interface FileMetadataDto {
  id: string
  fileName: string
  contentType?: string
  sizeBytes: number
  blobUri?: string
  transcriptionText?: string
  extractedText?: string
  visionDescription?: string
  blobMigrationPending: boolean
  createdAt: string
  updatedAt: string
  knowledgeId?: string
  knowledgeTitle?: string
  vaultId?: string
  vaultName?: string
  // Structured vision/document fields (NodeID: SelfHostedAttachmentExperience)
  visionTagsJson?: string
  visionObjectsJson?: string
  visionExtractedText?: string
  visionAnalyzedAt?: string
  layoutDataJson?: string
  textExtractionStatus?: number
  textExtractedAt?: string
  attachmentAIProvider?: string
}

export interface FileUploadResult {
  fileRecordId: string
  fileName: string
  contentType: string
  sizeBytes: number
  blobUri: string
  success: boolean
}

export interface FileListResponse {
  items: FileMetadataDto[]
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
}

export interface FileAttachmentDto {
  id: string
  fileRecordId: string
  knowledgeId?: string
  commentId?: string
  createdAt: string
}

// --- Admin Configuration Types ---

export interface ConfigCategoryDto {
  probeKind?: string
  category: string
  displayName: string
  description: string
  requiresRestart: boolean
  entries: ConfigEntryDto[]
}

export interface ConfigEntryDto {
  applicationError?: string | null
  editable?: boolean
  authority?: string | null
  effectiveIsSet?: boolean
  pendingRestart?: boolean
  key: string
  value: string | null
  isSecret: boolean
  requiresRestart: boolean
  description: string | null
  isSet: boolean
  lastModifiedAt: string | null
  lastModifiedBy: string | null
  source: string | null
}

export interface ConfigEntryUpdateDto {
  key: string
  value: string | null
}

export interface ConfigUpdateResult {
  success: boolean
  restartRequired: boolean
  errors: string[]
  entriesUpdated: number
}

export interface ServiceHealthResult {
  probeKind?: string
  probeStatus?: string
  checkedAt?: string | null
  category: string
  displayName: string
  isHealthy: boolean
  status: string
  latencyMs: number | null
}

export interface DeploymentStatusDto {
  configurationErrors?: Record<string, string>
  activeProvider?: string
  capabilities?: string[]
  mode: string
  version: string
  startupTime: string
  restartRequired: boolean
  restartReasons: string[]
}

// --- Chat Types ---

export interface ChatMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  sources?: { knowledgeId: string }[]
  confidence?: number
  timestamp: string
}

export interface ChatConversation {
  id: string
  title: string
  vaultId?: string
  messages: ChatMessage[]
  createdAt: string
  updatedAt: string
}

export interface ChatRequestData {
  question: string
  conversationHistory?: { role: string; content: string }[]
  vaultId?: string
  knowledgeId?: string
  researchMode?: boolean
  maxTurns?: number
}

export interface ChatResponseData {
  answer: string
  sources: { knowledgeId: string }[]
  confidence: number
  /** SH_OfflineAiHonesty: true when no AI provider is configured on this instance. */
  aiUnavailable?: boolean
}

// --- Comment/Contribution Types ---

export interface Comment {
  id: string
  knowledgeId: string
  parentCommentId?: string
  authorName: string
  body: string
  isAnswer: boolean
  sentiment?: string
  createdAt: string
  updatedAt: string
  replies?: Comment[]
  attachmentCount: number
}

export interface CreateCommentData {
  body: string
  authorName?: string
  parentCommentId?: string
  sentiment?: string
}

export interface UpdateCommentData {
  body?: string
  sentiment?: string
}

// WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — FEAT_CommentDeleteAttachmentChoice.
// Result envelope returned from DELETE /api/v1/comments/{id}?deleteFiles=...
// Mirrors the shape of the platform's CommentDeleteResult DTO.
export interface CommentDeleteResult {
  filesPreserved: number
  filesDeleted: number
  preservedFileNames: string[]
  deletedFileNames: string[]
}

// WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
// Result envelope returned from DELETE /api/v1/knowledge/{id}/attachments/{fileId}?deleteFiles=...
export interface DetachResult {
  detached: boolean
  filesDeleted: number
  filesPreserved: number
  deletedFileNames: string[]
  preservedFileNames: string[]
}

// Config-backed file-cleanup policy (GET /api/v1/settings/file-cleanup).
// Mode is one of 'AutoCleanup' | 'PreserveAlways' | 'PromptUser'.
export interface FileCleanupSettings {
  mode: 'AutoCleanup' | 'PreserveAlways' | 'PromptUser'
}

// --- SSO Types ---

export interface SelfHostedSSOConfigDto {
  isEnabled: boolean
  clientId: string | null
  hasClientSecret: boolean
  directoryTenantId: string | null
  autoProvisionUsers: boolean
  defaultRole: string
  detectedMode: string
  lastTestedAt: string | null
  lastTestSucceeded: boolean | null
}

export interface SelfHostedSSOConfigRequest {
  isEnabled: boolean
  clientId: string | null
  clientSecret: string | null
  directoryTenantId: string | null
  autoProvisionUsers: boolean
  defaultRole: string
}

export interface SelfHostedSSOTestResultDto {
  success: boolean
  detectedMode: string | null
  errorMessage: string | null
  status: string | null
  validTenantIds: string[] | null
  testedAt: string
}

export interface SSOProviderInfo {
  provider: string
  displayName: string
  mode?: string
}

export interface SSOCallbackResultDto {
  success: boolean
  token: string
  expiresAt: string
  email: string | null
  displayName: string | null
  wasAutoProvisioned: boolean
}

// --- Prompt Template Types ---

export interface PromptTemplateDto {
  id: string
  promptKey: string
  scope: string
  templateText: string
  mergeStrategy: string
  description: string | null
  isSystemSeeded: boolean
  updatedAt: string
  lastModifiedBy: string | null
}

// --- Knowledge Versioning Types ---

export interface KnowledgeVersion {
  id: string
  knowledgeId: string
  versionNumber: number
  title: string
  content: string
  contentType?: string
  createdAt: string
  createdByUserId?: string
  changeDescription?: string
}

// --- Audit Log Types ---

export interface AuditLogEntry {
  id: string
  entityType: string
  entityId: string
  action: string
  userId?: string
  userEmail?: string
  timestamp: string
  details?: string
}

export interface PaginatedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalItems: number
  totalPages: number
}

// --- Git Sync Types ---

export interface GitSyncStatus {
  id: string
  vaultId: string
  repositoryUrl: string
  branch: string
  lastSyncCommitSha?: string
  lastSyncAt?: string
  status: string
  filePatterns?: string
  errorMessage?: string
  trackCommitHistory?: boolean
  commitHistoryDepth?: number
}

export interface GitSyncHistoryEntry {
  action: string
  timestamp: string
  details?: string
}

// --- Commit History Types ---

export interface CommitHistoryEntry {
  knowledgeId: string
  sha: string
  shortSha: string
  title: string
  authorName: string
  committedAt: string  // ISO 8601
  changedFileCount: number
  linesAdded: number
  linesDeleted: number
  content: string
}

export interface CommitHistoryResponse {
  items: CommitHistoryEntry[]
  total: number
  page: number
  pageSize: number
}

// --- Knowz Cloud connect types ---

export type PlatformConnectionTestStatus =
  | 'Untested'
  | 'Ok'
  | 'Unauthorized'
  | 'NetworkError'
  | 'SchemaIncompatible'

export interface PlatformConnectionDto {
  platformApiUrl: string
  displayName: string | null
  hasApiKey: boolean
  apiKeyMask: string | null
  remoteTenantId: string | null
  lastTestedAt: string | null
  lastTestStatus: PlatformConnectionTestStatus
  lastTestError: string | null
  updatedAt: string
}

export interface UpsertPlatformConnectionRequest {
  platformApiUrl: string
  displayName?: string | null
  apiKey?: string | null
}

export interface PlatformConnectionTestResult {
  status: PlatformConnectionTestStatus
  message: string | null
  remoteTenantId: string | null
  schemaVersion: string | null
}

export interface PlatformVaultDto {
  id: string
  name: string
  description: string | null
  knowledgeCount: number
  updatedAt: string | null
}

export interface PlatformVaultListDto {
  vaults: PlatformVaultDto[]
  totalCount: number
}

export interface PlatformKnowledgeSummaryDto {
  id: string
  title: string
  summary: string | null
  updatedAt: string | null
  createdBy: string | null
}

export interface PlatformKnowledgeListDto {
  items: PlatformKnowledgeSummaryDto[]
  page: number
  pageSize: number
  totalCount: number
}

export interface PlatformKnowledgeDetailDto {
  id: string
  title: string
  content: string | null
  summary: string | null
  tags: string | null
  updatedAt: string | null
  createdBy: string | null
}

export type SyncConflictStrategy = 'Skip' | 'Overwrite'

export interface SyncItemRequest {
  knowledgeId: string
  overwriteLocal: boolean
}

export type SyncItemOutcome =
  | 'Created'
  | 'Updated'
  | 'Skipped'
  | 'Unchanged'
  | 'Failed'
  | 'RateLimited'
  | 'PermissionDenied'
  | 'NotFound'

export interface SyncItemResult {
  success: boolean
  outcome: SyncItemOutcome
  localKnowledgeId: string | null
  message: string | null
  duration: string
}

export interface VaultSyncStatusDto {
  linkId: string
  localVaultId: string
  localVaultName: string
  remoteVaultId: string
  platformApiUrl: string
  status: string
  lastSyncError: string | null
  lastSyncCompletedAt: string | null
  lastPullCursor: string | null
  lastPushCursor: string | null
  syncEnabled: boolean
}

/**
 * UI_DestinationsLiveConnect S4 — direction of a manual Destinations run.
 * Mirrors `Knowz.SelfHosted.Application.DTOs.SyncDirection`.
 */
export type SyncDirection = 'Full' | 'PullOnly' | 'PushOnly'

/**
 * UI_DestinationsLiveConnect S4 — result of `POST /api/v1/sync/run/{localVaultId}`.
 * Mirrors `Knowz.SelfHosted.Application.DTOs.VaultSyncResult` (JSON is camelCase).
 * `partial` is true when the run stopped at the 100-item-per-run cap; the cursor is
 * kept, so re-running continues from where it left off.
 */
export interface VaultSyncResult {
  success: boolean
  direction: SyncDirection
  pullAccepted: number
  pullSkipped: number
  pushAccepted: number
  pushSkipped: number
  tombstonesApplied: number
  details: string[]
  error: string | null
  duration: string
  partial: boolean
}

export type PlatformSyncOperation =
  | 'Connect'
  | 'Disconnect'
  | 'TestConnection'
  | 'BrowseVaults'
  | 'BrowseKnowledge'
  | 'PullItem'
  | 'PushItem'
  | 'PullVault'
  | 'PushVault'

export type PlatformSyncDirection = 'None' | 'Pull' | 'Push'

export type PlatformSyncRunStatus = 'InProgress' | 'Succeeded' | 'Failed' | 'Partial'

export interface PlatformSyncRunDto {
  id: string
  vaultSyncLinkId: string | null
  userId: string
  userEmail: string | null
  operation: PlatformSyncOperation
  direction: PlatformSyncDirection
  knowledgeId: string | null
  itemCount: number
  bytesTransferred: number
  status: PlatformSyncRunStatus
  errorMessage: string | null
  startedAt: string
  completedAt: string | null
}

// --- SSE Streaming Types ---

export interface SSESourcesEvent {
  type: 'sources'
  sources: { knowledgeId: string }[]
  confidence: number
  /** SH_OfflineAiHonesty R4: carried on the preamble, not a new event type. */
  aiUnavailable?: boolean
}

export interface SSETokenEvent {
  type: 'token'
  content: string
}

export interface SSEDoneEvent {
  type: 'done'
}

export interface SSEErrorEvent {
  type: 'error'
  message: string
}

export type SSEEvent = SSESourcesEvent | SSETokenEvent | SSEDoneEvent | SSEErrorEvent
