import type { SortDir } from '../../hooks/useSortedRows'

/**
 * Clickable <th> that toggles sort direction for the table it belongs to.
 * Shows ↕ when inactive, ▲ when active asc, ▼ when active desc.
 */
export function SortHeader({
  label,
  sortKey,
  activeKey,
  dir,
  onClick,
  className = '',
}: {
  label: string
  sortKey: string
  activeKey?: string
  dir?: SortDir
  onClick: () => void
  className?: string
}) {
  const active = activeKey === sortKey
  const arrow = active ? (dir === 'asc' ? '▲' : '▼') : '↕'
  const arrowClass = active ? 'text-brand-600' : 'text-gray-300'

  return (
    <th
      scope="col"
      onClick={onClick}
      className={`px-4 py-3 text-left font-medium cursor-pointer select-none hover:bg-gray-100 transition-colors whitespace-nowrap ${className}`}
    >
      <span className="inline-flex items-center gap-1.5">
        {label}
        <span className={`text-[10px] leading-none ${arrowClass}`}>{arrow}</span>
      </span>
    </th>
  )
}
