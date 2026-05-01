import {
  useEffect,
  useMemo,
  useState,
  type ClipboardEvent as ReactClipboardEvent,
  type KeyboardEvent as ReactKeyboardEvent,
} from 'react'
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

// ---- Field model -----------------------------------------------------------
//
// Order columns the user can edit. Same conceptual schema as the CSV mapping
// flow — license number resolves to a Store at submit time, and order
// addresses come from the matched store, never from this grid.
type FieldKey =
  | 'orderDate'
  | 'customer'
  | 'salesOrderNumber'
  | 'storeName'
  | 'purchaseOrderNumber'
  | 'licenseNumber'

interface ColumnDef {
  key: FieldKey
  label: string
  required?: boolean
  width: string
  placeholder?: string
}

const COLUMNS: ColumnDef[] = [
  { key: 'orderDate',           label: 'Order Date',       required: true, width: 'w-[8rem]', placeholder: 'YYYY-MM-DD' },
  { key: 'customer',            label: 'Customer',         width: 'w-[11rem]' },
  { key: 'salesOrderNumber',    label: 'Sales Order #',    width: 'w-[9rem]' },
  { key: 'storeName',           label: 'Store Name',       width: 'w-[14rem]' },
  { key: 'purchaseOrderNumber', label: 'Purchase Order #', width: 'w-[9rem]' },
  { key: 'licenseNumber',       label: 'License Number',   required: true, width: 'w-[10rem]' },
]

const INITIAL_ROW_COUNT = 5
const SCRATCH_ROW_LIMIT = 500

type Row = Record<FieldKey, string>

const emptyRow = (): Row => ({
  orderDate:           '',
  customer:            '',
  salesOrderNumber:    '',
  storeName:           '',
  purchaseOrderNumber: '',
  licenseNumber:       '',
})

interface ResolvedRow {
  index: number
  row: Row
  isEmpty: boolean
  matched: StoreLite | undefined
  warning: string | null
}

// ---- Component -------------------------------------------------------------

export default function ManualOrderEntry() {
  const { user } = useAuth()

  const [rows, setRows] = useState<Row[]>(() =>
    Array.from({ length: INITIAL_ROW_COUNT }, emptyRow),
  )
  const [stores, setStores] = useState<StoreLite[]>([])
  const [storesLoading, setStoresLoading] = useState(true)
  const [createStoreFor, setCreateStoreFor] =
    useState<{ rowIndex: number; license: string; storeName?: string } | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState('')
  const [result, setResult] = useState<ImportOrdersResult | null>(null)
  // Same selector the CSV flow has — pick Scheduled to route immediately.
  const [initialStatus, setInitialStatus] = useState<ImportInitialStatus>('Ordered')

  useEffect(() => {
    setStoresLoading(true)
    getStores()
      .then(setStores)
      .catch(() => setStores([]))
      .finally(() => setStoresLoading(false))
  }, [])

  const storesByLicense = useMemo(() => {
    const map = new Map<string, StoreLite>()
    for (const s of stores) {
      if (s.licenseNumber) map.set(s.licenseNumber.trim().toLowerCase(), s)
    }
    return map
  }, [stores])

  // Resolved view of the current rows: matches license, flags duplicates
  // inside the batch, and empty-row detection so the UI knows what's
  // submittable.
  const resolved = useMemo<ResolvedRow[]>(() => {
    const seenSalesOrders = new Map<string, number>() // value -> first row idx
    return rows.map((row, idx) => {
      const isEmpty = COLUMNS.every((c) => !row[c.key]?.trim())
      const license = row.licenseNumber.trim()
      const matched = license ? storesByLicense.get(license.toLowerCase()) : undefined
      let warning: string | null = null

      if (!isEmpty) {
        if (!row.orderDate.trim()) warning = 'Order Date is required.'
        else if (!license) warning = 'License Number is required.'
        else if (!matched) warning = 'No matching store. Click "Create store" or fix the license number.'

        const so = row.salesOrderNumber.trim().toLowerCase()
        if (so) {
          const first = seenSalesOrders.get(so)
          if (first !== undefined && first !== idx) {
            warning = warning ?? `Duplicate sales order — also on row ${first + 1}.`
          } else {
            seenSalesOrders.set(so, idx)
          }
        }
      }

      return { index: idx, row, isEmpty, matched, warning }
    })
  }, [rows, storesByLicense])

  const submittableCount = resolved.filter(
    (r) => !r.isEmpty && r.warning === null && r.matched !== undefined,
  ).length

  // ---- Mutations ----------------------------------------------------------

  function setCell(rowIdx: number, key: FieldKey, value: string) {
    setRows((prev) => {
      const next = [...prev]
      next[rowIdx] = { ...next[rowIdx], [key]: value }
      return next
    })
  }

  function addRow() {
    setRows((prev) => (prev.length >= SCRATCH_ROW_LIMIT ? prev : [...prev, emptyRow()]))
  }

  function deleteRow(idx: number) {
    setRows((prev) => {
      if (prev.length === 1) return [emptyRow()]
      const next = [...prev]
      next.splice(idx, 1)
      return next
    })
  }

  function clearAll() {
    if (!confirm('Clear all rows?')) return
    setRows(Array.from({ length: INITIAL_ROW_COUNT }, emptyRow))
    setResult(null)
    setSubmitError('')
  }

  // Spreadsheet-style paste: tab-separated values fan out across columns,
  // newline-separated values fan out across rows. If the clipboard contains
  // only one value, fall back to default paste behavior (single cell).
  function handlePaste(
    e: ReactClipboardEvent<HTMLInputElement>,
    rowIdx: number,
    colIdx: number,
  ) {
    const text = e.clipboardData.getData('text/plain')
    if (!text) return
    if (!text.includes('\t') && !text.includes('\n')) return // single cell

    e.preventDefault()
    const lines = text.replace(/\r/g, '').split('\n')
    // Strip trailing blank line if present (Excel often ends with \n)
    while (lines.length > 1 && lines[lines.length - 1].trim() === '') lines.pop()

    setRows((prev) => {
      const next = [...prev]
      // Grow the grid as needed to fit the pasted block
      const needed = rowIdx + lines.length
      while (next.length < needed && next.length < SCRATCH_ROW_LIMIT) next.push(emptyRow())

      lines.forEach((line, i) => {
        const targetRow = rowIdx + i
        if (targetRow >= next.length) return
        const cells = line.split('\t')
        const target = { ...next[targetRow] }
        cells.forEach((cell, j) => {
          const targetCol = colIdx + j
          if (targetCol >= COLUMNS.length) return
          target[COLUMNS[targetCol].key] = cell.trim()
        })
        next[targetRow] = target
      })
      return next
    })
  }

  // Tab/Enter cell navigation. Enter moves down one row, Tab/Shift-Tab moves
  // across columns (and rolls to the next row at the edge).
  function handleKeyDown(
    e: ReactKeyboardEvent<HTMLInputElement>,
    rowIdx: number,
    colIdx: number,
  ) {
    if (e.key === 'Enter') {
      e.preventDefault()
      const targetRow = rowIdx + 1
      if (targetRow >= rows.length) addRow()
      focusCell(targetRow, colIdx)
      return
    }
    if (e.key === 'Tab') {
      // Native Tab is already correct unless we're at the last cell of the
      // last row, in which case extend the grid so the user can keep typing.
      if (!e.shiftKey && rowIdx === rows.length - 1 && colIdx === COLUMNS.length - 1) {
        addRow()
        // Don't preventDefault — let the browser advance focus into the new row.
      }
    }
  }

  // ---- Submit -------------------------------------------------------------

  async function handleSubmit() {
    setSubmitError('')
    setResult(null)

    const payload: ImportedOrderRow[] = resolved
      .filter((r) => !r.isEmpty && r.warning === null && r.matched !== undefined)
      .map((r) => ({
        storeId:             r.matched!.id,
        orderDate:           r.row.orderDate.trim(),
        customer:            blankToUndef(r.row.customer),
        salesOrderNumber:    blankToUndef(r.row.salesOrderNumber),
        purchaseOrderNumber: blankToUndef(r.row.purchaseOrderNumber),
      }))

    if (payload.length === 0) {
      setSubmitError('Nothing to submit. Fill in the required fields and resolve any warnings first.')
      return
    }

    setSubmitting(true)
    try {
      const res = await importOrders(payload, initialStatus)
      setResult(res)

      // Clear successfully-submitted rows so the user can keep entering more.
      // We don't know exactly which input rows mapped to which response rows
      // when there are server-side errors, so the simplest correct behaviour
      // is: if everything succeeded, blank the grid; otherwise leave it
      // alone so the operator can inspect/resubmit.
      if (res.errors.length === 0) {
        setRows(Array.from({ length: INITIAL_ROW_COUNT }, emptyRow))
      }
    } catch (e) {
      setSubmitError(e instanceof Error ? e.message : 'Submit failed.')
    } finally {
      setSubmitting(false)
    }
  }

  async function handleStoreCreated(newStore: StoreLite) {
    setStores((prev) => [...prev, newStore])
    setCreateStoreFor(null)
  }

  // ---- Render -------------------------------------------------------------

  return (
    <div className="space-y-3">
      <div className="bg-white rounded-xl border border-gray-200 p-4 flex flex-wrap items-center justify-between gap-3">
        <div className="text-sm text-gray-600">
          Type rows directly, or paste from a spreadsheet (tabs / newlines fan out across cells and rows).
          {' '}
          {user?.warehouseName ? <span>Importing into <strong>{user.warehouseName}</strong>.</span> : null}
        </div>
        <div className="flex items-center gap-2">
          <button onClick={addRow} className="text-sm text-brand-600 hover:underline">+ Add row</button>
          <span className="text-gray-300">·</span>
          <button onClick={clearAll} className="text-sm text-gray-500 hover:text-gray-700">Clear</button>
        </div>
      </div>

      <div className="bg-white rounded-xl border border-gray-200 overflow-hidden">
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead className="bg-gray-50 text-gray-500 text-xs uppercase tracking-wide">
              <tr>
                <th className="px-3 py-2 text-left font-medium w-10">#</th>
                {COLUMNS.map((c) => (
                  <th key={c.key} className={`px-3 py-2 text-left font-medium ${c.width}`}>
                    {c.label}{c.required ? ' *' : ''}
                  </th>
                ))}
                <th className="px-3 py-2 text-left font-medium w-[12rem]">Status</th>
                <th className="px-3 py-2 text-left font-medium w-10"></th>
              </tr>
            </thead>
            <tbody className="divide-y divide-gray-100">
              {resolved.map((r) => (
                <tr key={r.index} className={r.warning ? 'bg-amber-50/40' : ''}>
                  <td className="px-3 py-1 text-gray-400">{r.index + 1}</td>
                  {COLUMNS.map((c, colIdx) => (
                    <td key={c.key} className="px-1 py-1">
                      <input
                        id={`cell-${r.index}-${colIdx}`}
                        type="text"
                        value={r.row[c.key]}
                        placeholder={c.placeholder ?? ''}
                        onChange={(e) => setCell(r.index, c.key, e.target.value)}
                        onPaste={(e) => handlePaste(e, r.index, colIdx)}
                        onKeyDown={(e) => handleKeyDown(e, r.index, colIdx)}
                        className={`w-full px-2 py-1 text-xs border rounded focus:outline-none focus:ring-1 focus:ring-brand-400 ${
                          c.required && !r.isEmpty && !r.row[c.key].trim()
                            ? 'border-red-300 bg-red-50'
                            : 'border-gray-200'
                        }`}
                      />
                    </td>
                  ))}
                  <td className="px-2 py-1">
                    <RowStatus
                      r={r}
                      storesLoading={storesLoading}
                      onCreateStore={() =>
                        setCreateStoreFor({
                          rowIndex:  r.index,
                          license:   r.row.licenseNumber.trim(),
                          storeName: r.row.storeName.trim() || undefined,
                        })
                      }
                    />
                  </td>
                  <td className="px-2 py-1 text-right">
                    <button
                      onClick={() => deleteRow(r.index)}
                      className="text-gray-300 hover:text-red-500 text-xs"
                      title="Delete row"
                    >
                      ✕
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <div className="bg-white rounded-xl border border-gray-200 p-3 flex items-center justify-between gap-3 flex-wrap">
        <div className="text-sm text-gray-600">
          <span className="font-medium text-green-700">{submittableCount}</span> ready to submit
        </div>
        <div className="flex items-center gap-3">
          <div className="flex items-center gap-2">
            <label className="text-xs text-gray-600">Initial status</label>
            <select
              value={initialStatus}
              onChange={(e) => setInitialStatus(e.target.value as ImportInitialStatus)}
              className="text-sm border border-gray-300 rounded-md px-2 py-1 focus:outline-none focus:ring-2 focus:ring-brand-400"
              title="Pick Scheduled to route the new orders immediately."
            >
              <option value="Ordered">Ordered</option>
              <option value="Scheduled">Scheduled (route now)</option>
            </select>
          </div>
          <button
            onClick={handleSubmit}
            disabled={submitting || submittableCount === 0}
            className="px-4 py-2 bg-brand-500 text-white text-sm font-medium rounded-lg hover:bg-brand-600 disabled:opacity-50"
          >
            {submitting ? 'Saving…' : `Save ${submittableCount} Order${submittableCount === 1 ? '' : 's'}`}
          </button>
        </div>
      </div>

      {submitError && (
        <div className="bg-red-50 border border-red-200 rounded-xl p-3 text-sm text-red-700">{submitError}</div>
      )}

      {result && <SubmitResultCard result={result} />}

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

// ---- Row status cell -------------------------------------------------------

function RowStatus({
  r,
  storesLoading,
  onCreateStore,
}: {
  r: ResolvedRow
  storesLoading: boolean
  onCreateStore: () => void
}) {
  if (r.isEmpty) return <span className="text-xs text-gray-300">—</span>
  if (storesLoading) return <span className="text-xs text-gray-400">Checking…</span>
  if (r.matched && r.warning === null) {
    return (
      <span className="inline-flex items-center gap-1.5 text-xs text-green-700">
        <span className="w-1.5 h-1.5 rounded-full bg-green-500" />
        {r.matched.name}
      </span>
    )
  }
  // No match (because license is filled but unknown) → show create-store button
  if (r.row.licenseNumber.trim() && !r.matched) {
    return (
      <span className="inline-flex items-center gap-2 text-xs text-red-600">
        <span>No match</span>
        <button onClick={onCreateStore} className="text-brand-600 hover:text-brand-800 underline">
          Create store
        </button>
      </span>
    )
  }
  return <span className="text-xs text-amber-700">{r.warning ?? '—'}</span>
}

// ---- Reused: inline Create Store dialog -----------------------------------

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
            Saved under your company. Address is geocoded so deliveries route correctly.
          </p>
        </div>

        <div className="px-6 py-4 grid grid-cols-2 gap-3">
          <Field label="Store Name *"     value={form.name}            onChange={(v) => update('name', v)} />
          <Field label="License Number *" value={form.licenseNumber}   onChange={(v) => update('licenseNumber', v)} />
          <Field label="Customer"         value={form.customer ?? ''}  onChange={(v) => update('customer', v)} />
          <div />
          <div className="col-span-2">
            <Field label="Street Address *" value={form.address} onChange={(v) => update('address', v)} />
          </div>
          <Field label="City" value={form.city ?? ''} onChange={(v) => update('city', v)} />
          <div className="grid grid-cols-2 gap-3">
            <Field label="State" value={form.state ?? ''} onChange={(v) => update('state', v)} />
            <Field label="ZIP"   value={form.zip ?? ''}   onChange={(v) => update('zip', v)} />
          </div>
          <Field label="County" value={form.county ?? ''} onChange={(v) => update('county', v)} />
          <Field label="Email"  value={form.email  ?? ''} onChange={(v) => update('email', v)} />
          <Field label="Phone"  value={form.phone  ?? ''} onChange={(v) => update('phone', v)} />
        </div>

        {error && (
          <div className="px-6 pb-2">
            <div className="bg-red-50 border border-red-200 rounded-lg px-3 py-2 text-sm text-red-700">{error}</div>
          </div>
        )}

        <div className="px-6 py-3 border-t border-gray-100 flex justify-end gap-2">
          <button onClick={onCancel} className="px-4 py-2 text-sm text-gray-600 hover:text-gray-900">Cancel</button>
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

// ---- Result card -----------------------------------------------------------

function SubmitResultCard({ result }: { result: ImportOrdersResult }) {
  const hasErrors  = result.errors.length > 0
  const routes     = result.routesQueued       ?? 0
  const omitted    = result.routeOmitted       ?? 0
  const stores     = result.storesGeocoded     ?? 0
  const hubs       = result.hubsGeocoded       ?? 0
  const warehouses = result.warehousesGeocoded ?? 0
  const failures   = result.geocodeFailures    ?? 0
  const ungeocoded = result.ordersUngeocoded   ?? 0
  const geocodeBits: string[] = []
  if (stores     > 0) geocodeBits.push(`${stores} store${stores === 1 ? '' : 's'}`)
  if (hubs       > 0) geocodeBits.push(`${hubs} hub${hubs === 1 ? '' : 's'}`)
  if (warehouses > 0) geocodeBits.push(`${warehouses} warehouse${warehouses === 1 ? '' : 's'}`)
  return (
    <div className={`rounded-xl border p-4 space-y-2 ${
      hasErrors ? 'bg-amber-50 border-amber-200' : 'bg-green-50 border-green-200'
    }`}>
      <p className="text-sm font-semibold text-gray-800">
        Saved {result.created} order{result.created === 1 ? '' : 's'}
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
            <li key={e.row}>Row {e.row + 1}: {e.message}</li>
          ))}
          {result.errors.length > 25 && <li>…and {result.errors.length - 25} more.</li>}
        </ul>
      )}
    </div>
  )
}

// ---- Helpers ---------------------------------------------------------------

function blankToUndef(v: string): string | undefined {
  const t = v.trim()
  return t.length === 0 ? undefined : t
}

function focusCell(rowIdx: number, colIdx: number) {
  // Schedule for next tick so React can render any newly-added rows first.
  setTimeout(() => {
    const el = document.getElementById(`cell-${rowIdx}-${colIdx}`) as HTMLInputElement | null
    el?.focus()
    el?.select()
  }, 0)
}
