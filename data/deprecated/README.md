Files in this folder are kept for reference only and are no longer imported.

- `stores.csv` — superseded by `data/store_zone.csv`, which combines store
  attributes with their zone + district assignment. The import script reads
  `store_zone.csv` directly; the larger legacy file lives here so historical
  rows can still be inspected if needed.
- `updated_warehouses.csv` — early draft of the warehouse seed. Replaced by
  `data/warehouses.csv`, which now also carries the `IsChicagoLand` column.
