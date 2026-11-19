import { ShipmentRecord } from '../models/shipment.model';

/**
 * Cleans up the "other columns" of the sheets so the table only shows useful, unique columns:
 *   - a column that repeats Part number / Date / Result / Source (also "Date_1", "Part Number 2") is dropped,
 *   - the same column spelled differently in different sheets ("Serial No", "serial no", "SERIAL_NO") becomes ONE column,
 *   - a column whose values are identical to another column is dropped,
 *   - a column that has no data at all is dropped.
 */

const norm = (s: string) => s.toLowerCase().replace(/[^a-z0-9]/g, '');
/** "Date_1", "Date 2" -> "Date" (header repeats get a number suffix) */
const stripCounter = (s: string) => s.trim().replace(/[\s_]+\d+$/, '');

/** names that are already shown as fixed columns */
const RESERVED = new Set([
  'partnumber', 'partno', 'partnum', 'partid', 'part', 'pn',
  'date', 'shipdate', 'shipmentdate', 'datetime', 'timestamp',
  'result', 'source', 'file', 'sheet',
]);

const MAX_COLUMNS = 40;

export interface TidyResult {
  records: ShipmentRecord[];
  columns: string[];
}

export function tidyColumns(input: ShipmentRecord[]): TidyResult {
  // 1) merge spelling variants, drop reserved names and empty cells
  const display = new Map<string, string>(); // normalised name -> first spelling seen
  const order: string[] = [];
  const cleaned: Record<string, string>[] = input.map((r) => {
    const out: Record<string, string> = {};
    for (const [rawName, rawVal] of Object.entries(r.extra ?? {})) {
      const name = rawName.trim();
      if (!name || name.startsWith('__EMPTY')) continue;
      if (RESERVED.has(norm(stripCounter(name)))) continue;
      const val = (rawVal ?? '').toString().trim();
      if (!val) continue;
      const key = norm(name);
      if (!key) continue;
      if (!display.has(key)) {
        display.set(key, name);
        order.push(key);
      }
      out[key] = val;
    }
    return out;
  });

  // 2) drop columns that copy another column (or the part / result column): wherever the column has a value,
  //    that value is exactly the same as in the other column
  const refPart = input.map((r) => (r.part ?? '').toString().trim());
  const refResult = input.map((r) => (r.resultRaw ?? '').toString().trim());
  const copies = (key: string, ref: (i: number) => string): boolean => {
    let seen = 0;
    for (let i = 0; i < cleaned.length; i++) {
      const v = cleaned[i][key];
      if (v === undefined) continue;
      if (v !== ref(i)) return false;
      seen++;
    }
    return seen > 0;
  };

  const keep: string[] = [];
  for (const key of order) {
    if (copies(key, (i) => refPart[i]) || copies(key, (i) => refResult[i])) continue;
    if (keep.some((k) => copies(key, (i) => cleaned[i][k] ?? ''))) continue;
    keep.push(key);
    if (keep.length >= MAX_COLUMNS) break;
  }

  // 3) rebuild the records with the tidy columns only (same order everywhere)
  const records = input.map((r, i) => {
    const extra: Record<string, string> = {};
    for (const key of keep) {
      const v = cleaned[i][key];
      if (v !== undefined) extra[display.get(key)!] = v;
    }
    return { ...r, extra };
  });

  return { records, columns: keep.map((k) => display.get(k)!) };
}
