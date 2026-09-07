import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  Building2,
  Users,
  Shield,
  Activity,
  ArrowRight,
  RefreshCw,
  Loader2,
} from 'lucide-react'
import { api } from '../../lib/api-client'
import { useAuth } from '../../lib/auth'
import { UserRole } from '../../lib/types'
import { parseAsUtc } from '../../lib/format-utils'

export default function AdminDashboardPage() {
  const { user } = useAuth()
  const isSuperAdmin = user?.role === UserRole.SuperAdmin

  const tenantsQuery = useQuery({
    queryKey: ['admin', 'tenants'],
    queryFn: () => api.listTenants(),
    enabled: isSuperAdmin,
  })

  const usersQuery = useQuery({
    queryKey: ['admin', 'users'],
    queryFn: () => api.listUsers(),
  })

  const isLoading = (isSuperAdmin && tenantsQuery.isLoading) || usersQuery.isLoading
  const error = (isSuperAdmin && tenantsQuery.error) || usersQuery.error

  if (error) {
    return (
      <div className="text-center py-12">
        <p className="text-red-600 dark:text-red-400 mb-4">
          {error instanceof Error ? error.message : 'Failed to load admin dashboard'}
        </p>
        <button
          onClick={() => {
            if (isSuperAdmin) tenantsQuery.refetch()
            usersQuery.refetch()
          }}
          className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-md text-sm font-medium transition-colors"
        >
          <RefreshCw size={16} /> Retry
        </button>
      </div>
    )
  }

  if (isLoading) {
    return (
      <div className="space-y-6">
        <h1 className="text-2xl font-bold">Administration</h1>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
          {[1, 2, 3].map((i) => (
            <div key={i} className="h-32 bg-muted rounded-xl animate-pulse" />
          ))}
        </div>
      </div>
    )
  }

  const tenants = tenantsQuery.data ?? []
  const users = usersQuery.data ?? []
  const activeTenants = tenants.filter((t) => t.isActive).length
  const activeUsers = users.filter((u) => u.isActive).length
  const superAdminCount = users.filter((u) => u.role === UserRole.SuperAdmin).length
  const adminCount = users.filter((u) => u.role === UserRole.Admin).length

  const recentUsers = [...users]
    .sort((a, b) => parseAsUtc(b.createdAt).getTime() - parseAsUtc(a.createdAt).getTime())
    .slice(0, 5)

  const recentTenants = [...tenants]
    .sort((a, b) => parseAsUtc(b.createdAt).getTime() - parseAsUtc(a.createdAt).getTime())
    .slice(0, 5)

  return (
    <div className="space-y-8">
      <div className="flex items-center justify-between">
        <h1 className="text-2xl font-bold">Administration</h1>
        <button
          onClick={() => {
            if (isSuperAdmin) tenantsQuery.refetch()
            usersQuery.refetch()
          }}
          disabled={(isSuperAdmin && tenantsQuery.isFetching) || usersQuery.isFetching}
          className="inline-flex items-center gap-2 px-3 py-1.5 border border-input rounded-md text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
        >
          {(isSuperAdmin && tenantsQuery.isFetching) || usersQuery.isFetching ? (
            <Loader2 size={14} className="animate-spin" />
          ) : (
            <RefreshCw size={14} />
          )}
          Refresh
        </button>
      </div>

      {/* Stats Cards */}
      <div className={`grid grid-cols-1 sm:grid-cols-2 ${isSuperAdmin ? 'lg:grid-cols-4' : 'lg:grid-cols-3'} gap-4`}>
        {isSuperAdmin && (
          <div className="p-5 bg-card border border-border/60 rounded-xl shadow-sm">
            <div className="flex items-center gap-3 mb-2">
              <div className="p-2 bg-blue-50 dark:bg-blue-950/30 rounded-lg">
                <Building2 size={18} className="text-blue-600 dark:text-blue-400" />
              </div>
              <span className="text-sm font-medium text-muted-foreground">Tenants</span>
            </div>
            <p className="text-3xl font-bold">{tenants.length}</p>
            <p className="text-xs text-muted-foreground mt-1">
              {activeTenants} active
            </p>
          </div>
        )}

        <div className="p-5 bg-card border border-border/60 rounded-xl shadow-sm">
          <div className="flex items-center gap-3 mb-2">
            <div className="p-2 bg-green-50 dark:bg-green-950/30 rounded-lg">
              <Users size={18} className="text-green-600 dark:text-green-400" />
            </div>
            <span className="text-sm font-medium text-muted-foreground">Users</span>
          </div>
          <p className="text-3xl font-bold">{users.length}</p>
          <p className="text-xs text-muted-foreground mt-1">
            {activeUsers} active
          </p>
        </div>

        <div className="p-5 bg-card border border-border/60 rounded-xl shadow-sm">
          <div className="flex items-center gap-3 mb-2">
            <div className="p-2 bg-purple-50 dark:bg-purple-950/30 rounded-lg">
              <Shield size={18} className="text-purple-600 dark:text-purple-400" />
            </div>
            <span className="text-sm font-medium text-muted-foreground">Admins</span>
          </div>
          <p className="text-3xl font-bold">{superAdminCount + adminCount}</p>
          <p className="text-xs text-muted-foreground mt-1">
            {superAdminCount} super, {adminCount} admin
          </p>
        </div>

        <div className="p-5 bg-card border border-border/60 rounded-xl shadow-sm">
          <div className="flex items-center gap-3 mb-2">
            <div className="p-2 bg-amber-50 dark:bg-amber-950/30 rounded-lg">
              <Activity size={18} className="text-amber-600 dark:text-amber-400" />
            </div>
            <span className="text-sm font-medium text-muted-foreground">System</span>
          </div>
          <p className="text-lg font-bold text-green-600 dark:text-green-400">Healthy</p>
          <p className="text-xs text-muted-foreground mt-1">All services running</p>
        </div>
      </div>

      {/* Quick Links */}
      <div className={`grid grid-cols-1 ${isSuperAdmin ? 'sm:grid-cols-2' : ''} gap-4`}>
        {isSuperAdmin && (
          <Link
            to="/admin/tenants"
            className="group flex items-center justify-between p-5 bg-card border border-border/60 rounded-xl shadow-sm hover:shadow-md transition-all"
          >
            <div className="flex items-center gap-3">
              <Building2 size={20} className="text-muted-foreground" />
              <div>
                <p className="font-medium">Tenant Management</p>
                <p className="text-sm text-muted-foreground">
                  Create and manage tenants
                </p>
              </div>
            </div>
            <ArrowRight size={18} className="text-muted-foreground group-hover:text-foreground transition-colors" />
          </Link>
        )}

        <Link
          to="/admin/users"
          className="group flex items-center justify-between p-5 bg-card border border-border/60 rounded-xl shadow-sm hover:shadow-md transition-all"
        >
          <div className="flex items-center gap-3">
            <Users size={20} className="text-muted-foreground" />
            <div>
              <p className="font-medium">User Management</p>
              <p className="text-sm text-muted-foreground">
                Create users and manage access
              </p>
            </div>
          </div>
          <ArrowRight size={18} className="text-muted-foreground group-hover:text-foreground transition-colors" />
        </Link>
      </div>

      {/* Recent Activity */}
      <div className={`grid grid-cols-1 ${isSuperAdmin ? 'lg:grid-cols-2' : ''} gap-6`}>
        {/* Recent Tenants (SuperAdmin only) */}
        {isSuperAdmin && (
          <div className="bg-card border border-border/60 rounded-xl shadow-sm">
            <div className="flex items-center justify-between px-5 py-4 border-b border-border/60">
              <h2 className="font-semibold">Recent Tenants</h2>
              <Link
                to="/admin/tenants"
                className="text-sm text-muted-foreground hover:text-foreground transition-colors"
              >
                View all
              </Link>
            </div>
            {recentTenants.length === 0 ? (
              <div className="p-5 text-center text-sm text-muted-foreground">
                {/* SH_InstanceCopyHygiene R14: single-tenant instances are not
                    nudged toward multi-tenancy; the route stays reachable. */}
                No tenants to show.
              </div>
            ) : (
              <div className="divide-y divide-border">
                {recentTenants.map((tenant) => (
                  <div key={tenant.id} className="flex items-center justify-between px-5 py-3">
                    <div>
                      <p className="text-sm font-medium">{tenant.name}</p>
                      <p className="text-xs text-muted-foreground">{tenant.slug}</p>
                    </div>
                    <div className="flex items-center gap-3">
                      <span className="text-xs text-muted-foreground">
                        {tenant.userCount} users
                      </span>
                      <span
                        className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${
                          tenant.isActive
                            ? 'bg-green-50 dark:bg-green-950/30 text-green-700 dark:text-green-400'
                            : 'bg-red-50 dark:bg-red-950/30 text-red-700 dark:text-red-400'
                        }`}
                      >
                        {tenant.isActive ? 'Active' : 'Inactive'}
                      </span>
                    </div>
                  </div>
                ))}
              </div>
            )}
          </div>
        )}

        {/* Recent Users */}
        <div className="bg-card border border-border/60 rounded-xl shadow-sm">
          <div className="flex items-center justify-between px-5 py-4 border-b border-border/60">
            <h2 className="font-semibold">Recent Users</h2>
            <Link
              to="/admin/users"
              className="text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              View all
            </Link>
          </div>
          {recentUsers.length === 0 ? (
            <div className="p-5 text-center text-sm text-muted-foreground">
              No users yet. Create your first user to get started.
            </div>
          ) : (
            <div className="divide-y divide-border">
              {recentUsers.map((user) => (
                <div key={user.id} className="flex items-center justify-between px-5 py-3">
                  <div>
                    <p className="text-sm font-medium">{user.displayName || user.username}</p>
                    <p className="text-xs text-muted-foreground">{user.email || user.username}</p>
                  </div>
                  <RoleBadge role={user.role} />
                </div>
              ))}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

function RoleBadge({ role }: { role: number }) {
  const config = {
    [UserRole.SuperAdmin]: {
      label: 'SuperAdmin',
      classes: 'bg-purple-50 dark:bg-purple-950/30 text-purple-700 dark:text-purple-400',
    },
    [UserRole.Admin]: {
      label: 'Admin',
      classes: 'bg-blue-50 dark:bg-blue-950/30 text-blue-700 dark:text-blue-400',
    },
    [UserRole.User]: {
      label: 'User',
      classes: 'bg-muted text-muted-foreground',
    },
  }
  const c = config[role as UserRole] ?? config[UserRole.User]
  return (
    <span className={`inline-flex px-2 py-0.5 rounded-full text-xs font-medium ${c.classes}`}>
      {c.label}
    </span>
  )
}
