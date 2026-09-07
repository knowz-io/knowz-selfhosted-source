import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import {
  Plus,
  Search,
  Pencil,
  Trash2,
  X,
  Loader2,
  Building2,
  AlertCircle,
  CheckCircle,
} from 'lucide-react'
import { api } from '../../lib/api-client'
import type { TenantDto, CreateTenantData, UpdateTenantData } from '../../lib/types'
import { useFormatters } from '../../hooks/useFormatters'

function slugify(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
}

export default function TenantsPage() {
  const queryClient = useQueryClient()
  const fmt = useFormatters()
  const [searchTerm, setSearchTerm] = useState('')
  const [showCreateModal, setShowCreateModal] = useState(false)
  const [editTenant, setEditTenant] = useState<TenantDto | null>(null)
  const [deleteTenant, setDeleteTenant] = useState<TenantDto | null>(null)
  const [toast, setToast] = useState<{ type: 'success' | 'error'; message: string } | null>(null)

  const showToast = (type: 'success' | 'error', message: string) => {
    setToast({ type, message })
    setTimeout(() => setToast(null), 3000)
  }

  const tenantsQuery = useQuery({
    queryKey: ['admin', 'tenants'],
    queryFn: () => api.listTenants(),
  })

  const createMutation = useMutation({
    mutationFn: (data: CreateTenantData) => api.createTenant(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'tenants'] })
      setShowCreateModal(false)
      showToast('success', 'Tenant created successfully.')
    },
    onError: (err) => {
      showToast('error', err instanceof Error ? err.message : 'Failed to create tenant.')
    },
  })

  const updateMutation = useMutation({
    mutationFn: ({ id, data }: { id: string; data: UpdateTenantData }) =>
      api.updateTenant(id, data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'tenants'] })
      setEditTenant(null)
      showToast('success', 'Tenant updated successfully.')
    },
    onError: (err) => {
      showToast('error', err instanceof Error ? err.message : 'Failed to update tenant.')
    },
  })

  const deleteMutation = useMutation({
    mutationFn: (id: string) => api.deleteTenant(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['admin', 'tenants'] })
      setDeleteTenant(null)
      showToast('success', 'Tenant deleted successfully.')
    },
    onError: (err) => {
      showToast('error', err instanceof Error ? err.message : 'Failed to delete tenant.')
    },
  })

  const tenants = tenantsQuery.data ?? []
  const filteredTenants = tenants.filter(
    (t) =>
      t.name.toLowerCase().includes(searchTerm.toLowerCase()) ||
      t.slug.toLowerCase().includes(searchTerm.toLowerCase()),
  )

  return (
    <div className="space-y-6">
      {/* Toast */}
      {toast && (
        <div
          className={`fixed top-4 right-4 z-50 flex items-center gap-2 px-4 py-3 rounded-lg shadow-lg text-sm font-medium transition-all ${
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
        <h1 className="text-2xl font-bold">Tenants</h1>
        <button
          onClick={() => setShowCreateModal(true)}
          className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors"
        >
          <Plus size={16} /> Create Tenant
        </button>
      </div>

      {/* Search */}
      <div className="relative">
        <Search size={16} className="absolute left-3 top-1/2 -translate-y-1/2 text-muted-foreground" />
        <input
          type="text"
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          placeholder="Search tenants by name or slug..."
          className="w-full pl-9 pr-3 py-2 border border-input rounded-md bg-card text-sm placeholder-muted-foreground focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
        />
      </div>

      {/* Table */}
      {tenantsQuery.isLoading ? (
        <div className="space-y-3">
          {[1, 2, 3].map((i) => (
            <div key={i} className="h-16 bg-muted rounded-lg animate-pulse" />
          ))}
        </div>
      ) : filteredTenants.length === 0 ? (
        <div className="text-center py-16 bg-card border border-border/60 rounded-xl shadow-sm">
          <Building2 size={40} className="mx-auto text-muted-foreground/30 mb-3" />
          <p className="text-muted-foreground text-sm">
            {searchTerm ? 'No tenants match your search.' : 'No tenants yet. Create your first tenant to get started.'}
          </p>
        </div>
      ) : (
        <div className="bg-card border border-border/60 rounded-xl shadow-sm overflow-hidden">
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-border/60 bg-muted">
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Name</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Slug</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Users</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Status</th>
                  <th className="text-left px-5 py-3 font-medium text-muted-foreground">Created</th>
                  <th className="text-right px-5 py-3 font-medium text-muted-foreground">Actions</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-border">
                {filteredTenants.map((tenant, idx) => (
                  <tr
                    key={tenant.id}
                    className={`hover:bg-muted transition-colors ${
                      idx % 2 === 1 ? 'bg-muted/50' : ''
                    }`}
                  >
                    <td className="px-5 py-3 font-medium">{tenant.name}</td>
                    <td className="px-5 py-3 text-muted-foreground">
                      <code className="text-xs bg-muted px-1.5 py-0.5 rounded">
                        {tenant.slug}
                      </code>
                    </td>
                    <td className="px-5 py-3 text-muted-foreground">{tenant.userCount}</td>
                    <td className="px-5 py-3">
                      <span
                        className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${
                          tenant.isActive
                            ? 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400'
                            : 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400'
                        }`}
                      >
                        {tenant.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </td>
                    <td className="px-5 py-3 text-muted-foreground">
                      {fmt.date(tenant.createdAt)}
                    </td>
                    <td className="px-5 py-3">
                      <div className="flex items-center justify-end gap-1">
                        <button
                          onClick={() => setEditTenant(tenant)}
                          className="p-1.5 rounded hover:bg-muted text-muted-foreground hover:text-foreground transition-colors"
                          title="Edit tenant"
                        >
                          <Pencil size={14} />
                        </button>
                        <button
                          onClick={() => setDeleteTenant(tenant)}
                          className="p-1.5 rounded hover:bg-red-50 dark:hover:bg-red-950/30 text-muted-foreground hover:text-red-600 dark:hover:text-red-400 transition-colors"
                          title="Delete tenant"
                        >
                          <Trash2 size={14} />
                        </button>
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {/* Create Modal */}
      {showCreateModal && (
        <TenantFormModal
          title="Create Tenant"
          isSubmitting={createMutation.isPending}
          onClose={() => setShowCreateModal(false)}
          onSubmit={(data) => createMutation.mutate(data)}
        />
      )}

      {/* Edit Modal */}
      {editTenant && (
        <TenantFormModal
          title="Edit Tenant"
          tenant={editTenant}
          isSubmitting={updateMutation.isPending}
          onClose={() => setEditTenant(null)}
          onSubmit={(data) => updateMutation.mutate({ id: editTenant.id, data })}
        />
      )}

      {/* Delete Confirmation */}
      {deleteTenant && (
        <ConfirmDeleteModal
          tenantName={deleteTenant.name}
          isDeleting={deleteMutation.isPending}
          onClose={() => setDeleteTenant(null)}
          onConfirm={() => deleteMutation.mutate(deleteTenant.id)}
        />
      )}
    </div>
  )
}

// --- Tenant Form Modal ---

interface TenantFormModalProps {
  title: string
  tenant?: TenantDto
  isSubmitting: boolean
  onClose: () => void
  onSubmit: (data: CreateTenantData & { isActive?: boolean }) => void
}

function TenantFormModal({ title, tenant, isSubmitting, onClose, onSubmit }: TenantFormModalProps) {
  const [name, setName] = useState(tenant?.name ?? '')
  const [slug, setSlug] = useState(tenant?.slug ?? '')
  const [description, setDescription] = useState(tenant?.description ?? '')
  const [isActive, setIsActive] = useState(tenant?.isActive ?? true)
  const [slugManuallyEdited, setSlugManuallyEdited] = useState(!!tenant)

  const handleNameChange = (value: string) => {
    setName(value)
    if (!slugManuallyEdited) {
      setSlug(slugify(value))
    }
  }

  const handleSlugChange = (value: string) => {
    setSlug(slugify(value))
    setSlugManuallyEdited(true)
  }

  const handleSubmit = () => {
    if (!name.trim() || !slug.trim()) return
    onSubmit({
      name: name.trim(),
      slug: slug.trim(),
      description: description.trim() || undefined,
      ...(tenant ? { isActive } : {}),
    })
  }

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-md mx-4 p-6">
        <div className="flex items-center justify-between mb-5">
          <h2 className="text-lg font-semibold">{title}</h2>
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
              Name <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={name}
              onChange={(e) => handleNameChange(e.target.value)}
              placeholder="e.g. Acme Corporation"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
              autoFocus
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Slug <span className="text-red-500">*</span>
            </label>
            <input
              type="text"
              value={slug}
              onChange={(e) => handleSlugChange(e.target.value)}
              placeholder="e.g. acme-corporation"
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
            <p className="text-xs text-muted-foreground mt-1">
              URL-safe identifier. Auto-generated from name.
            </p>
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Description
            </label>
            <textarea
              value={description}
              onChange={(e) => setDescription(e.target.value)}
              placeholder="Optional description..."
              rows={3}
              className="w-full px-3 py-2 border border-input rounded-md bg-card text-sm resize-none focus:outline-none focus:ring-2 focus:ring-ring focus:border-transparent"
            />
          </div>

          {tenant && (
            <div className="flex items-center justify-between">
              <label className="text-sm font-medium text-foreground">
                Active
              </label>
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
          )}
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
            disabled={isSubmitting || !name.trim() || !slug.trim()}
            className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium hover:opacity-90 transition-colors disabled:opacity-50 disabled:cursor-not-allowed"
          >
            {isSubmitting && <Loader2 size={14} className="animate-spin" />}
            {tenant ? 'Save Changes' : 'Create Tenant'}
          </button>
        </div>
      </div>
    </div>
  )
}

// --- Delete Confirmation Modal ---

interface ConfirmDeleteModalProps {
  tenantName: string
  isDeleting: boolean
  onClose: () => void
  onConfirm: () => void
}

function ConfirmDeleteModal({ tenantName, isDeleting, onClose, onConfirm }: ConfirmDeleteModalProps) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center">
      <div className="fixed inset-0 bg-black/50" onClick={onClose} />
      <div className="relative bg-card border border-border/60 rounded-xl shadow-xl w-full max-w-sm mx-4 p-6">
        <div className="flex items-center gap-3 mb-4">
          <div className="p-2 bg-red-50 dark:bg-red-950/30 rounded-lg">
            <Trash2 size={18} className="text-red-600 dark:text-red-400" />
          </div>
          <h2 className="text-lg font-semibold">Delete Tenant</h2>
        </div>

        <p className="text-sm text-muted-foreground mb-6">
          Are you sure you want to delete <strong>{tenantName}</strong>? This action cannot be undone and will remove all associated data.
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
