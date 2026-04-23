import { useMemo, useState } from 'react'

export type SortDir = 'asc' | 'desc'
export type SortState = { key: string; dir: SortDir } | null

/**
 * Generic client-side sort state for tables. Any string column key is accepted;
 * columns that need a computed/nested value register an accessor. Keys without
 * an accessor fall back to `row[key]`.
 */
export function useSortedRows<T>(
  rows: readonly T[],
  opts?: {
    initial?: SortState
    accessors?: Record<string, (row: T) => unknown>
  },
) {
  const [sort, setSort] = useState<SortState>(opts?.initial ?? null)

  function toggle(key: string) {
    setSort((s) => {
      if (!s || s.key !== key) return { key, dir: 'asc' }
      return { key, dir: s.dir === 'asc' ? 'desc' : 'asc' }
    })
  }

  const sorted = useMemo(() => {
    if (!sort) return [...rows]
    const accessor = opts?.accessors?.[sort.key]
    const dir = sort.dir === 'asc' ? 1 : -1
    return [...rows].sort((a, b) => {
      const av = accessor ? accessor(a) : (a as Record<string, unknown>)[sort.key]
      const bv = accessor ? accessor(b) : (b as Record<string, unknown>)[sort.key]
      return compare(av, bv) * dir
    })
  }, [rows, sort, opts?.accessors])

  return {
    sorted,
    sortKey: sort?.key,
    sortDir: sort?.dir,
    toggle,
  }
}

function compare(a: unknown, b: unknown): number {
  const aNil = a === null || a === undefined || a === ''
  const bNil = b === null || b === undefined || b === ''
  if (aNil && bNil) return 0
  if (aNil) return 1   // empty values sink to the bottom regardless of direction
  if (bNil) return -1

  if (typeof a === 'number' && typeof b === 'number') return a - b
  if (typeof a === 'boolean' && typeof b === 'boolean') return Number(a) - Number(b)
  if (a instanceof Date && b instanceof Date) return a.getTime() - b.getTime()

  const as = String(a)
  const bs = String(b)
  // ISO-8601 timestamps sort correctly as strings, so this also handles dates
  // passed in as strings. `numeric` handles things like "IN00000026" vs "IN00000101".
  return as.localeCompare(bs, undefined, { numeric: true, sensitivity: 'base' })
}
