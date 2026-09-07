import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Plus,
  Search,
  Pencil,
  Trash2,
  Key,
  Lock,
  X,
  Loader2,
  Users,
  AlertCircle,
  CheckCircle,
  Copy,
  Check,
  Eye,
  EyeOff,
  Shield,
  Building2,
} from 'lucide-react'
import { api } from '../../lib/api-client'
import { useAuth } from '../../lib/auth'
import { UserRole } from '../../lib/types'
import type {
  UserDto,
  TenantDto,
  CreateUserData,
  UpdateUserData,
} from '../../lib/types'
import { useFormatters } from '../../hooks/useFormatters'

const roleLabels: Record<number, string> = {
  [UserRole.SuperAdmin]: 'SuperAdmin',
  [UserRole.Admin]: 'Admin',
  [UserRole.User]: 'User',
}

const roleStyles: Record<number, string> = {
  [UserRole.SuperAdmin]: 'bg-purple-50 dark:bg-purple-950/30 text-purple-700 dark:text-purple-400',
  [UserRole.Admin]: 'bg-blue-50 dark:bg-blue-950/30 text-blue-700 dark:text-blue-400',
  [UserRole.User]: 'bg-muted text-muted-foreground',
}

export default function UsersPage() {
  const { user: currentUser } = useAuth()
  const fmt = useFormatters()
  const isSuperAdmin = currentUser?.role === UserRole.SuperAdmin
  const queryClient = useQueryClient()
  const [searchTerm, setSearchTerm] = useState('')
  const [filterTenantId, setFilterTenantId] = useState('')
  const [showCreateModal, setShowCreateModal] = useState(false)
  const [editUser, setEditUser] = useState<UserDto | null>(null)
  const [deleteUser, setDeleteUser] = useState<UserDto | null>(null)
  const [apiKeyModal, setApiKeyModal] = useState<{ userId: string; username: string } | null>(null)
  const [resetPasswordModal, setResetPasswordModal] = useState<{ userId: string; username: string } | null>(null)
  const [vaultAccessModal, setVaultAccessModal] = useState<{ userId: string; username: string } | null>(null)
  const [tenantMembershipsModal, setTenantMembershipsModal] = useState<{ userId: string; username: string } | null>(null)
  const [toast, setToast] = useState<{ type: 'success' | 'error'; message: string } | null>(null)

  const showToast = (type: 'success' | 'error', message: string) => {
    setToast({ type, message })
    setTimeout(() => setToast(null), 3000)
  }

  const usersQuery = useQuery({
    queryKey: ['admin', 'users', filterTenantId],
    queryFn: () => api.listUsers(filterTenantId || undefined),
  })

  const tenantsQuery = useQuery({
    queryKey: ['admin', 'tenants'],
    queryFn: () => api.listTenants(),
    enabled: isSuperAdmin,
  })

  const createMutation = useMutation({
    mutationFn: (data: CreateUserData) => api.createUser(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'tenants'] })
      setShowCreateModal(false)
      showToast('success', 'User created successfully.')
    },
    onError: (err) => {
      showToast('error', err instanceof Error ? err.message : 'Failed to create user.')
    },
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateUserData }) =>
      api.updateUser(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })
      setEditUser(null)
      showToast('success', 'User updated successfully.')
    },
    onError: (err) => {
      showToast('error', err instanceof Error ? err.message : 'Failed to update user.')
    },
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.deleteUser(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })
      queryClient.invalidateQueries({ queryKey: ['admin', 'tenants'] })
      setDeleteUser(null)
      showToast('success', 'User deleted successfully.')
    },
    onError: (err) => {
      showToast('error', err instanceof Error ? err.message : 'Failed to delete user.')
    },
  })

  const users = usersQuery.data ?? []
  const tenants = tenantsQuery.data ?? []

  const filteredUsers = users.filter(
    (u) =>
      u.username.toLowerCase().includes(searchTerm.toLowerCase()) ||
      (u.email?.toLowerCase().includes(searchTerm.toLowerCase()) ?? false) ||
      (u.displayName?.toLowerCase().includes(searchTerm.toLowerCase()) ?? false),
  )

  return (
    <div className="space-y-6">
      {/* Toast */}
      {toast && (
        <div
          className={`fixed top-4 right-4 z-50 flex items-center gap-2 px-4 py-3 rounded-lg shadow-lg text-sm font-medium ${
            toast.type === 'success'
              ? 'bg-green-50 dark:bg-green-950 text-green-800 dark:text-green-200 border border-green-200 dark:border-green-800'
              : 'bg-red-50 dark:bg-red-950 text-red-800 dark:text-red-200 border border-red-200 dark:border-red-800'
          }`}
        >
          {toast.type === 'success' ? <CheckCircle size={16} /> : <AlertCircle size={16} />}
          {toast.message}
        </div>
      )}

      {/* Header */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <h1 className="text-2xl font-bold">Users</h1>
        <button
          onClick={() => setShowCreateModal(true)}
          className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors"
        >
          <Plus size={16} /> Create User
        </button>
      </div>

      {/* Filters */}
      <div className="flex flex-col sm:flex-row gap-3">
        <div className="relative flex-1">
          <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
          <input
            type="text"
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
            placeholder="Search by username, email, or name..."
            className="w-full pl-9 pr-3 py-2 border border-input rounded-md bg-card text-sm placeholder-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
          />
        </div>
        {isSuperAdmin && (
          <select
            value={filterTenantId}
            onChange={(e) => setFilterTenantId(e.target.value)}
            className="px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
          >
            <option value="">All tenants</option>
            {tenants.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        )}
      </div>

      {/* Table */}
      {usersQuery.isLoading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="h-16 bg-muted rounded-lg animate-pulse" />
          ))}
        </div>
      ) : filteredUsers.length === 0 ? (
        <div className="text-center py-16 bg-card border border-border/60 rounded-xl shadow-sm">
          <Users size={40} className="mx-auto text-muted-foreground/30 mb-3" />
          <p className="text-muted-foreground text-sm">
            {searchTerm || filterTenantId
              ? 'No users match your filters.'
              : 'No users yet. Create your first user to get started.'}
          </p>
        </div>
      ) : (
        <div className="bg-card border border-border/60 rounded-xl shadow-sm overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-border/60 bg-muted">
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Username</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Email</th>
                  {isSuperAdmin && <th className="text-left px-5 py-3 font-medium text-muted-foreground">Tenant</th>}
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Role</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Status</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">API Key</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Created</th>
                  <th className="text-right px-5 py-3 font-medium text-muted-foreground">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {filteredUsers.map((user, idx) => (
                  <tr
                    key={user.id}
                    className={`hover:bg-muted transition-colors ${
                      idx % 2 === 1 ? 'bg-muted/50' : ''
                    }`}
                  >
                    <td className="px-5 py-3">
                      <div>
                        <p className="font-medium">{user.username}</p>
                        {user.displayName && (
                          <p className="text-xs text-muted-foreground">{user.displayName}</p>
                        )}
                      </div>
                    </td>
                    <td className="px-5 py-3 text-muted-foreground">
                      {user.email || '-'}
                    </td>
                    {isSuperAdmin && (
                      <td className="px-5 py-3 text-muted-foreground">
                        {user.tenantName || '-'}
                      </td>
                    )}
                    <td className="px-5 py-3">
                      <span className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${roleStyles[user.role] ?? roleStyles[UserRole.User]}`}>
                        {roleLabels[user.role] ?? 'User'}
                      </span>
                    </td>
                    <td className="px-5 py-3">
                      <span
                        className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${
                          user.isActive
                            ? 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400'
                            : 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400'
                        }`}
                      >
                        {user.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    <td className="px-5 py-3">
                      {user.apiKey ? (
                        <code className="text-xs bg-muted px-1.5 py-0.5 rounded text-muted-foreground">
                          {user.apiKey.slice(0, 8)}...
                        </code>
                      ) : (
                        <span className="text-xs text-muted-foreground">None</span>
                      )}
                    </td>
                    <td className="px-5 py-3 text-muted-foreground">
                      {fmt.date(user.createdAt)}
                    </td>
                    <td className="px-5 py-3">
                      {(isSuperAdmin || user.role < UserRole.Admin) && (
                        <div className="flex items-center justify-end gap-1">
                          <button
                            onClick={() => setVaultAccessModal({ userId: user.id, username: user.username })}
                            className="p-1.5 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
                            title="Vault Access"
                            aria-label="Manage vault access"
                          >
                            <Shield size={14} />
                          </button>
                          {isSuperAdmin && (
                            <button
                              onClick={() => setTenantMembershipsModal({ userId: user.id, username: user.username })}
                              className="p-1.5 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
                              title="Tenant Memberships"
                              aria-label="Manage tenant memberships"
                            >
                              <Building2 size={14} />
                            </button>
                          )}
                          <button
                            onClick={() => setApiKeyModal({ userId: user.id, username: user.username })}
                            className="p-1.5 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
                            title="Generate API Key"
                          >
                            <Key size={14} />
                          </button>
                          <button
                            onClick={() => setResetPasswordModal({ userId: user.id, username: user.username })}
                            className="p-1.5 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
                            title="Reset Password"
                          >
                            <Lock size={14} />
                          </button>
                          <button
                            onClick={() => setEditUser(user)}
                            className="p-1.5 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
                            title="Edit user"
                          >
                            <Pencil size={14} />
                          </button>
                          <button
                            onClick={() => setDeleteUser(user)}
                            className="p-1.5 rounded hover:bg-red-50 dark:hover:bg-red-950/30 text-muted-foreground hover:text-red-600 dark:hover:text-red-400 transition-colors"
                            title="Delete user"
                          >
                            <Trash2 size={14} />
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* Create User Modal */}
      {showCreateModal && (
        <CreateUserModal
          tenants={tenants}
          isSuperAdmin={isSuperAdmin}
          callerTenantId={currentUser?.tenantId ?? ''}
          isSubmitting={createMutation.isPending}
          onClose={() => setShowCreateModal(false)}
          onSubmit={(data) => createMutation.mutate(data)}
        />
      )}

      {/* Edit User Modal */}
      {editUser && (
        <EditUserModal
          user={editUser}
          isSuperAdmin={isSuperAdmin}
          isSubmitting={updateMutation.isPending}
          onClose={() => setEditUser(null)}
          onSubmit={(data) => updateMutation.mutate({ id: editUser.id, data })}
        />
      )}

      {/* Delete User Confirmation */}
      {deleteUser && (
        <ConfirmDeleteUserModal
          username={deleteUser.username}
          isDeleting={deleteMutation.isPending}
          onClose={() => setDeleteUser(null)}
          onConfirm={() => deleteMutation.mutate(deleteUser.id)}
        />
      )}

      {/* Generate API Key Modal */}
      {apiKeyModal && (
        <GenerateApiKeyModal
          userId={apiKeyModal.userId}
          username={apiKeyModal.username}
          onClose={() => {
            setApiKeyModal(null)
            queryClient.invalidateQueries({ queryKey: ['admin', 'users'] })
          }}
        />
      )}

      {/* Reset Password Modal */}
      {resetPasswordModal && (
        <ResetPasswordModal
          userId={resetPasswordModal.userId}
          username={resetPasswordModal.username}
          onClose={() => setResetPasswordModal(null)}
        />
      )}

      {/* Vault Access Modal */}
      {vaultAccessModal && (
        <VaultAccessModal
          userId={vaultAccessModal.userId}
          username={vaultAccessModal.username}
          onClose={() => setVaultAccessModal(null)}
          showToast={showToast}
        />
      )}

      {/* Tenant Memberships Modal */}
      {tenantMembershipsModal && (
        <TenantMembershipsModal
          userId={tenantMembershipsModal.userId}
          username={tenantMembershipsModal.username}
          tenants={tenants}
          onClose={() => setTenantMembershipsModal(null)}
          showToast={showToast}
        />
      )}
    </div>
  )
}

// --- Create User Modal ---

interface CreateUserModalProps {
  tenants: TenantDto[]
  isSuperAdmin: boolean
  callerTenantId: string
  isSubmitting: boolean
  onClose: () => void
  onSubmit: (data: CreateUserData) => void
}

function CreateUserModal({ tenants, isSuperAdmin, callerTenantId, isSubmitting, onClose, onSubmit }: CreateUserModalProps) {
  const [tenantId, setTenantId] = useState(isSuperAdmin ? (tenants[0]?.id ?? '') : callerTenantId)
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [role, setRole] = useState<number>(UserRole.User)
  const [showPassword, setShowPassword] = useState(false)

  const handleSubmit = () => {
    if (!tenantId || !username.trim() || !password.trim()) return
    onSubmit({
      tenantId,
      username: username.trim(),
      password,
      email: email.trim() || undefined,
      displayName: displayName.trim() || undefined,
      role,
    })
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-md mx-4 p-6 max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-5">
          <h2 className="text-lg font-semibold">Create User</h2>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-muted text-muted-foreground transition-colors"
          >
            <X size={18} />
          </button>
        </div>

        <div className="space-y-4">
          {isSuperAdmin && (
            <div>
              <label className="block text-sm font-medium text-foreground mb-1">
                Tenant <span className="text-red-500">*</span>
              </label>
              <select
                value={tenantId}
                onChange={(e) => setTenantId(e.target.value)}
                className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
              >
                {tenants.length === 0 && <option value="">No tenants available</option>}
                {tenants.map((t) => (
                  <option key={t.id} value={t.id}>
                    {t.name}
                  </option>
                ))}
              </select>
            </div>
          )}

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Username <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={username}
              onChange={(e) => setUsername(e.target.value)}
              placeholder="e.g. john.doe"
              autoComplete="off"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
              autoFocus
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Password <span className="text-red-500">*</span>
            </label>
            <div className="relative">
              <input
                type={showPassword ? 'text' : 'password'}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                placeholder="Minimum 8 characters"
                autoComplete="new-password"
                className="w-full px-3 py-2 pr-10 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
              />
              <button
                type="button"
                onClick={() => setShowPassword(!showPassword)}
                className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-muted-foreground hover:text-foreground transition-colors"
              >
                {showPassword ? <EyeOff size={16} /> : <Eye size={16} />}
              </button>
            </div>
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Email
            </label>
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="user@example.com"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Display Name
            </label>
            <input
              type="text"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              placeholder="John Doe"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Role <span className="text-red-500">*</span>
            </label>
            <select
              value={role}
              onChange={(e) => setRole(Number(e.target.value))}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            >
              {isSuperAdmin && <option value={UserRole.Admin}>Admin</option>}
              <option value={UserRole.User}>User</option>
            </select>
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button
            onClick={onClose}
            disabled={isSubmitting}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={handleSubmit}
            disabled={isSubmitting || !tenantId || !username.trim() || !password.trim()}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {isSubmitting && <Loader2 size={14} className="animate-spin" />}
            Create User
          </button>
        </div>
      </div>
    </div>
  )
}

// --- Edit User Modal ---

interface EditUserModalProps {
  user: UserDto
  isSuperAdmin: boolean
  isSubmitting: boolean
  onClose: () => void
  onSubmit: (data: UpdateUserData) => void
}

function EditUserModal({ user, isSuperAdmin, isSubmitting, onClose, onSubmit }: EditUserModalProps) {
  const [email, setEmail] = useState(user.email ?? '')
  const [displayName, setDisplayName] = useState(user.displayName ?? '')
  const [role, setRole] = useState<number>(user.role)
  const [isActive, setIsActive] = useState(user.isActive)

  const handleSubmit = () => {
    onSubmit({
      email: email.trim() || undefined,
      displayName: displayName.trim() || undefined,
      role,
      isActive,
    })
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-md mx-4 p-6">
        <div className="flex items-center justify-between mb-5">
          <h2 className="text-lg font-semibold">Edit User: {user.username}</h2>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-muted text-muted-foreground transition-colors"
          >
            <X size={18} />
          </button>
        </div>

        <div className="space-y-4">
          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Email
            </label>
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="user@example.com"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Display Name
            </label>
            <input
              type="text"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              placeholder="John Doe"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Role
            </label>
            <select
              value={role}
              onChange={(e) => setRole(Number(e.target.value))}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            >
              {isSuperAdmin && <option value={UserRole.SuperAdmin}>SuperAdmin</option>}
              {isSuperAdmin && <option value={UserRole.Admin}>Admin</option>}
              <option value={UserRole.User}>User</option>
            </select>
          </div>

          <div className="flex items-center justify-between">
            <label className="text-sm font-medium text-foreground">Active</label>
            <button
              type="button"
              onClick={() => setIsActive(!isActive)}
              className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                isActive ? 'bg-green-600' : 'bg-muted-foreground/30'
              }`}
            >
              <span
                className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                  isActive ? 'translate-x-6' : 'translate-x-1'
                }`}
              />
            </button>
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button
            onClick={onClose}
            disabled={isSubmitting}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={handleSubmit}
            disabled={isSubmitting}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50"
          >
            {isSubmitting && <Loader2 size={14} className="animate-spin" />}
            Save Changes
          </button>
        </div>
      </div>
    </div>
  )
}

// --- Delete User Confirmation ---

interface ConfirmDeleteUserModalProps {
  username: string
  isDeleting: boolean
  onClose: () => void
  onConfirm: () => void
}

function ConfirmDeleteUserModal({ username, isDeleting, onClose, onConfirm }: ConfirmDeleteUserModalProps) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-sm mx-4 p-6">
        <div className="flex items-center gap-3 mb-4">
          <div className="p-2 bg-red-50 dark:bg-red-950/30 rounded-lg">
            <Trash2 size={18} className="text-red-600 dark:text-red-400" />
          </div>
          <h2 className="text-lg font-semibold">Delete User</h2>
        </div>

        <p className="text-sm text-muted-foreground mb-6">
          Are you sure you want to delete user <strong>{username}</strong>? This action cannot be undone.
        </p>

        <div className="flex justify-end gap-3">
          <button
            onClick={onClose}
            disabled={isDeleting}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={onConfirm}
            disabled={isDeleting}
            className="inline-flex items-center gap-2 px-4 py-2 bg-red-600 text-white rounded-md text-sm font-medium hover:bg-red-700 transition-colors disabled:opacity-50"
          >
            {isDeleting && <Loader2 size={14} className="animate-spin" />}
            Delete
          </button>
        </div>
      </div>
    </div>
  )
}

// --- Generate API Key Modal ---

interface GenerateApiKeyModalProps {
  userId: string
  username: string
  onClose: () => void
}

function GenerateApiKeyModal({ userId, username, onClose }: GenerateApiKeyModalProps) {
  const [generatedKey, setGeneratedKey] = useState<string | null>(null)
  const [copied, setCopied] = useState(false)

  const generateMutation = useMutation({
    mutationFn: () => api.adminGenerateApiKey(userId),
    onSuccess: (data) => {
      setGeneratedKey(data.apiKey)
    },
  })

  const handleCopy = async () => {
    if (!generatedKey) return
    await navigator.clipboard.writeText(generatedKey)
    setCopied(true)
    setTimeout(() => setCopied(false), 2000)
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-md mx-4 p-6">
        <div className="flex items-center justify-between mb-5">
          <h2 className="text-lg font-semibold">API Key: {username}</h2>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-muted text-muted-foreground transition-colors"
          >
            <X size={18} />
          </button>
        </div>

        {generatedKey ? (
          <div className="space-y-4">
            <div className="flex items-start gap-2 p-3 bg-amber-50 dark:bg-amber-950/30 border border-amber-200 dark:border-amber-800 rounded-lg">
              <AlertCircle size={16} className="text-amber-600 dark:text-amber-400 mt-0.5 shrink-0" />
              <p className="text-sm text-amber-700 dark:text-amber-300">
                This API key will only be shown once. Copy it now and store it securely.
              </p>
            </div>
            <div className="flex items-center gap-2">
              <code className="flex-1 p-3 bg-muted rounded-md text-sm font-mono break-all">
                {generatedKey}
              </code>
              <button
                onClick={handleCopy}
                className="p-2 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors shrink-0"
                title="Copy to clipboard"
              >
                {copied ? <Check size={18} className="text-green-600" /> : <Copy size={18} />}
              </button>
            </div>
            <div className="flex justify-end">
              <button
                onClick={onClose}
                className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors"
              >
                Done
              </button>
            </div>
          </div>
        ) : (
          <div className="space-y-4">
            <p className="text-sm text-muted-foreground">
              Generate a new API key for <strong>{username}</strong>. This will replace any existing API key.
            </p>

            {generateMutation.error && (
              <div className="flex items-start gap-2 p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded-lg">
                <AlertCircle size={16} className="text-red-600 dark:text-red-400 mt-0.5 shrink-0" />
                <p className="text-sm text-red-600 dark:text-red-400">
                  {generateMutation.error instanceof Error ? generateMutation.error.message : 'Failed to generate API key.'}
                </p>
              </div>
            )}

            <div className="flex justify-end gap-3">
              <button
                onClick={onClose}
                disabled={generateMutation.isPending}
                className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
              >
                Cancel
              </button>
              <button
                onClick={() => generateMutation.mutate()}
                disabled={generateMutation.isPending}
                className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50"
              >
                {generateMutation.isPending && <Loader2 size={14} className="animate-spin" />}
                Generate Key
              </button>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}

// --- Reset Password Modal ---

interface ResetPasswordModalProps {
  userId: string
  username: string
  onClose: () => void
}

function ResetPasswordModal({ userId, username, onClose }: ResetPasswordModalProps) {
  const [newPassword, setNewPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [success, setSuccess] = useState(false)

  const resetMutation = useMutation({
    mutationFn: () => api.resetPassword(userId, { newPassword }),
    onSuccess: () => {
      setSuccess(true)
    },
  })

  if (success) {
    return (
      <div className="fixed inset-0 z-50 flex items-center justify-center">
        <div className="fixed inset-0 bg-black/50" onClick={onClose} />
        <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-sm mx-4 p-6">
          <div className="text-center space-y-3">
            <div className="inline-flex p-3 bg-green-50 dark:bg-green-950/30 rounded-full">
              <CheckCircle size={24} className="text-green-600 dark:text-green-400" />
            </div>
            <h2 className="text-lg font-semibold">Password Reset</h2>
            <p className="text-sm text-muted-foreground">
              Password for <strong>{username}</strong> has been updated successfully.
            </p>
            <button
              onClick={onClose}
              className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors"
            >
              Done
            </button>
          </div>
        </div>
      </div>
    )
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-sm mx-4 p-6">
        <div className="flex items-center justify-between mb-5">
          <h2 className="text-lg font-semibold">Reset Password</h2>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-muted text-muted-foreground transition-colors"
          >
            <X size={18} />
          </button>
        </div>

        <div className="space-y-4">
          <p className="text-sm text-muted-foreground">
            Set a new password for <strong>{username}</strong>.
          </p>

          {resetMutation.error && (
            <div className="flex items-start gap-2 p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded-lg">
              <AlertCircle size={16} className="text-red-600 dark:text-red-400 mt-0.5 shrink-0" />
              <p className="text-sm text-red-600 dark:text-red-400">
                {resetMutation.error instanceof Error ? resetMutation.error.message : 'Failed to reset password.'}
              </p>
            </div>
          )}

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              New Password
            </label>
            <div className="relative">
              <input
                type={showPassword ? 'text' : 'password'}
                value={newPassword}
                onChange={(e) => setNewPassword(e.target.value)}
                placeholder="Minimum 8 characters"
                autoComplete="new-password"
                className="w-full px-3 py-2 pr-10 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
                autoFocus
              />
              <button
                type="button"
                onClick={() => setShowPassword(!showPassword)}
                className="absolute right-2 top-1/2 -translate-y-1/2 p-1 text-muted-foreground hover:text-foreground transition-colors"
              >
                {showPassword ? <EyeOff size={16} /> : <Eye size={16} />}
              </button>
            </div>
          </div>
        </div>

        <div className="flex justify-end gap-3 mt-6">
          <button
            onClick={onClose}
            disabled={resetMutation.isPending}
            className="px-4 py-2 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
          >
            Cancel
          </button>
          <button
            onClick={() => resetMutation.mutate()}
            disabled={resetMutation.isPending || !newPassword.trim()}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {resetMutation.isPending && <Loader2 size={14} className="animate-spin" />}
            Reset Password
          </button>
        </div>
      </div>
    </div>
  )
}

// --- Vault Access Modal ---

interface VaultAccessModalProps {
  userId: string
  username: string
  onClose: () => void
  showToast: (type: 'success' | 'error', message: string) => void
}

function VaultAccessModal({ userId, username, onClose, showToast }: VaultAccessModalProps) {
  const queryClient = useQueryClient()
  const [addVaultId, setAddVaultId] = useState('')

  const permissionsQuery = useQuery({
    queryKey: ['vault-access', 'permissions', userId],
    queryFn: () => api.getUserPermissions(userId),
  })

  const accessQuery = useQuery({
    queryKey: ['vault-access', 'list', userId],
    queryFn: () => api.getUserVaultAccess(userId),
  })

  const vaultsQuery = useQuery({
    queryKey: ['vaults'],
    queryFn: () => api.listVaults(false),
  })

  const setPermissionsMutation = useMutation({
    mutationFn: (data: { hasAllVaultsAccess: boolean; canCreateVaults: boolean }) =>
      api.setUserPermissions(userId, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['vault-access', 'permissions', userId] })
      showToast('success', 'Permissions updated.')
    },
    onError: (err) => showToast('error', err instanceof Error ? err.message : 'Failed to update permissions.'),
  })

  const grantMutation = useMutation({
    mutationFn: (vaultId: string) =>
      api.grantVaultAccess(userId, { vaultId, canRead: true, canWrite: true, canDelete: false, canManage: false }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['vault-access', 'list', userId] })
      setAddVaultId('')
      showToast('success', 'Vault access granted.')
    },
    onError: (err) => showToast('error', err instanceof Error ? err.message : 'Failed to grant access.'),
  })

  const revokeMutation = useMutation({
    mutationFn: (vaultId: string) => api.revokeVaultAccess(userId, vaultId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['vault-access', 'list', userId] })
      showToast('success', 'Vault access revoked.')
    },
    onError: (err) => showToast('error', err instanceof Error ? err.message : 'Failed to revoke access.'),
  })

  const permissions = permissionsQuery.data ?? { userId, hasAllVaultsAccess: true, canCreateVaults: true }
  const accessList = accessQuery.data ?? []
  const allVaults = vaultsQuery.data?.vaults ?? []

  // Vaults not yet granted
  const grantedVaultIds = new Set(accessList.map((a) => a.vaultId))
  const availableVaults = allVaults.filter((v) => !grantedVaultIds.has(v.id))

  const isLoading = permissionsQuery.isLoading || accessQuery.isLoading

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-lg mx-4 p-6 max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-5">
          <div className="flex items-center gap-2">
            <Shield size={18} className="text-muted-foreground" />
            <h2 className="text-lg font-semibold">Vault Access: {username}</h2>
          </div>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-muted text-muted-foreground transition-colors"
            aria-label="Close"
          >
            <X size={18} />
          </button>
        </div>

        {isLoading ? (
          <div className="space-y-3">
            {[1, 2, 3].map((i) => (
              <div key={i} className="h-10 bg-muted rounded animate-pulse" />
            ))}
          </div>
        ) : (
          <div className="space-y-6">
            {/* Global Permissions */}
            <div className="space-y-3">
              <h3 className="text-sm font-medium text-foreground">Global Permissions</h3>

              <div className="flex items-center justify-between p-3 bg-muted rounded-lg">
                <div>
                  <p className="text-sm font-medium">Access All Vaults</p>
                  <p className="text-xs text-muted-foreground">
                    User can access all vaults without specific grants
                  </p>
                </div>
                <button
                  type="button"
                  role="switch"
                  aria-checked={permissions.hasAllVaultsAccess}
                  aria-label="Access All Vaults"
                  onClick={() =>
                    setPermissionsMutation.mutate({
                      hasAllVaultsAccess: !permissions.hasAllVaultsAccess,
                      canCreateVaults: permissions.canCreateVaults,
                    })
                  }
                  disabled={setPermissionsMutation.isPending}
                  className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                    permissions.hasAllVaultsAccess ? 'bg-green-600' : 'bg-muted-foreground/30'
                  }`}
                >
                  <span
                    className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                      permissions.hasAllVaultsAccess ? 'translate-x-6' : 'translate-x-1'
                    }`}
                  />
                </button>
              </div>

              <div className="flex items-center justify-between p-3 bg-muted rounded-lg">
                <div>
                  <p className="text-sm font-medium">Can Create Vaults</p>
                  <p className="text-xs text-muted-foreground">
                    User can create new vaults
                  </p>
                </div>
                <button
                  type="button"
                  role="switch"
                  aria-checked={permissions.canCreateVaults}
                  aria-label="Can Create Vaults"
                  onClick={() =>
                    setPermissionsMutation.mutate({
                      hasAllVaultsAccess: permissions.hasAllVaultsAccess,
                      canCreateVaults: !permissions.canCreateVaults,
                    })
                  }
                  disabled={setPermissionsMutation.isPending}
                  className={`relative inline-flex h-6 w-11 items-center rounded-full transition-colors ${
                    permissions.canCreateVaults ? 'bg-green-600' : 'bg-muted-foreground/30'
                  }`}
                >
                  <span
                    className={`inline-block h-4 w-4 transform rounded-full bg-white transition-transform ${
                      permissions.canCreateVaults ? 'translate-x-6' : 'translate-x-1'
                    }`}
                  />
                </button>
              </div>
            </div>

            {/* Vault-Specific Access (only shown when not "access all") */}
            {!permissions.hasAllVaultsAccess && (
              <div className="space-y-3">
                <h3 className="text-sm font-medium text-foreground">
                  Vault-Specific Access
                </h3>

                {/* Add vault access */}
                <div className="flex gap-2">
                  <select
                    value={addVaultId}
                    onChange={(e) => setAddVaultId(e.target.value)}
                    className="flex-1 px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
                  >
                    <option value="">Select a vault to grant access...</option>
                    {availableVaults.map((v) => (
                      <option key={v.id} value={v.id}>
                        {v.name}
                      </option>
                    ))}
                  </select>
                  <button
                    onClick={() => addVaultId && grantMutation.mutate(addVaultId)}
                    disabled={!addVaultId || grantMutation.isPending}
                    className="inline-flex items-center gap-1 px-3 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                  >
                    {grantMutation.isPending ? <Loader2 size={14} className="animate-spin" /> : <Plus size={14} />}
                    Grant
                  </button>
                </div>

                {/* Access list */}
                {accessList.length === 0 ? (
                  <div className="text-center py-6 bg-muted rounded-lg">
                    <Shield size={24} className="mx-auto text-muted-foreground/30 mb-2" />
                    <p className="text-sm text-muted-foreground">
                      No vault access granted. This user cannot access any vaults.
                    </p>
                  </div>
                ) : (
                  <div className="border border-border/60 rounded-xl shadow-sm overflow-hidden">
                    <table className="w-full text-sm">
                      <thead>
                        <tr className="border-b border-border/60 bg-muted">
                          <th className="text-left px-3 py-2 font-medium text-muted-foreground">
                            Vault
                          </th>
                          <th className="text-center px-2 py-2 font-medium text-muted-foreground">
                            Read
                          </th>
                          <th className="text-center px-2 py-2 font-medium text-muted-foreground">
                            Write
                          </th>
                          <th className="text-center px-2 py-2 font-medium text-muted-foreground">
                            Delete
                          </th>
                          <th className="text-center px-2 py-2 font-medium text-muted-foreground">
                            Manage
                          </th>
                          <th className="px-2 py-2"></th>
                        </tr>
                      </thead>
                      <tbody className="divide-y divide-border">
                        {accessList.map((access) => (
                          <tr key={access.id} className="hover:bg-muted">
                            <td className="px-3 py-2 font-medium">{access.vaultName}</td>
                            <td className="text-center px-2 py-2">
                              <PermBadge value={access.canRead} />
                            </td>
                            <td className="text-center px-2 py-2">
                              <PermBadge value={access.canWrite} />
                            </td>
                            <td className="text-center px-2 py-2">
                              <PermBadge value={access.canDelete} />
                            </td>
                            <td className="text-center px-2 py-2">
                              <PermBadge value={access.canManage} />
                            </td>
                            <td className="px-2 py-2 text-right">
                              <button
                                onClick={() => revokeMutation.mutate(access.vaultId)}
                                disabled={revokeMutation.isPending}
                                className="p-1 rounded hover:bg-red-50 dark:hover:bg-red-950/30 text-muted-foreground hover:text-red-600 dark:hover:text-red-400 transition-colors"
                                title="Revoke access"
                              >
                                <Trash2 size={14} />
                              </button>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            )}

            {permissions.hasAllVaultsAccess && (
              <div className="flex items-start gap-2 p-3 bg-blue-50 dark:bg-blue-950/30 border border-blue-200 dark:border-blue-800 rounded-lg">
                <AlertCircle size={16} className="text-blue-600 dark:text-blue-400 mt-0.5 shrink-0" />
                <p className="text-sm text-blue-700 dark:text-blue-300">
                  This user has access to all vaults. Disable "Access All Vaults" to configure per-vault permissions.
                </p>
              </div>
            )}
          </div>
        )}

        <div className="flex justify-end mt-6">
          <button
            onClick={onClose}
            className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors"
          >
            Done
          </button>
        </div>
      </div>
    </div>
  )
}

// --- Tenant Memberships Modal ---

interface TenantMembershipsModalProps {
  userId: string
  username: string
  tenants: TenantDto[]
  onClose: () => void
  showToast: (type: 'success' | 'error', message: string) => void
}

function TenantMembershipsModal({ userId, username, tenants, onClose, showToast }: TenantMembershipsModalProps) {
  const queryClient = useQueryClient()
  const [addTenantId, setAddTenantId] = useState('')
  const [addRole, setAddRole] = useState<number>(UserRole.User)

  const membershipsQuery = useQuery({
    queryKey: ['admin', 'user-memberships', userId],
    queryFn: () => api.getUserMemberships(userId),
  })

  const addMutation = useMutation({
    mutationFn: () => api.addUserToTenant(userId, { tenantId: addTenantId, role: addRole }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'user-memberships', userId] })
      setAddTenantId('')
      setAddRole(UserRole.User)
      showToast('success', 'User added to tenant.')
    },
    onError: (err) => showToast('error', err instanceof Error ? err.message : 'Failed to add user to tenant.'),
  })

  const updateRoleMutation = useMutation({
    mutationFn: ({ tenantId, role }: { tenantId: string; role: number }) =>
      api.updateUserTenantRole(userId, tenantId, { role }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'user-memberships', userId] })
      showToast('success', 'Role updated.')
    },
    onError: (err) => showToast('error', err instanceof Error ? err.message : 'Failed to update role.'),
  })

  const removeMutation = useMutation({
    mutationFn: (tenantId: string) => api.removeUserFromTenant(userId, tenantId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'user-memberships', userId] })
      showToast('success', 'User removed from tenant.')
    },
    onError: (err) => showToast('error', err instanceof Error ? err.message : 'Failed to remove user from tenant.'),
  })

  const memberships = membershipsQuery.data ?? []
  const memberTenantIds = new Set(memberships.map(m => m.tenantId))
  const availableTenants = tenants.filter(t => !memberTenantIds.has(t.id))

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-lg mx-4 p-6 max-h-[90vh] overflow-y-auto">
        <div className="flex items-center justify-between mb-5">
          <div className="flex items-center gap-2">
            <Building2 size={18} className="text-muted-foreground" />
            <h2 className="text-lg font-semibold">Tenant Memberships: {username}</h2>
          </div>
          <button
            onClick={onClose}
            className="p-1 rounded hover:bg-muted text-muted-foreground transition-colors"
            aria-label="Close"
          >
            <X size={18} />
          </button>
        </div>

        {membershipsQuery.isLoading ? (
          <div className="space-y-3">
            {[1, 2, 3].map((i) => (
              <div key={i} className="h-10 bg-muted rounded animate-pulse" />
            ))}
          </div>
        ) : (
          <div className="space-y-6">
            {/* Add to tenant */}
            <div className="space-y-3">
              <h3 className="text-sm font-medium text-foreground">Add to Tenant</h3>
              <div className="flex gap-2">
                <select
                  value={addTenantId}
                  onChange={(e) => setAddTenantId(e.target.value)}
                  className="flex-1 px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
                >
                  <option value="">Select a tenant...</option>
                  {availableTenants.map((t) => (
                    <option key={t.id} value={t.id}>
                      {t.name}
                    </option>
                  ))}
                </select>
                <select
                  value={addRole}
                  onChange={(e) => setAddRole(Number(e.target.value))}
                  className="px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
                >
                  <option value={UserRole.User}>User</option>
                  <option value={UserRole.Admin}>Admin</option>
                </select>
                <button
                  onClick={() => addTenantId && addMutation.mutate()}
                  disabled={!addTenantId || addMutation.isPending}
                  className="inline-flex items-center gap-1 px-3 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
                >
                  {addMutation.isPending ? <Loader2 size={14} className="animate-spin" /> : <Plus size={14} />}
                  Add
                </button>
              </div>
            </div>

            {/* Memberships list */}
            <div className="space-y-3">
              <h3 className="text-sm font-medium text-foreground">
                Current Memberships ({memberships.length})
              </h3>

              {memberships.length === 0 ? (
                <div className="text-center py-6 bg-muted rounded-lg">
                  <Building2 size={24} className="mx-auto text-muted-foreground/30 mb-2" />
                  <p className="text-sm text-muted-foreground">
                    No tenant memberships. Add this user to a tenant above.
                  </p>
                </div>
              ) : (
                <div className="border border-border/60 rounded-xl shadow-sm overflow-hidden">
                  <table className="w-full text-sm">
                    <thead>
                      <tr className="border-b border-border/60 bg-muted">
                        <th className="text-left px-3 py-2 font-medium text-muted-foreground">Tenant</th>
                        <th className="text-left px-3 py-2 font-medium text-muted-foreground">Role</th>
                        <th className="text-center px-3 py-2 font-medium text-muted-foreground">Status</th>
                        <th className="px-3 py-2"></th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-border">
                      {memberships.map((m) => (
                        <tr key={m.tenantId} className="hover:bg-muted">
                          <td className="px-3 py-2">
                            <div>
                              <p className="font-medium">{m.tenantName}</p>
                              <p className="text-xs text-muted-foreground">{m.tenantSlug}</p>
                            </div>
                          </td>
                          <td className="px-3 py-2">
                            <select
                              value={m.role}
                              onChange={(e) =>
                                updateRoleMutation.mutate({ tenantId: m.tenantId, role: Number(e.target.value) })
                              }
                              disabled={updateRoleMutation.isPending}
                              className="px-2 py-1 border border-input rounded text-xs bg-card focus:outline-none focus:ring-1 focus:ring-ring"
                            >
                              <option value={UserRole.User}>User</option>
                              <option value={UserRole.Admin}>Admin</option>
                              <option value={UserRole.SuperAdmin}>SuperAdmin</option>
                            </select>
                          </td>
                          <td className="px-3 py-2 text-center">
                            <span
                              className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${
                                m.isActive
                                  ? 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400'
                                  : 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400'
                              }`}
                            >
                              {m.isActive ? 'Active' : 'Inactive'}
                            </span>
                          </td>
                          <td className="px-3 py-2 text-right">
                            <button
                              onClick={() => removeMutation.mutate(m.tenantId)}
                              disabled={removeMutation.isPending}
                              className="p-1 rounded hover:bg-red-50 dark:hover:bg-red-950/30 text-muted-foreground hover:text-red-600 dark:hover:text-red-400 transition-colors"
                              title="Remove from tenant"
                            >
                              <Trash2 size={14} />
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          </div>
        )}

        <div className="flex justify-end mt-6">
          <button
            onClick={onClose}
            className="px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors"
          >
            Done
          </button>
        </div>
      </div>
    </div>
  )
}

function PermBadge({ value }: { value: boolean }) {
  return value ? (
    <span className="inline-flex w-5 h-5 items-center justify-center rounded-full bg-green-100 dark:bg-green-950/30">
      <Check size={12} className="text-green-600 dark:text-green-400" />
    </span>
  ) : (
    <span className="inline-flex w-5 h-5 items-center justify-center rounded-full bg-muted">
      <X size={12} className="text-muted-foreground" />
    </span>
  )
}
