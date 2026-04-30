// Minimal RFC-4180 CSV parser. Handles:
//   * comma-separated rows
//   * double-quoted cells, including embedded commas, quotes ("") and newlines
//   * \r\n and \n line endings
//
// Returns an array of rows; each row is an array of cell strings. Empty
// trailing rows are dropped so a stray newline at end-of-file doesn't
// produce a row of empty fields.
export function parseCsv(text: string): string[][] {
  const rows: string[][] = []
  let row: string[] = []
  let cell = ''
  let inQuotes = false
  let i = 0

  while (i < text.length) {
    const ch = text[i]

    if (inQuotes) {
      if (ch === '"') {
        if (text[i + 1] === '"') { cell += '"'; i += 2; continue }
        inQuotes = false
        i += 1
        continue
      }
      cell += ch
      i += 1
      continue
    }

    if (ch === '"') { inQuotes = true; i += 1; continue }
    if (ch === ',') { row.push(cell); cell = ''; i += 1; continue }
    if (ch === '\r') { i += 1; continue }
    if (ch === '\n') {
      row.push(cell); cell = ''
      rows.push(row); row = []
      i += 1
      continue
    }
    cell += ch
    i += 1
  }

  // Trailing cell + row
  if (cell.length > 0 || row.length > 0) {
    row.push(cell)
    rows.push(row)
  }

  // Drop fully empty trailing rows
  while (rows.length > 0 && rows[rows.length - 1].every((c) => c === '')) {
    rows.pop()
  }

  return rows
}
