import { useState } from 'react'
import { useMutation } from '@tanstack/react-query'
import { api } from '../lib/api-client'
import { useAuth } from '../lib/auth'
import { UserCircle, Lock, Save, CheckCircle2, Clock } from 'lucide-react'
import { DEFAULT_USER_TIMEZONE } from '../hooks/useFormatters'
import SurfaceCard from '../components/ui/SurfaceCard'

const COMMON_TIMEZONES: ReadonlyArray<{ value: string; label: string }> = [
  { value: 'America/New_York', label: 'Eastern Time (New York)' },
  { value: 'America/Chicago', label: 'Central Time (Chicago)' },
  { value: 'America/Denver', label: 'Mountain Time (Denver)' },
  { value: 'America/Phoenix', label: 'Mountain Time (Phoenix, no DST)' },
  { value: 'America/Los_Angeles', label: 'Pacific Time (Los Angeles)' },
  { value: 'America/Anchorage', label: 'Alaska Time (Anchorage)' },
  { value: 'Pacific/Honolulu', label: 'Hawaii Time (Honolulu)' },
  { value: 'America/Toronto', label: 'Eastern Time (Toronto)' },
  { value: 'America/Vancouver', label: 'Pacific Time (Vancouver)' },
  { value: 'America/Mexico_City', label: 'Central Time (Mexico City)' },
  { value: 'America/Sao_Paulo', label: 'Brasilia Time (Sao Paulo)' },
  { value: 'Europe/London', label: 'UK Time (London)' },
  { value: 'Europe/Dublin', label: 'Ireland Time (Dublin)' },
  { value: 'Europe/Paris', label: 'Central European Time (Paris)' },
  { value: 'Europe/Berlin', label: 'Central European Time (Berlin)' },
  { value: 'Europe/Madrid', label: 'Central European Time (Madrid)' },
  { value: 'Europe/Rome', label: 'Central European Time (Rome)' },
  { value: 'Europe/Athens', label: 'Eastern European Time (Athens)' },
  { value: 'Europe/Moscow', label: 'Moscow Time' },
  { value: 'Africa/Cairo', label: 'Eastern European Time (Cairo)' },
  { value: 'Africa/Johannesburg', label: 'South Africa Time (Johannesburg)' },
  { value: 'Asia/Dubai', label: 'Gulf Time (Dubai)' },
  { value: 'Asia/Kolkata', label: 'India Time (Kolkata)' },
  { value: 'Asia/Singapore', label: 'Singapore Time' },
  { value: 'Asia/Hong_Kong', label: 'Hong Kong Time' },
  { value: 'Asia/Tokyo', label: 'Japan Time (Tokyo)' },
  { value: 'Asia/Seoul', label: 'Korea Time (Seoul)' },
  { value: 'Asia/Shanghai', label: 'China Time (Shanghai)' },
  { value: 'Australia/Sydney', label: 'Australian Eastern Time (Sydney)' },
  { value: 'Australia/Perth', label: 'Australian Western Time (Perth)' },
  { value: 'Pacific/Auckland', label: 'New Zealand Time (Auckland)' },
  { value: 'UTC', label: 'Coordinated Universal Time (UTC)' },
]

export default function AccountPage() {
  const { user, refreshUser } = useAuth()

  const [displayName, setDisplayName] = useState(user?.displayName ?? '')
  const [email, setEmail] = useState(user?.email ?? '')
  const [profileSuccess, setProfileSuccess] = useState(false)
  const [profileError, setProfileError] = useState<string | null>(null)

  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [passwordSuccess, setPasswordSuccess] = useState(false)
  const [passwordError, setPasswordError] = useState<string | null>(null)

  const initialTz = user?.timeZonePreference ?? DEFAULT_USER_TIMEZONE
  const isKnownTz = COMMON_TIMEZONES.some((t) => t.value === initialTz)
  const [timeZone, setTimeZone] = useState(initialTz)
  const [timeZoneMode, setTimeZoneMode] = useState<'preset' | 'custom'>(
    isKnownTz ? 'preset' : 'custom',
  )
  const [preferencesSuccess, setPreferencesSuccess] = useState(false)
  const [preferencesError, setPreferencesError] = useState<string | null>(null)

  const profileMutation = useMutation({
    mutationFn: () => api.updateProfile(displayName || null, email || null),
    onSuccess: () => {
      setProfileSuccess(true)
      setProfileError(null)
      setTimeout(() => setProfileSuccess(false), 3000)
    },
    onError: (err: Error) => {
      setProfileError(err.message)
      setProfileSuccess(false)
    },
  })

  const passwordMutation = useMutation({
    mutationFn: () => api.changePassword(currentPassword, newPassword),
    onSuccess: () => {
      setPasswordSuccess(true)
      setPasswordError(null)
      setCurrentPassword('')
      setNewPassword('')
      setConfirmPassword('')
      setTimeout(() => setPasswordSuccess(false), 3000)
    },
    onError: (err: Error) => {
      setPasswordError(err.message)
      setPasswordSuccess(false)
    },
  })

  const preferencesMutation = useMutation({
    mutationFn: (tz: string) => api.updateUserPreferences({ timeZonePreference: tz }),
    onSuccess: async () => {
      setPreferencesSuccess(true)
      setPreferencesError(null)
      await refreshUser()
      setTimeout(() => setPreferencesSuccess(false), 3000)
    },
    onError: (err: Error) => {
      setPreferencesError(err.message)
      setPreferencesSuccess(false)
    },
  })

  const handlePreferencesSave = () => {
    const trimmed = timeZone.trim()
    if (!trimmed) {
      setPreferencesError('Timezone is required.')
      return
    }
    preferencesMutation.mutate(trimmed)
  }

  const handleProfileSave = () => {
    if (!displayName && !email) {
      setProfileError('At least one field is required.')
      return
    }
    profileMutation.mutate()
  }

  const handlePasswordChange = () => {
    if (!currentPassword) {
      setPasswordError('Current password is required.')
      return
    }
    if (!newPassword) {
      setPasswordError('New password is required.')
      return
    }
    if (newPassword.length < 6) {
      setPasswordError('New password must be at least 6 characters.')
      return
    }
    if (newPassword !== confirmPassword) {
      setPasswordError('Passwords do not match.')
      return
    }
    passwordMutation.mutate()
  }

  return (
    <div className="grid max-w-5xl grid-cols-1 gap-5 lg:grid-cols-2">
      <SurfaceCard className="p-6 space-y-4">
        <div className="flex items-center gap-2 text-lg font-semibold">
          <UserCircle size={20} />
          Profile
        </div>

        <div className="space-y-3">
          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Username
            </label>
            <input
              type="text"
              value={user?.username ?? ''}
              disabled
              className="w-full px-3 py-2 text-sm border border-input rounded-md bg-muted text-muted-foreground cursor-not-allowed"
            />
            <p className="text-xs text-muted-foreground mt-1">Username cannot be changed.</p>
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Display Name
            </label>
            <input
              type="text"
              value={displayName}
              onChange={(e) => setDisplayName(e.target.value)}
              placeholder="Your display name"
              className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-1 focus:ring-ring"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Email
            </label>
            <input
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              placeholder="your@email.com"
              className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-1 focus:ring-ring"
            />
          </div>
        </div>

        {profileError && (
          <div className="p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded text-sm text-red-700 dark:text-red-400">
            {profileError}
          </div>
        )}

        {profileSuccess && (
          <div className="flex items-center gap-2 p-3 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-800 rounded text-sm text-green-700 dark:text-green-400">
            <CheckCircle2 size={16} /> Profile updated successfully.
          </div>
        )}

        <button
          onClick={handleProfileSave}
          disabled={profileMutation.isPending}
          className="inline-flex items-center gap-2 px-4 py-2 text-sm bg-primary text-primary-foreground rounded-md hover:opacity-90 transition-colors disabled:opacity-50"
        >
          <Save size={16} />
          {profileMutation.isPending ? 'Saving...' : 'Save Profile'}
        </button>
      </SurfaceCard>

      <SurfaceCard className="p-6 space-y-4">
        <div className="flex items-center gap-2 text-lg font-semibold">
          <Clock size={20} />
          Preferences
        </div>

        <div className="space-y-3">
          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Timezone
            </label>
            <p className="text-xs text-muted-foreground mb-2">
              Dates and times in the app are displayed in this timezone.
              Defaults to Eastern Time (US) if not set.
            </p>
            {timeZoneMode === 'preset' ? (
              <select
                value={timeZone}
                onChange={(e) => {
                  const val = e.target.value
                  if (val === '__custom__') {
                    setTimeZoneMode('custom')
                  } else {
                    setTimeZone(val)
                  }
                }}
                className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-1 focus:ring-ring"
              >
                {COMMON_TIMEZONES.map((tz) => (
                  <option key={tz.value} value={tz.value}>
                    {tz.label}
                  </option>
                ))}
                <option value="__custom__">Other (enter manually)</option>
              </select>
            ) : (
              <div className="space-y-2">
                <input
                  type="text"
                  value={timeZone}
                  onChange={(e) => setTimeZone(e.target.value)}
                  placeholder="e.g. America/New_York"
                  className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card font-mono focus:outline-none focus:ring-1 focus:ring-ring"
                />
                <button
                  type="button"
                  onClick={() => {
                    setTimeZoneMode('preset')
                    setTimeZone(DEFAULT_USER_TIMEZONE)
                  }}
                  className="text-xs text-muted-foreground hover:text-foreground underline"
                >
                  Choose from list instead
                </button>
              </div>
            )}
          </div>
        </div>

        {preferencesError && (
          <div className="p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded text-sm text-red-700 dark:text-red-400">
            {preferencesError}
          </div>
        )}

        {preferencesSuccess && (
          <div className="flex items-center gap-2 p-3 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-800 rounded text-sm text-green-700 dark:text-green-400">
            <CheckCircle2 size={16} /> Preferences saved.
          </div>
        )}

        <button
          onClick={handlePreferencesSave}
          disabled={preferencesMutation.isPending}
          className="inline-flex items-center gap-2 px-4 py-2 text-sm bg-primary text-primary-foreground rounded-md hover:opacity-90 transition-colors disabled:opacity-50"
        >
          <Save size={16} />
          {preferencesMutation.isPending ? 'Saving...' : 'Save Preferences'}
        </button>
      </SurfaceCard>

      <SurfaceCard className="p-6 space-y-4 lg:col-span-2">
        <div className="flex items-center gap-2 text-lg font-semibold">
          <Lock size={20} />
          Change Password
        </div>

        <div className="grid grid-cols-1 gap-3 lg:grid-cols-3">
          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Current Password
            </label>
            <input
              type="password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
              className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-1 focus:ring-ring"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              New Password
            </label>
            <input
              type="password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-1 focus:ring-ring"
            />
          </div>

          <div>
            <label className="block text-sm font-medium text-foreground mb-1">
              Confirm New Password
            </label>
            <input
              type="password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              className="w-full px-3 py-2 text-sm border border-input rounded-md bg-card focus:outline-none focus:ring-1 focus:ring-ring"
            />
          </div>
        </div>

        {passwordError && (
          <div className="p-3 bg-red-50 dark:bg-red-950/30 border border-red-200 dark:border-red-800 rounded text-sm text-red-700 dark:text-red-400">
            {passwordError}
          </div>
        )}

        {passwordSuccess && (
          <div className="flex items-center gap-2 p-3 bg-green-50 dark:bg-green-950/30 border border-green-200 dark:border-green-800 rounded text-sm text-green-700 dark:text-green-400">
            <CheckCircle2 size={16} /> Password changed successfully.
          </div>
        )}

        <button
          onClick={handlePasswordChange}
          disabled={passwordMutation.isPending}
          className="inline-flex items-center gap-2 px-4 py-2 text-sm bg-primary text-primary-foreground rounded-md hover:opacity-90 transition-colors disabled:opacity-50"
        >
          <Lock size={16} />
          {passwordMutation.isPending ? 'Changing...' : 'Change Password'}
        </button>
      </SurfaceCard>
    </div>
  )
}
