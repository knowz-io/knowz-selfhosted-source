import { Routes, Route, Navigate } from 'react-router-dom'
import { AuthProvider, ProtectedRoute, FirstLoginRoute, AdminRoute, SuperAdminRoute } from './lib/auth'
import ErrorBoundary from './components/ErrorBoundary'
import { initTheme } from './lib/theme'
import { ViewModeProvider } from './contexts/ViewModeContext'

initTheme()
import Layout from './components/Layout'
import LoginPage from './pages/LoginPage'
import DashboardPage from './pages/DashboardPage'
import KnowledgeListPage from './pages/KnowledgeListPage'
import KnowledgeDetailPage from './pages/KnowledgeDetailPage'
import KnowledgeCreatePage from './pages/KnowledgeCreatePage'
import VaultListPage from './pages/VaultListPage'
import VaultDetailPage from './pages/VaultDetailPage'
import SearchPage from './pages/SearchPage'
import InboxPage from './pages/InboxPage'
import FilesPage from './pages/FilesPage'
import ChatPage from './pages/ChatPage'
import UnifiedSettingsPage from './pages/UnifiedSettingsPage'
import OrganizePage from './pages/OrganizePage'
import AdminDashboardPage from './pages/admin/AdminDashboardPage'
import TenantsPage from './pages/admin/TenantsPage'
import UsersPage from './pages/admin/UsersPage'
import AdminSettingsPage from './pages/admin/AdminSettingsPage'
import SSOSettingsPage from './pages/admin/SSOSettingsPage'
import AuditLogPage from './pages/admin/AuditLogPage'
import PlatformSyncPage from './pages/admin/PlatformSyncPage'
import LocalDecryptPage from './pages/LocalDecryptPage'
import SSOCallbackPage from './pages/SSOCallbackPage'
import FirstLoginPasswordPage from './pages/FirstLoginPasswordPage'

export default function App() {
  return (
    <ErrorBoundary>
    <AuthProvider>
    <ViewModeProvider>
      <Routes>
        {/* Public routes: no auth required */}
        <Route path="/login" element={<LoginPage />} />
        <Route path="/auth/sso/callback" element={<SSOCallbackPage />} />
        <Route
          path="/first-login"
          element={
            <FirstLoginRoute>
              <FirstLoginPasswordPage />
            </FirstLoginRoute>
          }
        />

        {/* Protected routes: require authentication */}
        <Route
          path="*"
          element={
            <ProtectedRoute>
              <Layout>
                <Routes>
                  <Route path="/" element={<DashboardPage />} />
                  <Route path="/knowledge" element={<KnowledgeListPage />} />
                  <Route path="/knowledge/new" element={<KnowledgeCreatePage />} />
                  <Route path="/knowledge/:id" element={<KnowledgeDetailPage />} />
                  <Route path="/vaults" element={<VaultListPage />} />
                  <Route path="/vaults/:id" element={<VaultDetailPage />} />
                  <Route path="/search" element={<SearchPage />} />
                  <Route path="/chat" element={<ChatPage />} />
                  <Route path="/inbox" element={<InboxPage />} />
                  <Route path="/files" element={<FilesPage />} />
                  <Route path="/settings" element={<UnifiedSettingsPage />} />
                  <Route path="/organize" element={<OrganizePage />} />
                  <Route path="/sync" element={<PlatformSyncPage />} />
                  <Route path="/unlock" element={<LocalDecryptPage />} />

                  {/* Backward-compatible redirects */}
                  <Route path="/ask" element={<Navigate to="/search?mode=ask" replace />} />
                  <Route path="/account" element={<Navigate to="/settings?tab=account" replace />} />
                  <Route path="/api-keys" element={<Navigate to="/settings?tab=api-keys" replace />} />
                  <Route path="/data" element={<Navigate to="/settings?tab=data" replace />} />
                  <Route path="/mcp-setup" element={<Navigate to="/settings?tab=mcp" replace />} />
                  <Route path="/topics" element={<Navigate to="/organize?tab=topics" replace />} />
                  <Route path="/tags" element={<Navigate to="/organize?tab=tags" replace />} />
                  <Route path="/entities" element={<Navigate to="/organize?tab=entities" replace />} />

                  {/* Admin routes: require SuperAdmin or Admin role */}
                  <Route
                    path="/admin"
                    element={
                      <AdminRoute>
                        <AdminDashboardPage />
                      </AdminRoute>
                    }
                  />
                  <Route
                    path="/admin/tenants"
                    element={
                      <SuperAdminRoute>
                        <TenantsPage />
                      </SuperAdminRoute>
                    }
                  />
                  <Route
                    path="/admin/users"
                    element={
                      <AdminRoute>
                        <UsersPage />
                      </AdminRoute>
                    }
                  />
                  <Route
                    path="/admin/audit-logs"
                    element={
                      <AdminRoute>
                        <AuditLogPage />
                      </AdminRoute>
                    }
                  />
                  <Route
                    path="/admin/settings"
                    element={
                      <SuperAdminRoute>
                        <AdminSettingsPage />
                      </SuperAdminRoute>
                    }
                  />
                  <Route
                    path="/admin/sso"
                    element={
                      <SuperAdminRoute>
                        <SSOSettingsPage />
                      </SuperAdminRoute>
                    }
                  />
                  <Route
                    path="/admin/platform-sync"
                    element={<Navigate to="/sync" replace />}
                  />

                  {/* SPA fallback */}
                  <Route path="*" element={<DashboardPage />} />
                </Routes>
              </Layout>
            </ProtectedRoute>
          }
        />
      </Routes>
    </ViewModeProvider>
    </AuthProvider>
    </ErrorBoundary>
  )
}
