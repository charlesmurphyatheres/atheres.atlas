import { useEffect, useMemo, useRef, useState } from 'react'
import { useAuth } from '../../contexts/AuthContext'
import {
  createStore,
  getStores,
  importOrders,
  type CreateStorePayload,
  type ImportInitialStatus,
  type ImportOrdersResult,
  type ImportedOrderRow,
  type StoreLite,
} from '../../services/apiService'
import { parseCsv } from './csvParser'

// ---- Field model -----------------------------------------------------------
//
// CSV columns the user can map. The address fields (street/city/state/zip/
// county) are intentionally NOT in this list: when a row's license number
// matches an existing Store, the order takes its address from the store
// master data so route optimization gets a known-good geocoded address. When
// the license doesn't match, the operator either ignores the row or creates
// a new Store inline (with full address). Either way the CSV's address
// columns aren't used.
type FieldKey =
  | 'orderDate'
  | 'customer'
  | 'salesOrderNumber'
  | 'storeName'
  | 'purchaseOrderNumber'
  | 'licenseNumber'

interface FieldDef {
  key: FieldKey
  label: string
  required?: boolean
  // Header substrings we'll auto-match against (case-insensitive). The first
  // CSV header that matches any pattern wins. Patterns are deliberately loose
  // so spreadsheets with slightly different naming still snap into place.
  hints: string[]
}

const FIELDS: FieldDef[] = [
  { key: 'orderDate',           label: 'Order Date',       required: true,  hints: ['order date', 'orderdate', 'date'] },
  { key: 'customer',            label: 'Customer',         hints: ['customer', 'customer name'] },
  { key: 'salesOrderNumber',    label: 'Sales Order #',    hints: ['sales order', 'so #', 'so#', 'sales order number'] },
  { key: 'storeName',           label: 'Store Name',       hints: ['store name', 'store', 'dispensary'] },
  { key: 'purchaseOrderNumber', label: 'Purchase Order #', hints: ['purchase order', 'po #', 'po#', 'purchase order number'] },
  { key: 'licenseNumber',       label: 'License Number',   required: true,  hints: ['license number', 'license', 'license #', 'lic #'] },
]

// Sentinel used in the dropdowns to mean "skip this CSV column".
const SKIP = '__skip__'

interface ParsedCsv {
  headers: string[]
  rows: string[][]
  fileName: string
}

interface RowState {
  ignored: boolean
}

// ---- Component -------------------------------------------------------------

export default function OrderImportPanel() {
  const { user } = useAuth()
  const fileInputRef = useRef<HTMLInputElement>(null)

  const [parsed, setParsed] = useState<ParsedCsv | null>(null)
  const [parseError, setParseError] = useState('')
  // mapping[csvColumnIndex] = FieldKey | SKIP
  const [mapping, setMapping] = useState<string[]>([])
  // Per-row state — currently just whether the row is ignored.
  const [rowState, setRowState] = useState<RowState[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [result, setResult] = useState<ImportOrdersResult | null>(null)
  const [submitError, setSubmitError] = useState('')
  // Default new orders to Ordered. Picking Scheduled here triggers a single
  // batched route-optimization run for everything that gets imported as
  // Scheduled (stale rows still fall through to RouteOmitted server-side).
  const [initialStatus, setInitialStatus] = useState<ImportInitialStatus>('Ordered')

  // Stores keyed by license number for fast match lookup. Refreshed when the
  // operator creates a new Store inline so that row immediately flips to
  // matched without a full re-fetch.
  const [stores, setStores] = useState<StoreLite[]>([])
  const [storesLoading, setStoresLoading] = useState(true)
  const [createStoreFor, setCreateStoreFor] = useState<{ rowIndex: number; license: string; storeName?: string } | null>(null)

  useEffect(() => {
    setStoresLoading(true)
    getStores()
      .then((s) => setStores(s))
      .catch(() => setStores([]))
      .finally(() => setStoresLoading(false))
  }, [])

  const storesByLicense = useMemo(() => {
    const m = new Map<string, StoreLite>()
    for (const s of stores) {
      if (s.licenseNumber) m.set(s.licenseNumber.trim().toLowerCase(), s)
    }
    return m
  }, [stores])

  const { duplicates, missingRequired } = useMemo(() => {
    const counts = new Map<string, number>()
    for (const m of mapping) {
      if (m && m !== SKIP) counts.set(m, (counts.get(m) ?? 0) + 1)
    }
    const dups = new Set<string>()
    for (const [k, n] of counts) if (n > 1) dups.add(k)
    const mapped = new Set(counts.keys())
    const missing = FIELDS.filter((f) => f.required && !mapped.has(f.key)).map((f) => f.label)
    return { duplicates: dups, missingRequired: missing }
  }, [mapping])

  // Per-row resolved values + match status. Recomputed when mapping, rows,
  // ignored flags, or the stores cache change.
  const resolvedRows = useMemo(() => {
    if (!parsed) return []
    return parsed.rows.map((cells, idx) => {
      const fields = readRow(cells, mapping)
      const license = (fields.licenseNumber ?? '').trim()
      const matched = license ? storesByLicense.get(license.toLowerCase()) : undefined
      return {
        idx,
        ignored: rowState[idx]?.ignored ?? false,
        fields,
        license,
        matched,
      }
    })
  }, [parsed, mapping, rowState, storesByLicense])

  const summary = useMemo(() => {
    let toImport = 0
    let unmatched = 0
    let ignored = 0
    let invalidDate = 0
    for (const r of resolvedRows) {
      if (r.ignored) { ignored++; continue }
      if (!r.matched) { unmatched++; continue }
      if (!r.fields.orderDate) { invalidDate++; continue }
      toImport++
    }
    return { toImport, unmatched, ignored, invalidDate }
  }, [resolvedRows])

  function handleFile(file: File) {
    setParseError('')
    setResult(null)
    setSubmitError('')

    const reader = new FileReader()
    reader.onerror = () => setParseError('Failed to read file.')
    reader.onload = () => {
      try {
        const text = String(reader.result ?? '')
        const all = parseCsv(text)
        if (all.length < 2) {
          setParseError('CSV must contain a header row and at least one data row.')
          return
        }
        const [headers, ...rows] = all
        // Pad short rows so column counts line up — spreadsheets sometimes
        // emit shorter rows when trailing cells are empty.
        const colCount = headers.length
        const padded = rows.map((r) => r.length === colCount ? r : [...r, ...Array(colCount - r.length).fill('')])
        setParsed({ headers, rows: padded, fileName: file.name })
        setMapping(autoMap(headers))
        setRowState(padded.map(() => ({ ignored: false })))
      } catch (e) {
        setParseError(`Failed to parse CSV: ${e instanceof Error ? e.message : 'unknown error'}`)
      }
    }
    reader.readAsText(file)
  }

  function reset() {
    setParsed(null)
    setMapping([])
    setRowState([])
    setResult(null)
    setSubmitError('')
    setParseError('')
    if (fileInputRef.current) fileInputRef.current.value = ''
  }

  async function handleImport() {
    if (!parsed) return
    setSubmitting(true)
    setSubmitError('')
    setResult(null)
    try {
      const payload: ImportedOrderRow[] = resolvedRows
        .filter((r) => !r.ignored && r.matched && r.fields.orderDate)
        .map((r) => ({
          storeId:             r.matched!.id,
          orderDate:           r.fields.orderDate,
          customer:            r.fields.customer,
          salesOrderNumber:    r.fields.salesOrderNumber,
          purchaseOrderNumber: r.fields.purchaseOrderNumber,
        }))

      if (payload.length === 0) {
        setSubmitError('No rows ready to import. Match or ignore each row, then try again.')
        return
      }

      const res = await importOrders(payload, initialStatus)
      setResult(res)
    } catch (e) {
      const msg = e instanceof Error ? e.message : 'Import failed.'
      setSubmitError(msg)
    } finally {
      setSubmitting(false)
    }
  }

  async function handleStoreCreated(newStore: StoreLite) {
    // Refresh the cache and let the affected row's match flip from
    // unmatched to matched on the next render.
    setStores((prev) => [...prev, newStore])
    setCreateStoreFor(null)
  }

  const blockingUnmatched = summary.unmatched > 0
  const blockingDate      = summary.invalidDate > 0
  const canImport =
    !submitting &&
    missingRequired.length === 0 &&
    summary.toImport > 0 &&
    !blockingUnmatched &&
    !blockingDate

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold text-gray-900">Import Orders</h1>
          <p className="text-sm text-gray-500 mt-0.5">
            Upload a CSV of orders, map each column to a database field, then import.
            {' '}
            {user?.companyName ? <span>Importing into <strong>{user.companyName}</strong>.</span> : null}
          </p>
        </div>
        {parsed && (
          <button onClick={reset} className="text-sm text-gray-500 hover:text-gray-700">
            Start over
          </button>
        )}
      </div>

      {!parsed ? (
        <UploadDropzone fileInputRef={fileInputRef} onFile={handleFile} error={parseError} />
      ) : (
        <>
          <MappingGrid
            headers={parsed.headers}
            sample={parsed.rows[0] ?? []}
            mapping={mapping}
            duplicates={duplicates}
            onChange={(idx, value) => {
              setMapping((prev) => {
                const next = [...prev]
                next[idx] = value
                return next
              })
            }}
          />

          {missingRequired.length === 0 ? (
            <PreviewTable
              rows={resolvedRows}
              storesLoading={storesLoading}
              onToggleIgnore={(idx) => {
                setRowState((prev) => {
                  const next = [...prev]
                  next[idx] = { ignored: !(next[idx]?.ignored ?? false) }
                  return next
                })
              }}
              onCreateStore={(idx, license, storeName) =>
                setCreateStoreFor({ rowIndex: idx, license, storeName })
              }
            />
          ) : (
            <div className="bg-amber-50 border border-amber-200 rounded-xl p-4 text-sm text-amber-700">
              Map the required field{missingRequired.length === 1 ? '' : 's'} above to continue: {missingRequired.join(', ')}.
            </div>
          )}

          <div className="bg-white rounded-xl border border-gray-200 p-4 flex items-center justify-between gap-3 flex-wrap">
            <div className="text-sm text-gray-600 flex flex-wrap items-center gap-x-4 gap-y-1">
              <span><span className="font-medium text-green-700">{summary.toImport}</span> ready</span>
              <span><span className="font-medium text-amber-700">{summary.unmatched}</span> unmatched</span>
              <span><span className="font-medium text-gray-500">{summary.ignored}</span> ignored</span>
              {summary.invalidDate > 0 && (
                <span><span className="font-medium text-red-700">{summary.invalidDate}</span> invalid date</span>
              )}
              {duplicates.size > 0 && <span className="text-amber-600">Duplicate field mapping detected.</span>}
              <span className="text-gray-400 text-xs">from <span className="font-mono">{parsed.fileName}</span></span>
            </div>
            <div className="flex items-center gap-2">
              <label className="text-xs text-gray-600">Initial status</label>
              <select
                value={initialStatus}
                onChange={(e) => setInitialStatus(e.target.value as ImportInitialStatus)}
                className="text-sm border border-gray-300 rounded-md px-2 py-1 focus:outline-none focus:ring-2 focus:ring-brand-400"
                title="Pick Scheduled to route the imported orders immediately."
              >
                <option value="Ordered">Ordered</option>
                <option value="Scheduled">Scheduled (route now)</option>
              </select>
            </div>
            <button
              onClick={handleImport}
              disabled={!canImport}
              className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50"
              title={
                blockingUnmatched ? 'Match or ignore every row before importing.' :
                blockingDate      ? 'Some rows have an invalid order date.' :
                summary.toImport === 0 ? 'No rows are ready to import.' : ''
              }
            >
              {submitting ? 'Importing…' : `Import ${summary.toImport} Order${summary.toImport === 1 ? '' : 's'}`}
            </button>
          </div>

          {submitError && (
            <div className="bg-red-50 border border-red-200 rounded-xl p-4 text-sm text-red-700">{submitError}</div>
          )}

          {result && <ImportResultCard result={result} />}
        </>
      )}

      {createStoreFor && (
        <CreateStoreDialog
          license={createStoreFor.license}
          initialName={createStoreFor.storeName ?? ''}
          existingLicenses={storesByLicense}
          onCancel={() => setCreateStoreFor(null)}
          onCreated={handleStoreCreated}
        />
      )}
    </div>
  )
}

// ---- Upload dropzone -------------------------------------------------------

function UploadDropzone({
  fileInputRef,
  onFile,
  error,
}: {
  fileInputRef: React.RefObject<HTMLInputElement>
  onFile: (file: File) => void
  error: string
}) {
  const [dragOver, setDragOver] = useState(false)

  return (
    <div className="space-y-3">
      <div
        onDragOver={(e) => { e.preventDefault(); setDragOver(true) }}
        onDragLeave={() => setDragOver(false)}
        onDrop={(e) => {
          e.preventDefault()
          setDragOver(false)
          const f = e.dataTransfer.files[0]
          if (f) onFile(f)
        }}
        onClick={() => fileInputRef.current?.click()}
        className={`bg-white rounded-xl border-2 border-dashed p-12 text-center cursor-pointer transition-colors ${
          dragOver ? 'border-brand-400 bg-brand-50' : 'border-gray-300 hover:border-gray-400'
        }`}
      >
        <p className="text-base font-medium text-gray-700 mb-1">Drop a CSV file here, or click to browse</p>
        <p className="text-xs text-gray-500">First row must be column headers. Required: Order Date, License Number. Address details come from the matched store.</p>
        <input
          ref={fileInputRef}
          type="file"
          accept=".csv,text/csv"
          className="hidden"
          onChange={(e) => {
            const f = e.target.files?.[0]
            if (f) onFile(f)
          }}
        />
      </div>
      {error && <div className="bg-red-50 border border-red-200 rounded-xl p-3 text-sm text-red-700">{error}</div>}
    </div>
  )
}

// ---- Mapping grid ----------------------------------------------------------

function MappingGrid({
  headers,
  sample,
  mapping,
  duplicates,
  onChange,
}: {
  headers: string[]
  sample: string[]
  mapping: string[]
  duplicates: Set<string>
  onChange: (csvColumnIndex: number, value: string) => void
}) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
      <div className="px-5 py-3 border-b border-gray-100">
        <h2 className="text-sm font-semibold text-gray-800">Column Mapping</h2>
        <p className="text-xs text-gray-500 mt-0.5">Match each CSV column to a database field. Address columns are not used — orders inherit the store's address.</p>
      </div>
      <div className="overflow-x-auto">
        <table className="w-full text-sm">
          <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
            <tr>
              <th className="px-4 py-2 text-left font-medium w-1/4">CSV Column</th>
              <th className="px-4 py-2 text-left font-medium w-1/3">Sample</th>
              <th className="px-4 py-2 text-left font-medium">Maps to</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {headers.map((h, idx) => {
              const value = mapping[idx] ?? SKIP
              const isDup = value !== SKIP && duplicates.has(value)
              return (
                <tr key={idx}>
                  <td className="px-4 py-2 font-medium text-gray-900">{h || <span className="text-gray-400 italic">(unnamed)</span>}</td>
                  <td className="px-4 py-2 text-gray-500 truncate max-w-[18rem]" title={sample[idx]}>{sample[idx] ?? ''}</td>
                  <td className="px-4 py-2">
                    <select
                      value={value}
                      onChange={(e) => onChange(idx, e.target.value)}
                      className={`text-sm border rounded-lg px-3 py-1.5 focus:outline-none focus:ring-2 focus:ring-brand-400 ${
                        isDup ? 'border-amber-400 bg-amber-50' : 'border-gray-300'
                      }`}
                    >
                      <option value={SKIP}>— Skip this column —</option>
                      {FIELDS.map((f) => (
                        <option key={f.key} value={f.key}>
                          {f.label}{f.required ? ' *' : ''}
                        </option>
                      ))}
                    </select>
                  </td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
    </div>
  )
}

// ---- Preview table ---------------------------------------------------------

interface ResolvedRow {
  idx: number
  ignored: boolean
  fields: Record<FieldKey, string | undefined>
  license: string
  matched: StoreLite | undefined
}

function PreviewTable({
  rows,
  storesLoading,
  onToggleIgnore,
  onCreateStore,
}: {
  rows: ResolvedRow[]
  storesLoading: boolean
  onToggleIgnore: (idx: number) => void
  onCreateStore: (idx: number, license: string, storeName?: string) => void
}) {
  return (
    <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
      <div className="px-5 py-3 border-b border-gray-100 flex items-center justify-between">
        <div>
          <h2 className="text-sm font-semibold text-gray-800">Preview</h2>
          <p className="text-xs text-gray-500 mt-0.5">
            Each row must either be matched to an existing store by license number or ignored. Unmatched rows can be resolved by creating a new store.
          </p>
        </div>
        <p className="text-xs text-gray-500">{rows.length} row{rows.length === 1 ? '' : 's'}</p>
      </div>
      <div className="overflow-x-auto max-h-[28rem]">
        <table className="w-full text-xs">
          <thead className="bg-gray-50 text-gray-500 uppercase tracking-wide sticky top-0">
            <tr>
              <th className="px-3 py-2 text-left font-medium w-12">Ignore</th>
              <th className="px-3 py-2 text-left font-medium w-10">#</th>
              <th className="px-3 py-2 text-left font-medium whitespace-nowrap">Order Date</th>
              <th className="px-3 py-2 text-left font-medium whitespace-nowrap">Customer</th>
              <th className="px-3 py-2 text-left font-medium whitespace-nowrap">Sales Order #</th>
              <th className="px-3 py-2 text-left font-medium whitespace-nowrap">Store Name (CSV)</th>
              <th className="px-3 py-2 text-left font-medium whitespace-nowrap">Purchase Order #</th>
              <th className="px-3 py-2 text-left font-medium whitespace-nowrap">License Number</th>
              <th className="px-3 py-2 text-left font-medium whitespace-nowrap">Match Status</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100">
            {rows.map((r) => {
              const dim = r.ignored ? 'opacity-40' : ''
              return (
                <tr key={r.idx} className={`hover:bg-gray-50 ${r.ignored ? 'bg-gray-50' : ''}`}>
                  <td className="px-3 py-1.5">
                    <input
                      type="checkbox"
                      checked={r.ignored}
                      onChange={() => onToggleIgnore(r.idx)}
                      className="rounded border-gray-300 text-brand-500 focus:ring-brand-400 cursor-pointer"
                      title="Ignore this row — it won't be imported."
                    />
                  </td>
                  <td className={`px-3 py-1.5 text-gray-400 ${dim}`}>{r.idx + 1}</td>
                  <td className={`px-3 py-1.5 text-gray-700 whitespace-nowrap ${dim}`}>{r.fields.orderDate ?? <span className="text-red-500">—</span>}</td>
                  <td className={`px-3 py-1.5 text-gray-700 whitespace-nowrap ${dim}`}>{r.fields.customer ?? ''}</td>
                  <td className={`px-3 py-1.5 text-gray-700 whitespace-nowrap ${dim}`}>{r.fields.salesOrderNumber ?? ''}</td>
                  <td className={`px-3 py-1.5 text-gray-700 whitespace-nowrap ${dim}`}>{r.fields.storeName ?? ''}</td>
                  <td className={`px-3 py-1.5 text-gray-700 whitespace-nowrap ${dim}`}>{r.fields.purchaseOrderNumber ?? ''}</td>
                  <td className={`px-3 py-1.5 text-gray-700 whitespace-nowrap font-mono ${dim}`}>{r.license || <span className="text-gray-400 italic">—</span>}</td>
                  <td className="px-3 py-1.5 whitespace-nowrap">
                    <MatchCell
                      ignored={r.ignored}
                      license={r.license}
                      matched={r.matched}
                      storesLoading={storesLoading}
                      onCreateStore={() => onCreateStore(r.idx, r.license, r.fields.storeName)}
                    />
                  </td>
                </tr>
              )
            })}
            {rows.length === 0 && (
              <tr><td colSpan={9} className="px-4 py-8 text-center text-gray-400">No rows to preview.</td></tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function MatchCell({
  ignored,
  license,
  matched,
  storesLoading,
  onCreateStore,
}: {
  ignored: boolean
  license: string
  matched: StoreLite | undefined
  storesLoading: boolean
  onCreateStore: () => void
}) {
  if (ignored) return <span className="text-xs text-gray-400">Ignored</span>
  if (storesLoading) return <span className="text-xs text-gray-400">Checking…</span>
  if (matched) {
    return (
      <span className="inline-flex items-center gap-1.5 text-xs text-green-700">
        <span className="w-1.5 h-1.5 rounded-full bg-green-500" />
        Matched · {matched.name}
      </span>
    )
  }
  if (!license) {
    return (
      <span className="inline-flex items-center gap-2 text-xs text-red-600">
        <span>Missing license</span>
        <button
          onClick={onCreateStore}
          className="text-brand-600 hover:text-brand-800 underline"
          disabled
          title="A license number is required to create a store. Edit the source file or ignore this row."
        >
          Create store
        </button>
      </span>
    )
  }
  return (
    <span className="inline-flex items-center gap-2 text-xs text-red-600">
      <span>No match</span>
      <button
        onClick={onCreateStore}
        className="text-brand-600 hover:text-brand-800 underline"
      >
        Create store
      </button>
    </span>
  )
}

// ---- Inline Create Store dialog -------------------------------------------

function CreateStoreDialog({
  license,
  initialName,
  existingLicenses,
  onCancel,
  onCreated,
}: {
  license: string
  initialName: string
  existingLicenses: Map<string, StoreLite>
  onCancel: () => void
  onCreated: (store: StoreLite) => void
}) {
  const [form, setForm] = useState<CreateStorePayload>({
    name:          initialName,
    licenseNumber: license,
    address:       '',
    customer:      '',
    city:          '',
    state:         'IL',
    zip:           '',
    county:        '',
  })
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  const licenseTaken = existingLicenses.has(form.licenseNumber.trim().toLowerCase())

  async function submit() {
    setError('')
    if (!form.name.trim() || !form.licenseNumber.trim() || !form.address.trim()) {
      setError('Name, License Number, and Address are required.')
      return
    }
    if (licenseTaken && form.licenseNumber.trim() !== license.trim()) {
      setError('A store with that license number already exists.')
      return
    }
    setSaving(true)
    try {
      const created = await createStore({
        ...form,
        name:          form.name.trim(),
        licenseNumber: form.licenseNumber.trim(),
        address:       form.address.trim(),
        customer:      form.customer?.trim() || undefined,
        city:          form.city?.trim()     || undefined,
        state:         form.state?.trim()    || undefined,
        zip:           form.zip?.trim()      || undefined,
        county:        form.county?.trim()   || undefined,
      })
      onCreated(created)
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to create store.')
    } finally {
      setSaving(false)
    }
  }

  function update<K extends keyof CreateStorePayload>(key: K, value: CreateStorePayload[K]) {
    setForm((prev) => ({ ...prev, [key]: value }))
  }

  return (
    <div className="fixed inset-0 z-50 bg-black/40 flex items-center justify-center p-4">
      <div className="bg-white rounded-2xl border border-gray-200 shadow-xl w-full max-w-2xl">
        <div className="px-6 py-4 border-b border-gray-100">
          <h3 className="text-base font-semibold text-gray-900">Create New Store</h3>
          <p className="text-xs text-gray-500 mt-0.5">
            This store will be saved under your company and used to resolve the unmatched import row.
            Address is geocoded so deliveries route correctly.
          </p>
        </div>

        <div className="px-6 py-4 grid grid-cols-2 gap-3">
          <Field label="Store Name *" value={form.name} onChange={(v) => update('name', v)} />
          <Field label="License Number *" value={form.licenseNumber} onChange={(v) => update('licenseNumber', v)} />
          <Field label="Customer" value={form.customer ?? ''} onChange={(v) => update('customer', v)} />
          <div />
          <div className="col-span-2">
            <Field label="Street Address *" value={form.address} onChange={(v) => update('address', v)} />
          </div>
          <Field label="City" value={form.city ?? ''} onChange={(v) => update('city', v)} />
          <div className="grid grid-cols-2 gap-3">
            <Field label="State" value={form.state ?? ''} onChange={(v) => update('state', v)} />
            <Field label="ZIP" value={form.zip ?? ''} onChange={(v) => update('zip', v)} />
          </div>
          <Field label="County" value={form.county ?? ''} onChange={(v) => update('county', v)} />
          <Field label="Email" value={form.email ?? ''} onChange={(v) => update('email', v)} />
          <Field label="Phone" value={form.phone ?? ''} onChange={(v) => update('phone', v)} />
        </div>

        {error && (
          <div className="px-6 pb-2">
            <div className="bg-red-50 border border-red-200 rounded-lg px-3 py-2 text-sm text-red-700">{error}</div>
          </div>
        )}

        <div className="px-6 py-3 border-t border-gray-100 flex justify-end gap-2">
          <button
            onClick={onCancel}
            className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900"
          >
            Cancel
          </button>
          <button
            onClick={submit}
            disabled={saving}
            className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50"
          >
            {saving ? 'Saving…' : 'Create Store'}
          </button>
        </div>
      </div>
    </div>
  )
}

function Field({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <div>
      <label className="block text-xs font-medium text-gray-600 mb-1">{label}</label>
      <input type="text" value={value} onChange={(e) => onChange(e.target.value)}
        className="w-full px-3 py-2 text-sm border border-gray-300 rounded-lg focus:outline-none focus:ring-2 focus:ring-brand-400" />
    </div>
  )
}

// ---- Result ----------------------------------------------------------------

function ImportResultCard({ result }: { result: ImportOrdersResult }) {
  const hasErrors    = result.errors.length > 0
  const routes       = result.routesQueued      ?? 0
  const omitted      = result.routeOmitted      ?? 0
  const stores       = result.storesGeocoded    ?? 0
  const hubs         = result.hubsGeocoded      ?? 0
  const warehouses   = result.warehousesGeocoded ?? 0
  const failures     = result.geocodeFailures   ?? 0
  const ungeocoded   = result.ordersUngeocoded  ?? 0
  const geocodeBits: string[] = []
  if (stores     > 0) geocodeBits.push(`${stores} store${stores === 1 ? '' : 's'}`)
  if (hubs       > 0) geocodeBits.push(`${hubs} hub${hubs === 1 ? '' : 's'}`)
  if (warehouses > 0) geocodeBits.push(`${warehouses} warehouse${warehouses === 1 ? '' : 's'}`)
  return (
    <div className={`rounded-xl border p-4 space-y-2 ${
      hasErrors ? 'bg-amber-50 border-amber-200' : 'bg-green-50 border-green-200'
    }`}>
      <p className="text-sm font-semibold text-gray-800">
        Imported {result.created} order{result.created === 1 ? '' : 's'}
        {hasErrors ? `, ${result.errors.length} skipped.` : '.'}
      </p>
      {geocodeBits.length > 0 && (
        <p className="text-xs text-gray-700">
          Geocoded <strong>{geocodeBits.join(', ')}</strong> via Google Maps (cached for next time).
        </p>
      )}
      {(routes > 0 || omitted > 0 || ungeocoded > 0 || failures > 0) && (
        <p className="text-xs text-gray-700">
          {routes > 0 && (
            <>Queued <strong>{routes}</strong> optimization run{routes === 1 ? '' : 's'}. </>
          )}
          {omitted > 0 && (
            <span className="text-stone-600">
              {omitted} order{omitted === 1 ? '' : 's'} marked Route Omitted (more than a day old).{' '}
            </span>
          )}
          {ungeocoded > 0 && (
            <span className="text-amber-700">
              {ungeocoded} order{ungeocoded === 1 ? '' : 's'} still missing a geocode — check the source address.{' '}
            </span>
          )}
          {failures > 0 && (
            <span className="text-red-700">
              {failures} address{failures === 1 ? '' : 'es'} couldn't be resolved by Google.
            </span>
          )}
        </p>
      )}
      {hasErrors && (
        <ul className="text-xs text-gray-700 list-disc pl-5 space-y-0.5">
          {result.errors.slice(0, 25).map((e) => (
            <li key={e.row}>Row {e.row + 2}: {e.message}</li>
          ))}
          {result.errors.length > 25 && <li>…and {result.errors.length - 25} more.</li>}
        </ul>
      )}
    </div>
  )
}

// ---- Helpers ---------------------------------------------------------------

function autoMap(headers: string[]): string[] {
  const taken = new Set<string>()
  return headers.map((h) => {
    const norm = h.trim().toLowerCase()
    if (!norm) return SKIP
    for (const f of FIELDS) {
      if (taken.has(f.key)) continue
      if (f.hints.some((p) => norm === p || norm.includes(p))) {
        taken.add(f.key)
        return f.key
      }
    }
    return SKIP
  })
}

function readRow(cells: string[], mapping: string[]): Record<FieldKey, string | undefined> {
  const out: Partial<Record<FieldKey, string | undefined>> = {}
  mapping.forEach((m, idx) => {
    if (!m || m === SKIP) return
    const value = cells[idx]
    if (value === undefined) return
    const trimmed = value.trim()
    out[m as FieldKey] = trimmed.length > 0 ? trimmed : undefined
  })
  return out as Record<FieldKey, string | undefined>
}
