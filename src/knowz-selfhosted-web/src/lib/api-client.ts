import { normalizeUser } from './types'
import type {
  KnowledgeItem,
  KnowledgeListResponse,
  KnowledgeStats,
  CreatorRef,
  Vault,
  VaultContentsResponse,
  SearchResponse,
  AskResponse,
  Topic,
  TopicDetails,
  CreateKnowledgeData,
  UpdateKnowledgeData,
  LoginResponse,
  MultiTenantLoginResponse,
  SelectTenantData,
  SwitchTenantData,
  TenantMembershipDto,
  UserDto,
  UserPreferencesDto,
  UpdateUserPreferencesRequest,
  TenantDto,
  CreateTenantData,
  UpdateTenantData,
  CreateUserData,
  UpdateUserData,
  ResetPasswordData,
  UserPermissionsDto,
  UserVaultAccessDto,
  VaultAccessGrant,
  TagItem,
  EntityItem,
  InboxItemDto,
  InboxListResponse,
  ConvertResult,
  BatchConvertResult,
  ChatRequestData,
  ChatResponseData,
  SSEEvent,
  FileUploadResult,
  FileListResponse,
  FileMetadataDto,
  FileAttachmentDto,
  Comment,
  CreateCommentData,
  UpdateCommentData,
  CommentDeleteResult,
  DetachResult,
  FileCleanupSettings,
  ConfigCategoryDto,
  ConfigEntryUpdateDto,
  ConfigUpdateResult,
  ServiceHealthResult,
  DeploymentStatusDto,
  SelfHostedSSOConfigDto,
  SelfHostedSSOConfigRequest,
  SelfHostedSSOTestResultDto,
  SSOProviderInfo,
  PromptTemplateDto,
  KnowledgeVersion,
  PaginatedResult,
  AuditLogEntry,
  GitSyncStatus,
  GitSyncHistoryEntry,
  CommitHistoryResponse,
  PlatformConnectionDto,
  UpsertPlatformConnectionRequest,
  PlatformConnectionTestResult,
  PlatformVaultListDto,
  PlatformKnowledgeListDto,
  PlatformKnowledgeDetailDto,
  SyncItemResult,
  SyncDirection,
  VaultSyncResult,
  VaultSyncStatusDto,
  PlatformSyncRunDto,
} from './types'

const getApiUrl = (): string =>
  localStorage.getItem('apiUrl') || window.location.origin

const getApiKey = (): string | null =>
  localStorage.getItem('apiKey')

const getAuthToken = (): string | null =>
  sessionStorage.getItem('authToken')

const getActiveTenantId = (): string | null =>
  localStorage.getItem('activeTenantId')

export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message)
    this.name = 'ApiError'
  }
}

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
  const apiUrl = getApiUrl()
  const authToken = getAuthToken()
  const apiKey = getApiKey()
  const activeTenantId = getActiveTenantId()

  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    ...((options.headers as Record<string, string>) || {}),
  }

  if (authToken) {
    headers['Authorization'] = `Bearer ${authToken}`
  } else if (apiKey) {
    headers['X-Api-Key'] = apiKey
  }

  // SuperAdmin tenant override
  if (activeTenantId) {
    headers['X-Tenant-Id'] = activeTenantId
  }

  let response: Response
  try {
    response = await fetch(`${apiUrl}${path}`, {
      ...options,
      headers,
      redirect: 'error',
    })
  } catch (networkErr) {
    throw networkErr
  }

  if (!response.ok) {
    if (response.status === 401 && authToken && getAuthToken() === authToken) {
      sessionStorage.removeItem('authToken')
    }
    const body = await response.json().catch(() => ({}))
    throw new ApiError(
      response.status,
      (body as { error?: string }).error ||
      (Array.isArray((body as { errors?: string[] }).errors) ? (body as { errors: string[] }).errors.join(' ') : '') ||
      `Request failed: ${response.status}`,
    )
  }

  return response.json()
}

async function requestUpload<T>(path: string, formData: FormData): Promise<T> {
  const apiUrl = getApiUrl()
  const authToken = getAuthToken()
  const apiKey = getApiKey()
  const activeTenantId = getActiveTenantId()

  const headers: Record<string, string> = {}

  if (authToken) {
    headers['Authorization'] = `Bearer ${authToken}`
  } else if (apiKey) {
    headers['X-Api-Key'] = apiKey
  }

  if (activeTenantId) {
    headers['X-Tenant-Id'] = activeTenantId
  }

  let response: Response
  try {
    response = await fetch(`${apiUrl}${path}`, {
      method: 'POST',
      headers,
      redirect: 'error',
      body: formData,
    })
  } catch (networkErr) {
    throw networkErr
  }

  if (!response.ok) {
    if (response.status === 401 && authToken && getAuthToken() === authToken) {
      sessionStorage.removeItem('authToken')
    }
    const body = await response.json().catch(() => ({}))
    throw new ApiError(
      response.status,
      (body as { error?: string }).error ||
      (Array.isArray((body as { errors?: string[] }).errors) ? (body as { errors: string[] }).errors.join(' ') : '') ||
      `Request failed: ${response.status}`,
    )
  }

  return response.json()
}

async function requestBlob(path: string): Promise<Blob> {
  const apiUrl = getApiUrl()
  const authToken = getAuthToken()
  const apiKey = getApiKey()
  const activeTenantId = getActiveTenantId()

  const headers: Record<string, string> = {}

  if (authToken) {
    headers['Authorization'] = `Bearer ${authToken}`
  } else if (apiKey) {
    headers['X-Api-Key'] = apiKey
  }

  if (activeTenantId) {
    headers['X-Tenant-Id'] = activeTenantId
  }

  const response = await fetch(`${apiUrl}${path}`, { headers, redirect: 'error' })

  if (!response.ok) {
    if (response.status === 401 && authToken && getAuthToken() === authToken) {
      sessionStorage.removeItem('authToken')
    }
    const body = await response.json().catch(() => ({}))
    throw new ApiError(
      response.status,
      (body as { error?: string }).error ||
      (Array.isArray((body as { errors?: string[] }).errors) ? (body as { errors: string[] }).errors.join(' ') : '') ||
      `Request failed: ${response.status}`,
    )
  }

  return response.blob()
}

async function streamRequest(
  path: string,
  body: unknown,
  onEvent: (event: SSEEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  const apiUrl = getApiUrl()
  const authToken = getAuthToken()
  const apiKey = getApiKey()
  const activeTenantId = getActiveTenantId()

  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
  }

  if (authToken) {
    headers['Authorization'] = `Bearer ${authToken}`
  } else if (apiKey) {
    headers['X-Api-Key'] = apiKey
  }

  if (activeTenantId) {
    headers['X-Tenant-Id'] = activeTenantId
  }

  const response = await fetch(`${apiUrl}${path}`, {
    method: 'POST',
    headers,
    redirect: 'error',
    body: JSON.stringify(body),
    signal,
  })

  if (!response.ok) {
    if (response.status === 401 && authToken && getAuthToken() === authToken) {
      sessionStorage.removeItem('authToken')
    }
    const errorBody = await response.json().catch(() => ({}))
    throw new ApiError(
      response.status,
      (errorBody as { error?: string }).error || `Request failed: ${response.status}`,
    )
  }

  const reader = response.body?.getReader()
  if (!reader) throw new Error('No response body')

  const decoder = new TextDecoder()
  let buffer = ''

  try {
    while (true) {
      const { done, value } = await reader.read()
      if (done) break

      buffer += decoder.decode(value, { stream: true })

      // Parse SSE lines
      const lines = buffer.split('\n')
      buffer = lines.pop() || '' // Keep incomplete line in buffer

      for (const line of lines) {
        const trimmed = line.trim()
        if (!trimmed || !trimmed.startsWith('data: ')) continue
        const jsonStr = trimmed.slice(6) // Remove "data: "
        try {
          const event = JSON.parse(jsonStr) as SSEEvent
          onEvent(event)
        } catch {
          // Skip malformed events
        }
      }
    }
  } finally {
    reader.releaseLock()
  }
}

function buildQuery(params?: Record<string, string | undefined>): string {
  if (!params) return ''
  const filtered = Object.entries(params).filter(
    (entry): entry is [string, string] => entry[1] != null && entry[1] !== '',
  )
  if (filtered.length === 0) return ''
  return '?' + new URLSearchParams(filtered).toString()
}

export const api = {
  // --- Knowledge ---
  listKnowledge: (params?: Record<string, string | undefined>) =>
    request<KnowledgeListResponse>(`/api/v1/knowledge${buildQuery(params)}`),

  getKnowledge: (id: string) =>
    request<KnowledgeItem>(`/api/v1/knowledge/${id}`),

  createKnowledge: (data: CreateKnowledgeData) =>
    request<{ id: string; title: string; created: boolean }>('/api/v1/knowledge', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  updateKnowledge: (id: string, data: UpdateKnowledgeData) =>
    request<{ id: string; title: string; updated: boolean }>(`/api/v1/knowledge/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  deleteKnowledge: (id: string) =>
    request<{ id: string; deleted: boolean }>(`/api/v1/knowledge/${id}`, {
      method: 'DELETE',
    }),

  reprocessKnowledge: (id: string) =>
    request<{ id: string; title: string; reprocessed: boolean }>(`/api/v1/knowledge/${id}/reprocess`, {
      method: 'POST',
    }),

  getKnowledgeCreators: () =>
    request<CreatorRef[]>('/api/v1/knowledge/creators'),

  getStats: () =>
    request<KnowledgeStats>('/api/v1/knowledge/stats'),

  // --- Vaults ---
  listVaults: (includeStats = true) =>
    request<{ vaults: Vault[] }>(`/api/v1/vaults?includeStats=${includeStats}`),

  getVault: (id: string) =>
    request<Vault>(`/api/v1/vaults/${id}`),

  getVaultContents: (id: string, includeChildren = true, limit = 100) =>
    request<VaultContentsResponse>(
      `/api/v1/vaults/${id}/contents?includeChildren=${includeChildren}&limit=${limit}`,
    ),

  createVault: (data: { name: string; description?: string; parentVaultId?: string; vaultType?: string }) =>
    request<{ id: string; name: string; created: boolean }>('/api/v1/vaults', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  updateVault: (id: string, data: { name?: string; description?: string }) =>
    request<{ id: string; name: string; updated: boolean }>(`/api/v1/vaults/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  deleteVault: (id: string) =>
    request<{ id: string; deleted: boolean }>(`/api/v1/vaults/${id}`, {
      method: 'DELETE',
    }),

  // --- Search & Ask ---
  search: (q: string, params?: Record<string, string | undefined>) =>
    request<SearchResponse>(`/api/v1/search${buildQuery({ q, ...params })}`),

  ask: (data: { question: string; vaultId?: string; researchMode?: boolean }) =>
    request<AskResponse>('/api/v1/ask', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  // --- Topics ---
  listTopics: (limit = 50) =>
    request<{ topics: Topic[]; totalCount: number }>(`/api/v1/topics?limit=${limit}`),

  getTopicDetails: (id: string) =>
    request<TopicDetails>(`/api/v1/topics/${id}`),

  // --- Entities ---
  findEntities: (type: string, q?: string, limit = 50) =>
    request<{ entityType: string; entities: EntityItem[] }>(
      `/api/v1/entities${buildQuery({ type, q, limit: String(limit) })}`,
    ),

  createEntity: (type: string, name: string) =>
    request<EntityItem>('/api/v1/entities', {
      method: 'POST',
      body: JSON.stringify({ type, name }),
    }),

  updateEntity: (id: string, type: string, name: string) =>
    request<EntityItem>(`/api/v1/entities/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ type, name }),
    }),

  deleteEntity: (id: string, type: string) =>
    request<{ id: string; deleted: boolean }>(`/api/v1/entities/${id}?type=${type}`, {
      method: 'DELETE',
    }),

  // --- Tags ---
  listTags: (q?: string, limit = 50) =>
    request<TagItem[]>(`/api/v1/tags${buildQuery({ q, limit: String(limit) })}`),

  createTag: (name: string) =>
    request<TagItem>('/api/v1/tags', {
      method: 'POST',
      body: JSON.stringify({ name }),
    }),

  updateTag: (id: string, name: string) =>
    request<TagItem>(`/api/v1/tags/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ name }),
    }),

  deleteTag: (id: string) =>
    request<{ id: string; deleted: boolean }>(`/api/v1/tags/${id}`, {
      method: 'DELETE',
    }),

  // --- Inbox ---
  createInboxItem: (body: string) =>
    request<{ id: string; created: boolean }>('/api/v1/inbox', {
      method: 'POST',
      body: JSON.stringify({ body }),
    }),

  listInbox: (params?: Record<string, string | undefined>) =>
    request<InboxListResponse>(`/api/v1/inbox${buildQuery(params)}`),

  getInboxItem: (id: string) => request<InboxItemDto>(`/api/v1/inbox/${id}`),

  updateInboxItem: (id: string, body: string) =>
    request<InboxItemDto>(`/api/v1/inbox/${id}`, {
      method: 'PUT',
      body: JSON.stringify({ body }),
    }),

  deleteInboxItem: (id: string) =>
    request<{ id: string; deleted: boolean }>(`/api/v1/inbox/${id}`, {
      method: 'DELETE',
    }),

  convertInboxItem: (id: string, data: { vaultId?: string; tags?: string[] }) =>
    request<ConvertResult>(`/api/v1/inbox/${id}/convert`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  batchConvertInbox: (data: { ids: string[]; vaultId?: string; tags?: string[] }) =>
    request<BatchConvertResult>('/api/v1/inbox/batch-convert', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  // --- Files ---
  uploadFile: (file: File) => {
    const formData = new FormData()
    formData.append('file', file)
    return requestUpload<FileUploadResult>('/api/v1/files/upload', formData)
  },

  listFiles: (page?: number, pageSize?: number, search?: string, contentTypeFilter?: string) =>
    request<FileListResponse>(
      `/api/v1/files${buildQuery({
        page: page != null ? String(page) : undefined,
        pageSize: pageSize != null ? String(pageSize) : undefined,
        search,
        contentTypeFilter,
      })}`,
    ),

  getFileMetadata: (id: string) =>
    request<FileMetadataDto>(`/api/v1/files/${id}`),

  downloadFile: (id: string) =>
    requestBlob(`/api/v1/files/${id}/download`),

  deleteFile: (id: string) =>
    request<void>(`/api/v1/files/${id}`, {
      method: 'DELETE',
    }),

  // --- File Attachments ---
  attachFileToKnowledge: (knowledgeId: string, fileRecordId: string) =>
    request<FileAttachmentDto>(`/api/v1/knowledge/${knowledgeId}/attachments`, {
      method: 'POST',
      body: JSON.stringify({ fileRecordId }),
    }),

  getKnowledgeAttachments: (knowledgeId: string) =>
    request<FileMetadataDto[]>(`/api/v1/knowledge/${knowledgeId}/attachments`),

  // WorkGroupID: kc-feat-file-delete-orphan-policy-20260616-140729 — FEAT_SelfHostedFileCleanupPolicy (N6).
  // `deleteFiles` is omitted from the URL entirely when undefined so AutoCleanup/PreserveAlways
  // configs hit the param-absent server path (which resolves fail-safe). Pass true/false explicitly
  // only after a PromptUser confirmation. Self-hosted deletes the blob IMMEDIATELY when effective.
  detachFileFromKnowledge: (knowledgeId: string, fileRecordId: string, deleteFiles?: boolean) => {
    const qs = deleteFiles === undefined ? '' : `?deleteFiles=${deleteFiles}`
    return request<DetachResult>(
      `/api/v1/knowledge/${knowledgeId}/attachments/${fileRecordId}${qs}`,
      { method: 'DELETE' }
    )
  },

  // Config-backed cleanup policy — tells the client whether to prompt on detach.
  getFileCleanupPolicy: () =>
    request<FileCleanupSettings>('/api/v1/settings/file-cleanup'),

  // --- Comments/Contributions ---
  addComment: (knowledgeId: string, data: CreateCommentData) =>
    request<Comment>(`/api/v1/knowledge/${knowledgeId}/comments`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  listComments: (knowledgeId: string) =>
    request<Comment[]>(`/api/v1/knowledge/${knowledgeId}/comments`),

  updateComment: (commentId: string, data: UpdateCommentData) =>
    request<Comment>(`/api/v1/comments/${commentId}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  // WorkGroupID: kc-fix-attach-delete-transcript-20260411-080000 — FEAT_CommentDeleteAttachmentChoice.
  // `deleteFiles` defaults to false (preserve). The backend returns the CommentDeleteResult
  // envelope with counts of preserved/deleted files so the UI can show an accurate toast.
  deleteComment: (commentId: string, deleteFiles: boolean = false) =>
    request<CommentDeleteResult>(
      `/api/v1/comments/${commentId}?deleteFiles=${deleteFiles}`,
      { method: 'DELETE' }
    ),

  attachFileToComment: (commentId: string, fileRecordId: string) =>
    request<FileAttachmentDto>(`/api/v1/comments/${commentId}/attachments`, {
      method: 'POST',
      body: JSON.stringify({ fileRecordId }),
    }),

  getCommentAttachments: (commentId: string) =>
    request<FileMetadataDto[]>(`/api/v1/comments/${commentId}/attachments`),

  detachFileFromComment: (commentId: string, fileRecordId: string) =>
    request<void>(`/api/v1/comments/${commentId}/attachments/${fileRecordId}`, {
      method: 'DELETE',
    }),

  // --- Chat ---
  chat: (data: ChatRequestData) =>
    request<ChatResponseData>('/api/v1/chat', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  // --- Streaming ---
  askStream: (
    data: { question: string; vaultId?: string; researchMode?: boolean },
    onEvent: (event: SSEEvent) => void,
    signal?: AbortSignal,
  ) => streamRequest('/api/v1/ask/stream', data, onEvent, signal),

  chatStream: (
    data: ChatRequestData,
    onEvent: (event: SSEEvent) => void,
    signal?: AbortSignal,
  ) => streamRequest('/api/v1/chat/stream', data, onEvent, signal),

  // --- Health ---
  testConnection: () =>
    request<{ status: string }>('/healthz'),

  // --- Auth ---
  login: (username: string, password: string) =>
    request<MultiTenantLoginResponse>('/api/v1/auth/login', {
      method: 'POST',
      body: JSON.stringify({ username, password }),
    }).then(r => ({
      ...r,
      user: r.user ? normalizeUser(r.user) : null,
    })),

  selectTenant: (data: SelectTenantData) =>
    request<LoginResponse>('/api/v1/auth/select-tenant', {
      method: 'POST',
      body: JSON.stringify(data),
    }).then(r => ({ ...r, user: normalizeUser(r.user) })),

  switchTenant: (data: SwitchTenantData) =>
    request<LoginResponse>('/api/v1/auth/switch-tenant', {
      method: 'POST',
      body: JSON.stringify(data),
    }).then(r => ({ ...r, user: normalizeUser(r.user) })),

  getUserTenants: () =>
    request<TenantMembershipDto[]>('/api/v1/auth/tenants'),

  getMe: () =>
    request<UserDto>('/api/v1/auth/me').then(normalizeUser),

  // --- Admin: Tenants ---
  listTenants: () =>
    request<TenantDto[]>('/api/v1/admin/tenants'),

  getTenant: (id: string) =>
    request<TenantDto>(`/api/v1/admin/tenants/${id}`),

  createTenant: (data: CreateTenantData) =>
    request<TenantDto>('/api/v1/admin/tenants', {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  updateTenant: (id: string, data: UpdateTenantData) =>
    request<TenantDto>(`/api/v1/admin/tenants/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  deleteTenant: (id: string) =>
    request<{ message: string }>(`/api/v1/admin/tenants/${id}`, {
      method: 'DELETE',
    }),

  // --- Admin: Users ---
  listUsers: (tenantId?: string) =>
    request<UserDto[]>(`/api/v1/admin/users${buildQuery({ tenantId })}`).then(users => users.map(normalizeUser)),

  getUser: (id: string) =>
    request<UserDto>(`/api/v1/admin/users/${id}`).then(normalizeUser),

  createUser: (data: CreateUserData) =>
    request<UserDto>('/api/v1/admin/users', {
      method: 'POST',
      body: JSON.stringify(data),
    }).then(normalizeUser),

  updateUser: (id: string, data: UpdateUserData) =>
    request<UserDto>(`/api/v1/admin/users/${id}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }).then(normalizeUser),

  deleteUser: (id: string) =>
    request<{ message: string }>(`/api/v1/admin/users/${id}`, {
      method: 'DELETE',
    }),

  adminGenerateApiKey: (userId: string) =>
    request<{ apiKey: string }>(`/api/v1/admin/users/${userId}/generate-api-key`, {
      method: 'POST',
    }),

  resetPassword: (userId: string, data: ResetPasswordData) =>
    request<{ message: string }>(`/api/v1/admin/users/${userId}/reset-password`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  // --- Admin: User Tenant Memberships ---
  getUserMemberships: (userId: string) =>
    request<TenantMembershipDto[]>(`/api/v1/admin/users/${userId}/tenants`),

  addUserToTenant: (userId: string, data: { tenantId: string; role: number }) =>
    request<TenantMembershipDto>(`/api/v1/admin/users/${userId}/tenants`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  updateUserTenantRole: (userId: string, tenantId: string, data: { role: number }) =>
    request<TenantMembershipDto>(`/api/v1/admin/users/${userId}/tenants/${tenantId}`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  removeUserFromTenant: (userId: string, tenantId: string) =>
    request<{ message: string }>(`/api/v1/admin/users/${userId}/tenants/${tenantId}`, {
      method: 'DELETE',
    }),

  // --- Admin: Vault Access ---
  getUserPermissions: (userId: string) =>
    request<UserPermissionsDto>(`/api/v1/admin/users/${userId}/vault-access/permissions`),

  setUserPermissions: (userId: string, data: { hasAllVaultsAccess: boolean; canCreateVaults: boolean }) =>
    request<UserPermissionsDto>(`/api/v1/admin/users/${userId}/vault-access/permissions`, {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  getUserVaultAccess: (userId: string) =>
    request<UserVaultAccessDto[]>(`/api/v1/admin/users/${userId}/vault-access/`),

  grantVaultAccess: (userId: string, data: { vaultId: string; canRead?: boolean; canWrite?: boolean; canDelete?: boolean; canManage?: boolean }) =>
    request<UserVaultAccessDto>(`/api/v1/admin/users/${userId}/vault-access/`, {
      method: 'POST',
      body: JSON.stringify(data),
    }),

  revokeVaultAccess: (userId: string, vaultId: string) =>
    request<{ message: string }>(`/api/v1/admin/users/${userId}/vault-access/${vaultId}`, {
      method: 'DELETE',
    }),

  batchSetVaultAccess: (userId: string, grants: VaultAccessGrant[]) =>
    request<UserVaultAccessDto[]>(`/api/v1/admin/users/${userId}/vault-access/batch`, {
      method: 'POST',
      body: JSON.stringify({ grants }),
    }),

  // --- Account Profile ---
  updateProfile: (displayName: string | null, email: string | null) =>
    request<{ message: string; user: UserDto }>('/api/v1/account/profile', {
      method: 'PUT',
      body: JSON.stringify({ displayName, email }),
    }),

  changePassword: (currentPassword: string, newPassword: string) =>
    request<import('./types').ChangePasswordResponse>('/api/v1/account/change-password', {
      method: 'POST',
      body: JSON.stringify({ currentPassword, newPassword }),
    }),

  // --- User Preferences ---
  getUserPreferences: () =>
    request<UserPreferencesDto>('/api/v1/account/preferences'),

  updateUserPreferences: (req: UpdateUserPreferencesRequest) =>
    request<UserPreferencesDto>('/api/v1/account/preferences', {
      method: 'PATCH',
      body: JSON.stringify(req),
    }),

  // --- Personal API Key ---
  getApiKeyStatus: () =>
    request<{ hasKey: boolean; maskedKey: string | null }>('/api/v1/account/api-key'),

  generateApiKey: () =>
    request<{ apiKey: string }>('/api/v1/account/api-key', {
      method: 'POST',
    }),

  revokeApiKey: () =>
    request<{ message: string }>('/api/v1/account/api-key', {
      method: 'DELETE',
    }),

  // --- Admin: Configuration ---
  getConfigCategories: () =>
    request<ConfigCategoryDto[]>('/api/v1/admin/config/categories'),

  getConfigCategory: (category: string) =>
    request<ConfigCategoryDto>(`/api/v1/admin/config/${category}`),

  updateConfigCategory: (category: string, entries: ConfigEntryUpdateDto[]) =>
    request<ConfigUpdateResult>(`/api/v1/admin/config/${category}`, {
      method: 'PUT',
      body: JSON.stringify({ entries }),
    }),

  testConfigHealth: (category: string) =>
    request<ServiceHealthResult>(`/api/v1/admin/config/health/${category}`, {
      method: 'POST',
    }),

  testAllConfigHealth: () =>
    request<ServiceHealthResult[]>('/api/v1/admin/config/health', {
      method: 'POST',
    }),

  getConfigStatus: () =>
    request<DeploymentStatusDto>('/api/v1/admin/config/status'),

  // --- Portability ---
  exportData: () =>
    request<unknown>('/api/v1/portability/export'),

  validateImport: (pkg: unknown) =>
    request<unknown>('/api/v1/portability/import/validate', {
      method: 'POST',
      body: JSON.stringify(pkg),
    }),

  importData: (pkg: unknown, strategy: string) =>
    request<unknown>(`/api/v1/portability/import?strategy=${strategy}`, {
      method: 'POST',
      body: JSON.stringify(pkg),
    }),

  exportZip: () =>
    requestBlob('/api/v1/portability/export?mode=full'),

  importZip: (file: File, strategy: string) => {
    const form = new FormData()
    form.append('file', file)
    return requestUpload<unknown>(`/api/v1/portability/import/zip?strategy=${strategy}`, form)
  },

  getSchema: () =>
    request<{ version: number; minReadableVersion: number; compatibility: string }>('/api/v1/portability/schema'),

  // --- SSO Configuration ---
  getSSOConfig: () =>
    request<SelfHostedSSOConfigDto>('/api/v1/sso/config'),

  updateSSOConfig: (data: SelfHostedSSOConfigRequest) =>
    request<SelfHostedSSOConfigDto>('/api/v1/sso/config', {
      method: 'PUT',
      body: JSON.stringify(data),
    }),

  deleteSSOConfig: () =>
    request<{ message: string }>('/api/v1/sso/config', {
      method: 'DELETE',
    }),

  testSSOConnection: () =>
    request<SelfHostedSSOTestResultDto>('/api/v1/sso/config/test', {
      method: 'POST',
    }),

  getSSOMode: () =>
    request<{ mode: string }>('/api/v1/sso/config/mode'),

  // --- SSO Auth Flow ---
  getSSOProviders: () =>
    request<{ success: boolean; data: SSOProviderInfo[] }>('/api/v1/auth/sso/providers'),

  getSSOAuthorizeUrl: (provider: string, redirectUri: string) =>
    request<{ success: boolean; data: { authorizationUrl: string; state: string } }>(
      `/api/v1/auth/sso/authorize?provider=${provider}&redirectUri=${encodeURIComponent(redirectUri)}`,
    ),

  ssoCallback: (code: string, state: string) =>
    request<{ success: boolean; data: { token: string; expiresAt: string; email: string | null; displayName: string | null; wasAutoProvisioned: boolean } }>(
      '/api/v1/auth/sso/callback',
      { method: 'POST', body: JSON.stringify({ code, state }) },
    ),

  // --- Prompts ---
  getUserPrompts: () =>
    request<PromptTemplateDto[]>('/api/v1/prompts/user'),

  upsertUserPrompt: (key: string, templateText: string) =>
    request<PromptTemplateDto>(`/api/v1/prompts/user/${key}`, {
      method: 'PUT',
      body: JSON.stringify({ templateText }),
    }),

  deleteUserPrompt: (key: string) =>
    request<void>(`/api/v1/prompts/user/${key}`, {
      method: 'DELETE',
    }),

  getPlatformPrompts: () =>
    request<PromptTemplateDto[]>('/api/v1/prompts/platform'),

  updatePlatformPrompt: (key: string, templateText: string) =>
    request<PromptTemplateDto>(`/api/v1/prompts/platform/${key}`, {
      method: 'PUT',
      body: JSON.stringify({ templateText }),
    }),

  resetPlatformPrompt: (key: string) =>
    request<PromptTemplateDto>(`/api/v1/prompts/platform/${key}/reset`, {
      method: 'POST',
    }),

  // --- Enrichment Status ---
  getEnrichmentStatus: (knowledgeId: string) =>
    request<{ status: string; updatedAt?: string }>(`/api/v1/knowledge/${knowledgeId}/enrichment-status`),

  // --- Knowledge Versioning ---
  getVersionHistory: (knowledgeId: string) =>
    request<KnowledgeVersion[]>(`/api/v1/knowledge/${knowledgeId}/versions`),

  getVersion: (knowledgeId: string, versionNumber: number) =>
    request<KnowledgeVersion>(`/api/v1/knowledge/${knowledgeId}/versions/${versionNumber}`),

  restoreVersion: (knowledgeId: string, versionNumber: number) =>
    request<void>(`/api/v1/knowledge/${knowledgeId}/versions/${versionNumber}/restore`, {
      method: 'POST',
    }),

  // --- Commit History ---
  getKnowledgeCommitHistory: (
    vaultId: string,
    knowledgeId: string,
    page: number = 1,
    pageSize: number = 20,
  ) =>
    request<CommitHistoryResponse>(
      `/api/v1/vaults/${vaultId}/knowledge/${knowledgeId}/commit-history${buildQuery({
        page: String(page),
        pageSize: String(pageSize),
      })}`,
    ),

  // --- Audit Logs ---
  getAuditLogs: (params?: { entityId?: string; entityType?: string; page?: number; pageSize?: number }) =>
    request<PaginatedResult<AuditLogEntry>>(
      `/api/v1/audit-logs${buildQuery({
        entityId: params?.entityId,
        entityType: params?.entityType,
        page: params?.page != null ? String(params.page) : undefined,
        pageSize: params?.pageSize != null ? String(params.pageSize) : undefined,
      })}`,
    ),

  // --- Git Sync ---
  getGitSyncStatus: (vaultId: string) =>
    request<GitSyncStatus>(`/api/v1/vaults/${vaultId}/git-sync`),

  configureGitSync: (vaultId: string, config: { repositoryUrl: string; branch: string; pat?: string; filePatterns?: string; trackCommitHistory?: boolean; commitHistoryDepth?: number }) =>
    request<GitSyncStatus>(`/api/v1/vaults/${vaultId}/git-sync`, {
      method: 'PUT',
      body: JSON.stringify(config),
    }),

  triggerGitSync: (vaultId: string) =>
    request<void>(`/api/v1/vaults/${vaultId}/git-sync/trigger`, {
      method: 'POST',
    }),

  getGitSyncHistory: (vaultId: string) =>
    request<GitSyncHistoryEntry[]>(`/api/v1/vaults/${vaultId}/git-sync/history`),

  removeGitSync: (vaultId: string) =>
    request<void>(`/api/v1/vaults/${vaultId}/git-sync`, {
      method: 'DELETE',
    }),

  // --- Knowz Cloud connect: Connection ---
  getPlatformConnection: () =>
    request<PlatformConnectionDto>('/api/v1/sync/connection'),

  upsertPlatformConnection: (body: UpsertPlatformConnectionRequest) =>
    request<PlatformConnectionDto>('/api/v1/sync/connection', {
      method: 'PUT',
      body: JSON.stringify(body),
    }),

  deletePlatformConnection: () =>
    request<void>('/api/v1/sync/connection', {
      method: 'DELETE',
    }),

  testPlatformConnection: () =>
    request<PlatformConnectionTestResult>('/api/v1/sync/connection/test', {
      method: 'POST',
    }),

  testPlatformConnectionCandidate: (platformApiUrl: string, apiKey: string) =>
    request<PlatformConnectionTestResult>('/api/v1/sync/connection/test-candidate', {
      method: 'POST',
      body: JSON.stringify({ platformApiUrl, apiKey }),
    }),

  // --- Knowz Cloud connect: Browsing ---
  listPlatformVaults: () =>
    request<PlatformVaultListDto>('/api/v1/sync/platform/vaults'),

  listPlatformKnowledge: (
    vaultId: string,
    page: number = 1,
    pageSize: number = 25,
    search?: string,
  ) =>
    request<PlatformKnowledgeListDto>(
      `/api/v1/sync/platform/vaults/${vaultId}/knowledge${buildQuery({
        page: String(page),
        pageSize: String(pageSize),
        search,
      })}`,
    ),

  getPlatformKnowledge: (knowledgeId: string) =>
    request<PlatformKnowledgeDetailDto>(`/api/v1/sync/platform/knowledge/${knowledgeId}`),

  // --- Knowz Cloud connect: Links & Item Ops ---
  listSyncLinks: () =>
    request<VaultSyncStatusDto[]>('/api/v1/sync/links'),

  removeSyncLink: (localVaultId: string) =>
    request<void>(`/api/v1/sync/links/${localVaultId}`, {
      method: 'DELETE',
    }),

  runSyncLink: (localVaultId: string, direction: SyncDirection = 'Full') =>
    request<VaultSyncResult>(`/api/v1/sync/run/${localVaultId}`, {
      method: 'POST',
      body: JSON.stringify({ direction }),
    }),

  pullPlatformItem: (linkId: string, knowledgeId: string, overwriteLocal: boolean) =>
    request<SyncItemResult>(`/api/v1/sync/links/${linkId}/pull-item`, {
      method: 'POST',
      body: JSON.stringify({ knowledgeId, overwriteLocal }),
    }),

  pushPlatformItem: (linkId: string, knowledgeId: string) =>
    request<SyncItemResult>(`/api/v1/sync/links/${linkId}/push-item`, {
      method: 'POST',
      body: JSON.stringify({ knowledgeId, overwriteLocal: false }),
    }),

  // --- Knowz Cloud connect: History ---
  getPlatformSyncHistory: (
    page: number = 1,
    pageSize: number = 50,
    vaultSyncLinkId?: string,
  ) =>
    request<PlatformSyncRunDto[]>(
      `/api/v1/sync/history${buildQuery({
        page: String(page),
        pageSize: String(pageSize),
        vaultSyncLinkId,
      })}`,
    ),
}
