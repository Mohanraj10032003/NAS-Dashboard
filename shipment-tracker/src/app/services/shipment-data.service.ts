import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { BehaviorSubject, firstValueFrom } from 'rxjs';
import * as XLSX from 'xlsx';
import { APP_CONFIG } from '../config';
import { AppInfo, Catalog, DataQuery, DataState, ResultType, ShipmentRecord, Summary } from '../models/shipment.model';
import { tidyColumns } from './column-tidy';

interface FoundFile {
  file: File;
  path: string;
}

const DB_NAME = 'shipment-tracker';
const STORE = 'kv';
const HANDLE_KEY = 'nas-folder-handle';
const APP_KEY = 'nas-dashboard-app';

// ---- shape of the .NET API response (GET /api/shipments) ----
interface ApiRecord {
  id: number;
  part: string;
  date: string | null; // local time "yyyy-MM-ddTHH:mm:ss"
  result: ResultType;
  resultRaw: string;
  source: string;
  extra: Record<string, string>;
}
interface ApiCatalog {
  appId: string;
  appName: string;
  folder: string;
  fileCount: number;
  parts: string[];
  minDate: string | null;
  maxDate: string | null;
  warning: string | null;
}
interface ApiSummary extends Summary {
  appId: string;
  appName: string;
  folder: string;
  folderName: string;
  fileCount: number;
  sheetCount: number;
  skippedSheets: number;
  errors: string[];
  skipped: string[];
  warning: string | null;
}
interface ApiResponse {
  skipped: string[];
  totalRows: number;
  truncated: boolean;
  appId: string;
  appName: string;
  version: string;
  generatedAtUtc: string;
  folder: string;
  folderName: string;
  fileCount: number;
  sheetCount: number;
  skippedSheets: number;
  errors: string[];
  warning: string | null;
  records: ApiRecord[];
}

/**
 * Gives the dashboard its data. Two modes (APP_CONFIG.useApi):
 *  - API mode: the .NET API logs in to the NAS server, reads the Excel files and sends the rows as JSON.
 *  - Browser mode: reads Excel / CSV files from a NAS folder using the browser's File System Access API
 * (Chrome / Edge). A web page can never read a typed path like \\NAS\share on its own –
 * the user has to pick the folder once, and the browser remembers it afterwards.
 */
@Injectable({ providedIn: 'root' })
export class ShipmentDataService {
  private recordsSubject = new BehaviorSubject<ShipmentRecord[]>([]);
  records$ = this.recordsSubject.asObservable();

  /** unique, non-empty "other" columns of the current rows (duplicates already removed) */
  private columnsSubject = new BehaviorSubject<string[]>([]);
  columns$ = this.columnsSubject.asObservable();

  /** every path that publishes rows goes through here, so the columns are always tidied */
  private setRecords(rows: ShipmentRecord[]): void {
    const tidy = tidyColumns(rows);
    this.columnsSubject.next(tidy.columns);
    this.recordsSubject.next(tidy.records);
  }

  /** total / pass / fail / other of the current selection (arrives before the rows) */
  private summarySubject = new BehaviorSubject<Summary | null>(null);
  summary$ = this.summarySubject.asObservable();

  private stateSubject = new BehaviorSubject<DataState>({
    tableLoading: false,
    queried: false,
    catalogLoading: false,
    note: '',
    appId: '',
    appName: '',
    mode: APP_CONFIG.useApi ? 'api' : 'browser',
    folderPath: '',
    apiOffline: false,
    supported: typeof (window as any).showDirectoryPicker === 'function',
    loading: false,
    folderName: '',
    fileCount: 0,
    processed: 0,
    skippedSheets: 0,
    error: '',
    lastRefreshed: null,
    needsPermission: false,
    isDemo: false,
  });
  state$ = this.stateSubject.asObservable();

  private handle: any = null;
  private idCounter = 0;
  private apiVersion = '';
  private reqId = 0; // newest request wins when the user switches application quickly
  private catReq = 0;

  /** what exists in the selected application (parts, first / last date) - fills the drop-downs */
  private catalogSubject = new BehaviorSubject<Catalog | null>(null);
  catalog$ = this.catalogSubject.asObservable();
  lastQuery: DataQuery | null = null;
  private demoCache: ShipmentRecord[] = [];

  /** true = the user chooses application + part + dates and presses "Load data" (API, and the preview). false = old folder-picker mode. */
  get queryMode(): boolean {
    return APP_CONFIG.useApi || !!(APP_CONFIG as any).previewDemo;
  }

  /** the applications of the drop-down (API mode) */
  private appsSubject = new BehaviorSubject<AppInfo[]>([]);
  apps$ = this.appsSubject.asObservable();

  constructor(private http: HttpClient) {}

  get state(): DataState {
    return this.stateSubject.value;
  }

  get isApi(): boolean {
    return APP_CONFIG.useApi;
  }

  // ------------------------------------------------------------------ API mode

  /** Reads the list of applications from the API and picks the one used last time (or the first). */
  async loadApps(): Promise<void> {
    try {
      const apps = await firstValueFrom(this.http.get<AppInfo[]>(`${APP_CONFIG.apiBaseUrl}/api/apps`));
      this.appsSubject.next(apps);
      let saved = '';
      try {
        saved = localStorage.getItem(APP_KEY) ?? '';
      } catch {
        /* ignore */
      }
      const pick = apps.find((a) => a.id === saved) ?? apps[0];
      this.patch({ appId: pick?.id ?? '', appName: pick?.name ?? '', folderName: pick?.name ?? '' });
    } catch (e) {
      this.patch({ apiOffline: true, error: this.apiError(e as HttpErrorResponse) });
    }
  }

  /** The Settings page changed the list of applications: show the new list and read the folder list of the selected one. */
  async reloadApps(apps?: AppInfo[]): Promise<void> {
    if (this.isApi) {
      await this.loadApps();
    } else if (apps) {
      this.appsSubject.next(apps);
      const keep = apps.find((a) => a.id === this.state.appId) ?? apps[0];
      this.patch({ appId: keep?.id ?? '', appName: keep?.name ?? '', folderName: keep?.name ?? '' });
    }
    this.resetData();
    await this.loadCatalog(true);
  }

  /** The user picked another application in the drop-down: forget the old rows and read that application's folder list. */
  async selectApp(id: string): Promise<void> {
    const app = this.appsSubject.value.find((a) => a.id === id);
    if (!app || app.id === this.state.appId) return;
    if (this.isApi) {
      try {
        localStorage.setItem(APP_KEY, app.id);
      } catch {
        /* ignore */
      }
    }
    this.resetData();
    this.patch({ appId: app.id, appName: app.name, folderName: app.name });
    await this.loadCatalog();
  }

  /** Back to "nothing loaded yet" (new application selected). */
  private resetData(): void {
    this.reqId++;
    this.catReq++;
    this.apiVersion = '';
    this.lastQuery = null;
    this.catalogSubject.next(null);
    this.setRecords([]);
    this.summarySubject.next(null);
    this.patch({
      tableLoading: false,
      queried: false,
      loading: false,
      catalogLoading: false,
      fileCount: 0,
      skippedSheets: 0,
      apiOffline: false,
      error: '',
      note: '',
      folderPath: '',
      lastRefreshed: null,
    });
  }

  /**
   * Step 1: ask the NAS WHAT exists (part numbers, first / last date). Only folder names are listed, no Excel file is opened,
   * so this is quick. The result fills the Part number and date filters.
   */
  async loadCatalog(force = false): Promise<void> {
    const my = ++this.catReq;
    this.patch({ catalogLoading: true, error: '' });

    if (!this.isApi) {
      // preview: sample data
      this.demoCache = this.demoRows();
      const parts = Array.from(new Set(this.demoCache.map((r) => r.part))).sort();
      const keys = this.demoCache.map((r) => r.dateKey).sort();
      this.catalogSubject.next({ parts, minDate: keys[0] ?? '', maxDate: keys[keys.length - 1] ?? '', fileCount: 60, maxRangeDays: 31 });
      this.patch({ catalogLoading: false, isDemo: true, fileCount: 60, folderName: this.state.appName || 'Demo data' });
      return;
    }

    try {
      const q = `app=${encodeURIComponent(this.state.appId)}${force ? '&refresh=true' : ''}`;
      const res = await firstValueFrom(this.http.get<ApiCatalog>(`${APP_CONFIG.apiBaseUrl}/api/catalog?${q}`));
      if (my !== this.catReq) return; // the user already switched to another application
      this.catalogSubject.next({
        parts: res.parts,
        minDate: res.minDate ?? '',
        maxDate: res.maxDate ?? '',
        fileCount: res.fileCount,
      });
      this.patch({
        catalogLoading: false,
        apiOffline: !!res.warning,
        folderName: res.appName,
        folderPath: res.folder,
        fileCount: res.fileCount,
        note: res.warning ? `NAS not reachable right now, showing the saved copy of the reports. ${res.warning}` : '',
      });
    } catch (e) {
      if (my !== this.catReq) return;
      this.patch({ catalogLoading: false, apiOffline: true, error: this.apiError(e as HttpErrorResponse) });
    }
  }

  /**
   * Step 2: read the data. Only the reports that match the part number and date range are read from the NAS.
   */
  async load(query: DataQuery, force = false): Promise<void> {
    const my = ++this.reqId;
    this.lastQuery = { ...query };
    this.patch({ loading: true, error: '', note: '' });

    if (!this.isApi) {
      // preview: filter the sample data the same way the server would
      const p = query.part.trim().toLowerCase();
      const rows = this.demoCache.filter(
        (r) => (!p || r.part.toLowerCase().includes(p)) && (!query.from || r.dateKey >= query.from) && (!query.to || r.dateKey <= query.to)
      );
      await new Promise((r) => setTimeout(r, 450));
      if (my !== this.reqId) return;
      this.setRecords([]);
      this.patch({ loading: false, queried: true, tableLoading: rows.length > 0, fileCount: new Set(rows.map((r) => r.source)).size, lastRefreshed: new Date() });
      this.summarySubject.next(this.summarize(rows));
      await new Promise((r) => setTimeout(r, 1800)); // preview only: pretend the table takes longer
      if (my !== this.reqId) return;
      this.patch({ tableLoading: false, lastRefreshed: new Date() });
      this.setRecords(rows);
      return;
    }

    try {
      const params = new URLSearchParams({ app: this.state.appId });
      if (query.part.trim()) params.set('part', query.part.trim());
      if (query.from) params.set('from', query.from);
      if (query.to) params.set('to', query.to);
      if (force) params.set('refresh', 'true');

      // STEP A: only the numbers (total / pass / fail / other) - small, shown at once
      const sum = await firstValueFrom(this.http.get<ApiSummary>(`${APP_CONFIG.apiBaseUrl}/api/summary?${params}`));
      if (my !== this.reqId) return; // newer request / another application

      const same = sum.version === this.apiVersion && this.recordsSubject.value.length > 0;
      if (!same) {
        this.apiVersion = '';
        this.setRecords([]); // old rows must not sit under the new numbers
      }
      this.patch({
        loading: false,
        queried: true,
        tableLoading: !same && sum.total > 0,
        apiOffline: !!sum.warning,
        folderName: sum.appName || sum.folderName,
        folderPath: sum.folder,
        fileCount: sum.fileCount,
        processed: sum.fileCount,
        skippedSheets: sum.skippedSheets,
        lastRefreshed: new Date(),
        note: this.notesOf(sum.warning, sum.errors, sum.skipped),
      });
      this.summarySubject.next(sum); // after the state, so the dashboard knows the table is still loading
      if (same || sum.total === 0) return;

      // STEP B: the table rows (the server already read the files in step A, so this is mostly transfer time)
      params.delete('refresh');
      const res = await firstValueFrom(this.http.get<ApiResponse>(`${APP_CONFIG.apiBaseUrl}/api/shipments?${params}`));
      if (my !== this.reqId) return;
      this.apiVersion = res.version;
      this.patch({ tableLoading: false }); // before the rows, so the dashboard switches from the summary numbers to the real rows
      this.setRecords(res.records.map((r) => this.fromApi(r)));
      this.patch({
        skippedSheets: res.skippedSheets,
        lastRefreshed: new Date(),
        note: this.notesOf(res.warning, res.errors, res.skipped, res.truncated ? res.totalRows : 0, res.records.length),
      });
    } catch (e) {
      if (my !== this.reqId) return;
      this.patch({ loading: false, tableLoading: false, apiOffline: true, error: this.apiError(e as HttpErrorResponse) });
    }
  }

  private notesOf(warning: string | null, errors: string[], skipped: string[] = [], totalRows = 0, shownRows = 0): string {
    const notes: string[] = [];
    if (warning) notes.push(`NAS not reachable right now, showing the saved copy of the reports. ${warning}`);
    if (totalRows > shownRows && shownRows > 0)
      notes.push(`The table shows the first ${shownRows.toLocaleString()} of ${totalRows.toLocaleString()} rows. The numbers above count all of them. Choose fewer days to see every row.`);
    if (skipped.length) notes.push(`${skipped.length} file(s) were left out because they are damaged or still being saved.`);
    if (errors.length) {
      const first = errors[0];
      const cut = first.indexOf(': ');
      const name = (cut > 0 ? first.slice(0, cut) : first).split('/').pop();
      const why = cut > 0 ? first.slice(cut + 2) : '';
      notes.push(`${errors.length} file(s) could not be read (${name}${why ? ': ' + why : ''}).`);
    }
    return notes.join(' ');
  }

  /** preview only: the same numbers the server sends */
  private summarize(rows: ShipmentRecord[]): Summary {
    let pass = 0, fail = 0, other = 0;
    const days = new Map<string, { pass: number; fail: number }>();
    const parts = new Map<string, { fails: number; total: number }>();
    for (const r of rows) {
      if (r.result === 'PASS') pass++; else if (r.result === 'FAIL') fail++; else other++;
      if (r.dateKey) {
        const d = days.get(r.dateKey) ?? { pass: 0, fail: 0 };
        if (r.result === 'PASS') d.pass++; else if (r.result === 'FAIL') d.fail++;
        days.set(r.dateKey, d);
      }
      if (r.part) {
        const p = parts.get(r.part) ?? { fails: 0, total: 0 };
        p.total++;
        if (r.result === 'FAIL') p.fails++;
        parts.set(r.part, p);
      }
    }
    return {
      version: 'demo',
      total: rows.length, pass, fail, other, partCount: parts.size,
      days: Array.from(days.entries()).sort((a, b) => a[0].localeCompare(b[0])).slice(-14).map(([date, v]) => ({ date, ...v })),
      topFails: Array.from(parts.entries()).filter(([, v]) => v.fails > 0).map(([part, v]) => ({ part, ...v }))
        .sort((a, b) => b.fails - a.fails).slice(0, 5),
    };
  }

  private apiError(err: HttpErrorResponse): string {
    if (err.status === 0) {
      return `Cannot reach the NAS Dashboard API at ${APP_CONFIG.apiBaseUrl || location.origin}. Check that it is running.`;
    }
    if (err.status === 401) return 'Your session expired. Please sign in again.';
    if (err.status === 503) return err.error?.message ?? 'The NAS server is not reachable.';
    return err.error?.message ?? `The server returned an error (${err.status}).`;
  }

  private fromApi(r: ApiRecord): ShipmentRecord {
    const date = r.date ? this.toDate(r.date) : null;
    return {
      id: r.id,
      part: r.part,
      date,
      dateKey: date ? this.ymd(date) : '',
      dateLabel: date ? this.fmt(date) : '',
      result: r.result,
      resultRaw: r.resultRaw,
      source: r.source,
      extra: r.extra ?? {},
    };
  }

  // ------------------------------------------------------------------ public API

  /** Called when the dashboard opens: try to re-open the folder used last time. */
  async restore(): Promise<void> {
    if (this.isApi) {
      if (!this.appsSubject.value.length) await this.loadApps();
      if (!this.catalogSubject.value) await this.loadCatalog();
      return;
    }
    if (!this.state.supported || this.handle || this.recordsSubject.value.length) return;
    try {
      const saved = await this.kvGet(HANDLE_KEY);
      if (!saved) return;
      this.handle = saved;
      this.patch({ folderName: saved.name });
      const perm = await saved.queryPermission({ mode: 'read' });
      if (perm === 'granted') {
        await this.refresh();
      } else {
        this.patch({ needsPermission: true });
      }
    } catch {
      /* nothing saved or not allowed – user will click the button */
    }
  }

  /** Opens the folder picker – this is the "enter NAS path" step. */
  async pickFolder(): Promise<void> {
    if (!this.state.supported) return;
    try {
      const handle = await (window as any).showDirectoryPicker({ mode: 'read' });
      this.handle = handle;
      await this.kvSet(HANDLE_KEY, handle);
      this.patch({ folderName: handle.name, needsPermission: false, isDemo: false, error: '' });
      await this.refresh();
    } catch (e: any) {
      if (e?.name !== 'AbortError') {
        this.patch({ error: 'Could not open the folder: ' + (e?.message ?? e) });
      }
    }
  }

  /** User clicked "Reconnect" – browsers need a click to re-grant folder permission. */
  async reconnect(): Promise<void> {
    if (!this.handle) return this.pickFolder();
    try {
      const perm = await this.handle.requestPermission({ mode: 'read' });
      if (perm === 'granted') {
        this.patch({ needsPermission: false, error: '' });
        await this.refresh();
      } else {
        this.patch({ error: 'Permission to read the folder was not granted.' });
      }
    } catch (e: any) {
      this.patch({ error: 'Could not reconnect: ' + (e?.message ?? e) });
    }
  }

  /**
   * Re-reads the data. API mode: asks the API (force = make the API re-scan the NAS right now).
   * Browser mode: re-reads every Excel file in the connected folder.
   */
  async refresh(force = false): Promise<void> {
    if (this.queryMode) {
      // "Refresh": run the same question again (changed reports are re-read) - or re-list the folders when nothing was loaded yet
      if (this.lastQuery) return this.load(this.lastQuery, force);
      return this.loadCatalog(force);
    }
    if (!this.handle || this.state.loading) return;
    this.patch({ loading: true, error: '', processed: 0, fileCount: 0, skippedSheets: 0, isDemo: false });
    try {
      const files: FoundFile[] = [];
      await this.walk(this.handle, '', files, 0);
      this.patch({ fileCount: files.length });
      await this.loadFiles(files);
    } catch (e: any) {
      const denied = e?.name === 'NotAllowedError' || e?.name === 'SecurityError';
      this.patch({
        loading: false,
        needsPermission: denied,
        error: denied
          ? 'Access to the folder expired. Click Reconnect.'
          : 'Failed to read folder: ' + (e?.message ?? e),
      });
    }
  }

  /** Fallback for browsers without the folder API (or drag & drop): load chosen files. */
  async loadFileList(list: FileList | File[]): Promise<void> {
    const files: FoundFile[] = Array.from(list)
      .filter((f) => /\.(xlsx|xlsm|xls|csv)$/i.test(f.name) && !f.name.startsWith('~$'))
      .map((f) => ({ file: f, path: (f as any).webkitRelativePath || f.name }));
    this.handle = null;
    this.patch({
      loading: true,
      error: '',
      processed: 0,
      fileCount: files.length,
      skippedSheets: 0,
      folderName: files.length ? 'Selected files' : '',
      needsPermission: false,
      isDemo: false,
    });
    await this.loadFiles(files);
  }

  /** Sample data so the UI can be explored before the NAS path is connected. */
  /** Preview only: shows the application drop-down with sample data (no API needed). */
  enableDemoApps(): void {
    const apps = [
      { id: 'diagcam', name: 'DiagCAM' },
      { id: 'calibration', name: 'Calibration' },
      { id: 'blemish', name: 'Blemish' },
      { id: 'firmware', name: 'Parallel Firmware Flasher' },
      { id: 'ezee-focus', name: 'e-zee Focus' },
      { id: 'active-alignment', name: 'Active Alignment' },
      { id: 'carrier-board', name: 'Carrier Board' },
      { id: 'leak-tester', name: 'Leak_Tester' },
      { id: 'new-foco', name: 'New_Foco' },
    ];
    this.appsSubject.next(apps);
    this.patch({ appId: apps[0].id, appName: apps[0].name, folderName: apps[0].name });
    void this.loadCatalog();
  }

  private demoRows(): ShipmentRecord[] {
    const parts = ['ECAM-AR0234-MIPI', 'ECAM-IMX307-USB', 'ECAM-OV5640-MIPI', 'ECAM-AR0521-USB', 'ECAM-IMX415-MIPI', 'ECAM-AR1335-USB'];
    const rows: ShipmentRecord[] = [];
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    let seed = 42 + [...this.state.appId].reduce((a, c) => a + c.charCodeAt(0), 0);
    const rnd = () => {
      seed = (seed * 16807) % 2147483647;
      return seed / 2147483647;
    };
    for (let d = 0; d < 60; d++) {
      const day = new Date(today);
      day.setDate(today.getDate() - d);
      const count = 6 + Math.floor(rnd() * 10);
      for (let i = 0; i < count; i++) {
        const part = parts[Math.floor(rnd() * parts.length)];
        const pass = rnd() > 0.18;
        const stamp = new Date(day);
        stamp.setHours(8 + Math.floor(rnd() * 10), Math.floor(rnd() * 60));
        rows.push(
          this.makeRecord(
            part,
            stamp,
            pass ? 'PASS' : 'FAIL',
            `Shipment_${this.ymd(day)}.xlsx › Sheet1`,
            {
              'Batch No': 'B' + (1000 + Math.floor(rnd() * 900)),
              Qty: String(10 + Math.floor(rnd() * 90)),
              Operator: ['Arun', 'Meena', 'Karthik', 'Divya'][Math.floor(rnd() * 4)],
            }
          )
        );
      }
    }
    return rows;
  }

  loadDemo(): void {
    const rows = this.demoRows();
    this.handle = null;
    this.setRecords(rows);
    this.patch({
      loading: false,
      isDemo: true,
      folderName: this.state.appName || 'Demo data',
      fileCount: 30,
      processed: 30,
      skippedSheets: 0,
      error: '',
      needsPermission: false,
      lastRefreshed: new Date(),
    });
  }

  async disconnect(): Promise<void> {
    this.handle = null;
    try {
      await this.kvSet(HANDLE_KEY, null);
    } catch {
      /* ignore */
    }
    this.setRecords([]);
    this.patch({
      folderName: '',
      fileCount: 0,
      processed: 0,
      skippedSheets: 0,
      error: '',
      needsPermission: false,
      lastRefreshed: null,
      isDemo: false,
    });
  }

  // ------------------------------------------------------------------ reading

  private async walk(dir: any, path: string, out: FoundFile[], depth: number): Promise<void> {
    for await (const [name, h] of dir.entries()) {
      if (h.kind === 'directory') {
        if (depth < 6 && !name.startsWith('.') && !name.startsWith('$')) {
          await this.walk(h, path + name + '/', out, depth + 1);
        }
      } else if (/\.(xlsx|xlsm|xls|csv)$/i.test(name) && !name.startsWith('~$')) {
        out.push({ file: await h.getFile(), path: path + name });
      }
    }
  }

  private async loadFiles(files: FoundFile[]): Promise<void> {
    const all: ShipmentRecord[] = [];
    let skipped = 0;
    let errors = 0;

    for (let i = 0; i < files.length; i++) {
      try {
        const buf = await files[i].file.arrayBuffer();
        // cellDates stays OFF: SheetJS Date objects drift by seconds / a day in some time zones.
        // We read the raw serial numbers ourselves (exact) plus the formatted text Excel shows.
        const wb = XLSX.read(buf, { type: 'array', cellDates: false });
        for (const sheetName of wb.SheetNames) {
          const ws = wb.Sheets[sheetName];
          const rows = XLSX.utils.sheet_to_json<Record<string, any>>(ws, { defval: '', raw: true });
          const shown = XLSX.utils.sheet_to_json<Record<string, any>>(ws, { defval: '', raw: false });
          const parsed = this.parseSheet(rows, shown, `${files[i].path} › ${sheetName}`);
          if (parsed === null) skipped++;
          else all.push(...parsed);
        }
      } catch {
        errors++;
      }
      this.patch({ processed: i + 1 });
      // let the UI breathe on big folders
      if (i % 3 === 0) await new Promise((r) => setTimeout(r));
    }

    this.setRecords(all);
    this.patch({
      loading: false,
      skippedSheets: skipped,
      lastRefreshed: new Date(),
      error: errors ? `${errors} file(s) could not be read (maybe open/locked or corrupted).` : '',
    });
  }

  /** Returns null when the sheet does not look like a shipment sheet. */
  private parseSheet(
    rows: Record<string, any>[],
    shown: Record<string, any>[],
    source: string
  ): ShipmentRecord[] | null {
    if (!rows.length) return [];

    const headerSet = new Set<string>();
    rows.slice(0, 50).forEach((r) => Object.keys(r).forEach((k) => headerSet.add(k)));
    const headers = Array.from(headerSet);

    const partCol = this.findColumn(headers, ['partnumber', 'partno', 'partnum', 'partid', 'part', 'pn', 'material', 'item'], ['part']);
    const dateCol = this.findColumn(headers, ['date', 'shipdate', 'shipmentdate', 'datetime', 'timestamp'], ['date', 'time']);
    const resultCol = this.findColumn(headers, ['passfail', 'result', 'status', 'pass', 'qcstatus'], ['pass', 'result', 'status']);

    if (!partCol && !resultCol) return null;

    const out: ShipmentRecord[] = [];
    const aligned = shown.length === rows.length;
    for (let ri = 0; ri < rows.length; ri++) {
      const row = rows[ri];
      const shownRow = aligned ? shown[ri] : row;
      const partVal = partCol ? String(row[partCol] ?? '').trim() : '';
      const resultRaw = resultCol ? String(row[resultCol] ?? '').trim() : '';
      if (!partVal && !resultRaw) continue;

      const date = dateCol ? this.toDate(row[dateCol]) : null;
      const extra: Record<string, string> = {};
      for (const h of headers) {
        if (h === partCol || h === dateCol || h === resultCol) continue;
        if (h.startsWith('__EMPTY')) continue;
        extra[h] = this.cellToString(shownRow[h]);
      }
      out.push(this.makeRecord(partVal, date, resultRaw, source, extra));
    }
    return out;
  }

  private findColumn(headers: string[], exact: string[], contains: string[]): string | null {
    const norm = headers.map((h) => ({ h, n: h.toLowerCase().replace(/[^a-z0-9]/g, '') }));
    for (const e of exact) {
      const hit = norm.find((x) => x.n === e);
      if (hit) return hit.h;
    }
    for (const c of contains) {
      const hit = norm.find((x) => x.n.includes(c));
      if (hit) return hit.h;
    }
    return null;
  }

  private makeRecord(
    part: string,
    date: Date | null,
    resultRaw: string,
    source: string,
    extra: Record<string, string>
  ): ShipmentRecord {
    return {
      id: ++this.idCounter,
      part,
      date,
      dateKey: date ? this.ymd(date) : '',
      dateLabel: date ? this.fmt(date) : '',
      result: this.toResult(resultRaw),
      resultRaw,
      source,
      extra,
    };
  }

  // ------------------------------------------------------------------ helpers

  private toResult(v: string): ResultType {
    const s = v.trim().toLowerCase();
    if (['pass', 'passed', 'ok', 'p', 'true', '1', 'yes', 'good', 'accepted'].includes(s)) return 'PASS';
    if (['fail', 'failed', 'ng', 'f', 'false', '0', 'no', 'bad', 'reject', 'rejected'].includes(s)) return 'FAIL';
    return 'OTHER';
  }

  private toDate(v: any): Date | null {
    if (v === null || v === undefined || v === '') return null;
    if (v instanceof Date) return isNaN(v.getTime()) ? null : v;

    // Excel serial number -> local wall-clock date (no time-zone drift)
    if (typeof v === 'number') {
      if (!isFinite(v) || v < 1) return null; // time-only cell, no date
      const u = new Date(Date.UTC(1899, 11, 30) + Math.round(v * 86400) * 1000);
      return new Date(u.getUTCFullYear(), u.getUTCMonth(), u.getUTCDate(), u.getUTCHours(), u.getUTCMinutes(), u.getUTCSeconds());
    }

    const s = String(v).trim();
    // yyyy-mm-dd (optionally with time) – parsed as LOCAL time, never UTC
    let m = s.match(/^(\d{4})[\/\-.](\d{1,2})[\/\-.](\d{1,2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?/);
    if (m) {
      const d = new Date(+m[1], +m[2] - 1, +m[3], +(m[4] ?? 0), +(m[5] ?? 0), +(m[6] ?? 0));
      return isNaN(d.getTime()) ? null : d;
    }
    // dd-mm-yyyy or dd/mm/yyyy (optionally with time)
    m = s.match(/^(\d{1,2})[\/\-.](\d{1,2})[\/\-.](\d{4})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2}))?)?/);
    if (m) {
      const d = new Date(+m[3], +m[2] - 1, +m[1], +(m[4] ?? 0), +(m[5] ?? 0), +(m[6] ?? 0));
      return isNaN(d.getTime()) ? null : d;
    }
    const d = new Date(s);
    return isNaN(d.getTime()) ? null : d;
  }

  private cellToString(v: any): string {
    if (v === null || v === undefined) return '';
    if (v instanceof Date) return this.fmt(v);
    // Excel shows whole dates as "2026-10-23 0:00:00" – drop the empty time part
    return String(v).replace(/ 0{1,2}:00:00$/, '');
  }

  private ymd(d: Date): string {
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${d.getFullYear()}-${mm}-${dd}`;
  }

  private fmt(d: Date): string {
    const date = d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
    if (d.getHours() === 0 && d.getMinutes() === 0) return date;
    const time = d.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
    return `${date} · ${time}`;
  }

  private patch(p: Partial<DataState>): void {
    this.stateSubject.next({ ...this.stateSubject.value, ...p });
  }

  // ------------------------------------------------------------------ IndexedDB (remember the folder)

  private openDb(): Promise<IDBDatabase> {
    return new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, 1);
      req.onupgradeneeded = () => req.result.createObjectStore(STORE);
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }

  private async kvGet(key: string): Promise<any> {
    const db = await this.openDb();
    return new Promise((resolve, reject) => {
      const req = db.transaction(STORE, 'readonly').objectStore(STORE).get(key);
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }

  private async kvSet(key: string, value: any): Promise<void> {
    const db = await this.openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put(value, key);
      tx.oncomplete = () => resolve();
      tx.onerror = () => reject(tx.error);
    });
  }
}
