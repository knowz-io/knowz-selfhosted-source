import { useState, useRef } from 'react'
import { useQuery, useMutation } from '@tanstack/react-query'
import {
  Download,
  Upload,
  FileCheck,
  Loader2,
  CheckCircle,
  XCircle,
  AlertTriangle,
  Info,
  FileArchive,
} from 'lucide-react'
import { api } from '../lib/api-client'
import SurfaceCard from '../components/ui/SurfaceCard'

type ExportMode = 'light' | 'full'
type Strategy = 'skip' | 'overwrite' | 'merge'

const strategyDescriptions: Record<Strategy, { label: string; description: string }> = {
  skip: {
    label: 'Skip Conflicts',
    description: 'Keep existing data, skip any imported items that conflict.',
  },
  overwrite: {
    label: 'Overwrite',
    description: 'Replace existing items with imported data when conflicts occur.',
  },
  merge: {
    label: 'Merge',
    description: 'Fill missing fields from imported data, keep existing non-null values.',
  },
}

export default function DataPortabilityPage() {
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [exportMode, setExportMode] = useState<ExportMode>('light')
  const [importFile, setImportFile] = useState<File | null>(null)
  const [importFileType, setImportFileType] = useState<'json' | 'zip' | null>(null)
  const [importPackage, setImportPackage] = useState<Record<string, unknown> | null>(null)
  const [strategy, setStrategy] = useState<Strategy>('skip')
  const [parseError, setParseError] = useState<string | null>(null)
  const [validationResult, setValidationResult] = useState<Record<string, unknown> | null>(null)
  const [importResult, setImportResult] = useState<Record<string, unknown> | null>(null)

  const { data: schema } = useQuery({
    queryKey: ['portability', 'schema'],
    queryFn: () => api.getSchema(),
  })

  const exportJsonMutation = useMutation({
    mutationFn: () => api.exportData(),
    onSuccess: (data) => {
      const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' })
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      const date = new Date().toISOString().split('T')[0]
      a.href = url
      a.download = `knowz-export-${date}.json`
      document.body.appendChild(a)
      a.click()
      document.body.removeChild(a)
      URL.revokeObjectURL(url)
    },
  })

  const exportZipMutation = useMutation({
    mutationFn: () => api.exportZip(),
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      const date = new Date().toISOString().split('T')[0]
      a.href = url
      a.download = `knowz-export-${date}.zip`
      document.body.appendChild(a)
      a.click()
      document.body.removeChild(a)
      URL.revokeObjectURL(url)
    },
  })

  const exportMutation = exportMode === 'full' ? exportZipMutation : exportJsonMutation

  const validateMutation = useMutation({
    mutationFn: (pkg: Record<string, unknown>) => api.validateImport(pkg),
    onSuccess: (data) => {
      setValidationResult(data as Record<string, unknown>)
    },
    onError: (err: unknown) => {
      // 422 returns validation result in body
      if (err && typeof err === 'object' && 'status' in err) {
        setValidationResult(err as Record<string, unknown>)
      }
    },
  })

  const importJsonMutation = useMutation({
    mutationFn: ({ pkg, strat }: { pkg: Record<string, unknown>; strat: string }) =>
      api.importData(pkg, strat),
    onSuccess: (data) => {
      setImportResult(data as Record<string, unknown>)
    },
  })

  const importZipMutation = useMutation({
    mutationFn: ({ file, strat }: { file: File; strat: string }) =>
      api.importZip(file, strat),
    onSuccess: (data) => {
      setImportResult(data as Record<string, unknown>)
    },
  })

  const importMutation = importFileType === 'zip' ? importZipMutation : importJsonMutation

  const handleFileSelect = async (e: React.ChangeEvent<HTMLInputElement>) => {
    const file = e.target.files?.[0]
    if (!file) return

    setImportFile(file)
    setParseError(null)
    setValidationResult(null)
    setImportResult(null)
    setImportPackage(null)
    setImportFileType(null)

    const ext = file.name.toLowerCase().split('.').pop()

    if (ext === 'zip') {
      setImportFileType('zip')
    } else if (ext === 'json') {
      setImportFileType('json')
      try {
        const text = await file.text()
        const parsed = JSON.parse(text)
        setImportPackage(parsed)
      } catch {
        setParseError('Invalid JSON file. Please select a valid Knowz export file.')
      }
    } else {
      setParseError('Unsupported file type. Please select a .json or .zip file.')
    }
  }

  const handleValidate = () => {
    if (importPackage) {
      validateMutation.mutate(importPackage)
    }
  }

  const handleImport = () => {
    if (importFileType === 'zip' && importFile) {
      importZipMutation.mutate({ file: importFile, strat: strategy })
    } else if (importPackage) {
      importJsonMutation.mutate({ pkg: importPackage, strat: strategy })
    }
  }

  const isImportReady = importFileType === 'zip' ? !!importFile : !!importPackage

  const vr = validationResult as Record<string, unknown> | null

  return (
    <div className="space-y-4 max-w-2xl">
      {/* Schema info */}
      {schema && (
        <SurfaceCard className="border-blue-200/90 bg-blue-50/80 px-3 py-2 text-sm text-blue-700 dark:border-blue-900/60 dark:bg-blue-950/20 dark:text-blue-300">
          <div className="flex items-center gap-2">
            <Info size={16} className="shrink-0" />
            <span>{schema.compatibility}</span>
          </div>
        </SurfaceCard>
      )}

      {/* Export section */}
      <SurfaceCard className="p-5">
        <h2 className="text-lg font-semibold mb-2">Export</h2>
        <p className="text-sm text-muted-foreground mb-4">
          Download all your knowledge, vaults, topics, tags, and entities.
        </p>
        <div className="flex gap-2 mb-4">
          <button
            onClick={() => setExportMode('light')}
            className={`inline-flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm font-medium border transition-colors ${
              exportMode === 'light'
                ? 'border-primary bg-primary/10 text-primary'
                : 'border-border/60 hover:bg-muted'
            }`}
          >
            <Download size={14} />
            Light (JSON)
          </button>
          <button
            onClick={() => setExportMode('full')}
            className={`inline-flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm font-medium border transition-colors ${
              exportMode === 'full'
                ? 'border-primary bg-primary/10 text-primary'
                : 'border-border/60 hover:bg-muted'
            }`}
          >
            <FileArchive size={14} />
            Full (ZIP with files)
          </button>
        </div>
        {exportMode === 'full' && (
          <p className="text-xs text-muted-foreground mb-3">
            Full export includes binary files (images, documents) in a browsable ZIP archive.
          </p>
        )}
        <button
          onClick={() => exportMutation.mutate(undefined as never)}
          disabled={exportMutation.isPending}
          className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
        >
          {exportMutation.isPending ? (
            <Loader2 size={14} className="animate-spin" />
          ) : exportMode === 'full' ? (
            <FileArchive size={14} />
          ) : (
            <Download size={14} />
          )}
          {exportMutation.isPending ? 'Exporting...' : `Export All Data (${exportMode === 'full' ? 'ZIP' : 'JSON'})`}
        </button>
        {exportMutation.isSuccess && (
          <p className="mt-2 text-sm text-green-600 dark:text-green-400 flex items-center gap-1.5">
            <CheckCircle size={14} /> Export downloaded successfully.
          </p>
        )}
        {exportMutation.isError && (
          <p className="mt-2 text-sm text-red-600 dark:text-red-400 flex items-center gap-1.5">
            <XCircle size={14} /> Export failed. Please try again.
          </p>
        )}
      </SurfaceCard>

      {/* Import section */}
      <SurfaceCard className="p-5">
        <h2 className="text-lg font-semibold mb-2">Import</h2>
        <p className="text-sm text-muted-foreground mb-4">
          Import data from a Knowz export file. Validate first to check for conflicts.
        </p>

        {/* File selection */}
        <div className="space-y-4">
          <div>
            <input
              ref={fileInputRef}
              type="file"
              accept=".json,.zip"
              onChange={handleFileSelect}
              className="hidden"
            />
            <button
              onClick={() => fileInputRef.current?.click()}
              className="inline-flex items-center gap-2 px-4 py-2 border border-input rounded-lg text-sm font-medium hover:bg-muted transition-colors"
            >
              <Upload size={14} />
              {importFile ? 'Change File' : 'Select File (.json or .zip)'}
            </button>
            {importFile && (
              <span className="ml-3 text-sm text-muted-foreground">
                {importFile.name} ({(importFile.size / 1024).toFixed(1)} KB)
              </span>
            )}
          </div>

          {parseError && (
            <p className="text-sm text-red-600 dark:text-red-400 flex items-center gap-1.5">
              <XCircle size={14} /> {parseError}
            </p>
          )}

          {/* Strategy selector */}
          {isImportReady && (
            <div>
              <label className="block text-sm font-medium mb-2">Conflict Strategy</label>
              <div className="space-y-2">
                {(Object.keys(strategyDescriptions) as Strategy[]).map((key) => (
                  <label
                    key={key}
                    className={`flex items-start gap-3 p-3 border rounded-lg cursor-pointer transition-colors duration-150 ${
                      strategy === key
                        ? 'border-primary bg-primary/5'
                        : 'border-border/60 hover:bg-muted'
                    }`}
                  >
                    <input
                      type="radio"
                      name="strategy"
                      value={key}
                      checked={strategy === key}
                      onChange={() => setStrategy(key)}
                      className="mt-0.5"
                    />
                    <div>
                      <span className="text-sm font-medium">{strategyDescriptions[key].label}</span>
                      <p className="text-xs text-muted-foreground mt-0.5">
                        {strategyDescriptions[key].description}
                      </p>
                    </div>
                  </label>
                ))}
              </div>
            </div>
          )}

          {/* Action buttons */}
          {isImportReady ? (
            <div className="flex gap-3">
              {importFileType === 'json' && (
                <button
                  onClick={handleValidate}
                  disabled={validateMutation.isPending}
                  className="inline-flex items-center gap-2 px-4 py-2 border border-input rounded-lg text-sm font-medium hover:bg-muted transition-colors disabled:opacity-50"
                >
                  {validateMutation.isPending ? (
                    <Loader2 size={14} className="animate-spin" />
                  ) : (
                    <FileCheck size={14} />
                  )}
                  Validate
                </button>
              )}
              <button
                onClick={handleImport}
                disabled={importMutation.isPending || (importFileType === 'json' && !validationResult)}
                className="inline-flex items-center gap-2 px-4 py-2 bg-primary text-primary-foreground rounded-lg text-sm font-medium hover:opacity-90 transition-opacity disabled:opacity-50"
                title={importFileType === 'json' && !validationResult ? 'Validate the file first' : undefined}
              >
                {importMutation.isPending ? (
                  <Loader2 size={14} className="animate-spin" />
                ) : (
                  <Upload size={14} />
                )}
                {importMutation.isPending ? 'Importing...' : `Import ${importFileType === 'zip' ? 'ZIP' : 'JSON'}`}
              </button>
            </div>
          ) : null}

          {/* Validation results */}
          {vr && (
            <div
              className={`border rounded-xl p-4 ${
                vr.isValid
                  ? 'border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/30'
                  : 'border-amber-200 dark:border-amber-800 bg-amber-50 dark:bg-amber-950/30'
              }`}
            >
              <div className="flex items-center gap-2 mb-3">
                {vr.isValid ? (
                  <CheckCircle size={16} className="text-green-600 dark:text-green-400" />
                ) : (
                  <AlertTriangle size={16} className="text-amber-600 dark:text-amber-400" />
                )}
                <span className="font-medium text-sm">
                  {vr.isValid ? 'Validation Passed' : 'Validation Issues'}
                </span>
              </div>
              <div className="grid grid-cols-2 gap-x-4 gap-y-1 text-sm">
                {vr.totalVaults != null && (
                  <div className="text-muted-foreground">Vaults: {vr.totalVaults as number}</div>
                )}
                {vr.totalKnowledgeItems != null && (
                  <div className="text-muted-foreground">
                    Knowledge Items: {vr.totalKnowledgeItems as number}
                  </div>
                )}
                {vr.totalTopics != null && (
                  <div className="text-muted-foreground">Topics: {vr.totalTopics as number}</div>
                )}
                {vr.totalTags != null && (
                  <div className="text-muted-foreground">Tags: {vr.totalTags as number}</div>
                )}
                {vr.totalPersons != null && (
                  <div className="text-muted-foreground">Persons: {vr.totalPersons as number}</div>
                )}
                {vr.totalInboxItems != null && (
                  <div className="text-muted-foreground">
                    Inbox Items: {vr.totalInboxItems as number}
                  </div>
                )}
              </div>
              {Array.isArray(vr.warnings) && (vr.warnings as string[]).length > 0 && (
                <div className="mt-3 space-y-1">
                  {(vr.warnings as string[]).map((w, i) => (
                    <p key={i} className="text-xs text-amber-700 dark:text-amber-400">
                      {w}
                    </p>
                  ))}
                </div>
              )}
              {Array.isArray(vr.errors) && (vr.errors as string[]).length > 0 && (
                <div className="mt-3 space-y-1">
                  {(vr.errors as string[]).map((e, i) => (
                    <p key={i} className="text-xs text-red-700 dark:text-red-400">
                      {e}
                    </p>
                  ))}
                </div>
              )}
            </div>
          )}

          {/* Import results */}
          {importResult && (
            <div
              className={`border rounded-xl p-4 ${
                (importResult as Record<string, unknown>).success
                  ? 'border-green-200 dark:border-green-800 bg-green-50 dark:bg-green-950/30'
                  : 'border-red-200 dark:border-red-800 bg-red-50 dark:bg-red-950/30'
              }`}
            >
              <div className="flex items-center gap-2 mb-3">
                {(importResult as Record<string, unknown>).success ? (
                  <CheckCircle size={16} className="text-green-600 dark:text-green-400" />
                ) : (
                  <XCircle size={16} className="text-red-600 dark:text-red-400" />
                )}
                <span className="font-medium text-sm">
                  {(importResult as Record<string, unknown>).success
                    ? 'Import Completed'
                    : 'Import Failed'}
                </span>
              </div>
              {Boolean((importResult as Record<string, unknown>).success) && (
                <div className="text-sm text-muted-foreground space-y-1">
                  <ImportCountLine label="Vaults" counts={(importResult as Record<string, unknown>).vaults} />
                  <ImportCountLine label="Knowledge" counts={(importResult as Record<string, unknown>).knowledgeItems} />
                  <ImportCountLine label="Topics" counts={(importResult as Record<string, unknown>).topics} />
                  <ImportCountLine label="Tags" counts={(importResult as Record<string, unknown>).tags} />
                  <ImportCountLine label="Persons" counts={(importResult as Record<string, unknown>).persons} />
                </div>
              )}
              {Boolean((importResult as Record<string, unknown>).error) && (
                <p className="text-sm text-red-700 dark:text-red-400">
                  {(importResult as Record<string, unknown>).error as string}
                </p>
              )}
            </div>
          )}

          {importMutation.isError && !importResult && (
            <p className="text-sm text-red-600 dark:text-red-400 flex items-center gap-1.5">
              <XCircle size={14} /> Import failed. Please try again.
            </p>
          )}
        </div>
      </SurfaceCard>
    </div>
  )
}

function ImportCountLine({ label, counts }: { label: string; counts: unknown }) {
  if (!counts || typeof counts !== 'object') return null
  const c = counts as Record<string, number>
  const parts: string[] = []
  if (c.created > 0) parts.push(`${c.created} created`)
  if (c.skipped > 0) parts.push(`${c.skipped} skipped`)
  if (c.overwritten > 0) parts.push(`${c.overwritten} overwritten`)
  if (c.merged > 0) parts.push(`${c.merged} merged`)
  if (parts.length === 0) return null
  return (
    <div>
      <span className="font-medium">{label}:</span> {parts.join(', ')}
    </div>
  )
}
