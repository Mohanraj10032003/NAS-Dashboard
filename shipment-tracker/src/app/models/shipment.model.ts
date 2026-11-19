export type ResultType = 'PASS' | 'FAIL' | 'OTHER';

export interface ShipmentRecord {
  id: number;
  part: string;
  date: Date | null;
  dateKey: string; // yyyy-MM-dd (used for filtering / sorting)
  dateLabel: string; // human readable
  result: ResultType;
  resultRaw: string;
  source: string; // "file.xlsx › Sheet1"
  extra: Record<string, string>; // all remaining columns of the sheet
}

export interface AppInfo {
  id: string;
  name: string;
}

/** What the user asks the NAS for (API mode): only matching reports are read. */
export interface DataQuery {
  part: string; // empty = all parts
  from: string; // yyyy-MM-dd
  to: string;
}

/** What exists in an application's folder (from the folder names, no Excel file read). */
export interface Catalog {
  maxRangeDays?: number; // longest date range of one load (0 / missing = no limit)
  parts: string[];
  minDate: string;
  maxDate: string;
  fileCount: number;
}

/** The numbers of a selection - arrive first, before the table rows. */
export interface Summary {
  version: string;
  total: number;
  pass: number;
  fail: number;
  other: number;
  partCount: number;
  days: { date: string; pass: number; fail: number }[];
  topFails: { part: string; fails: number; total: number }[];
}

export interface DataState {
  tableLoading: boolean; // counts are shown, the table rows are still coming
  queried: boolean; // "Load data" has been pressed for this application
  catalogLoading: boolean; // reading the folder list
  note: string; // soft remarks (skipped files, NAS warning) - not an error
  appId: string; // selected application (drop-down), API mode only
  appName: string;
  mode: 'api' | 'browser'; // api = data comes from the .NET API, browser = folder picker
  folderPath: string; // full NAS path reported by the API
  apiOffline: boolean; // API could not be reached / NAS not readable
  supported: boolean; // File System Access API available (Chrome / Edge)
  loading: boolean;
  folderName: string;
  fileCount: number;
  processed: number;
  skippedSheets: number;
  error: string;
  lastRefreshed: Date | null;
  needsPermission: boolean; // saved folder found, but needs a click to re-grant access
  isDemo: boolean;
}
