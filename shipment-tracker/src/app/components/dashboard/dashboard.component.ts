import { Component, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription } from 'rxjs';
import { APP_CONFIG } from '../../config';
import { AppInfo, Catalog, DataState, ResultType, ShipmentRecord, Summary } from '../../models/shipment.model';
import { AuthService } from '../../services/auth.service';
import { ShipmentDataService } from '../../services/shipment-data.service';

type SortKey = 'part' | 'date' | 'result' | 'source';
type ResultFilter = 'ALL' | ResultType;

interface TrendBar {
  key: string;
  label: string;
  pass: number;
  fail: number;
  total: number;
  h: number; // bar height, % of the tallest day
}

interface TopFail {
  part: string;
  fails: number;
  total: number;
  rate: number;
}

@Component({
  selector: 'app-dashboard',
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.scss'],
})
export class DashboardComponent implements OnInit, OnDestroy {
  appName = APP_CONFIG.appName;
  companyName = APP_CONFIG.companyName;
  userName = '';
  userRole = '';

  state!: DataState;
  apps: AppInfo[] = [];
  catalog: Catalog | null = null;
  records: ShipmentRecord[] = [];
  summary: Summary | null = null; // numbers of the selection; they arrive before the rows
  filtered: ShipmentRecord[] = [];
  paged: ShipmentRecord[] = [];

  // lists for filter dropdowns & table
  parts: string[] = [];
  sources: string[] = [];
  extraColumns: string[] = [];

  filters = {
    part: '',
    dateFrom: '',
    dateTo: '',
    result: 'ALL' as ResultFilter,
    source: 'ALL',
  };

  sortKey: SortKey = 'date';
  sortDir: 1 | -1 = -1;
  page = 1;
  pageSize = 25;
  pageSizes = [10, 25, 50, 100];

  kpi = { total: 0, pass: 0, fail: 0, other: 0, rate: 0 };
  trend: TrendBar[] = [];
  trendMax = 0; // value of the tallest bar (top of the chart)
  hasDate = false; // does any row have a date? (empty columns are not shown)
  hasResult = false;
  partCount = 0;
  openRows = new Set<number>(); // mobile cards that are expanded
  topFails: TopFail[] = [];

  autoRefresh = false;
  dragOver = false;

  private subs: Subscription[] = [];
  private timer: any = null;

  constructor(
    private auth: AuthService,
    private data: ShipmentDataService,
    private router: Router
  ) {}

  ngOnInit(): void {
    const u = this.auth.user;
    this.userName = u?.name ?? '';
    this.userRole = u?.role ?? '';

    this.subs.push(
      this.data.state$.subscribe((s) => (this.state = s)),
      this.data.columns$.subscribe((c) => (this.extraColumns = c)),
      this.data.apps$.subscribe((a) => (this.apps = a)),
      this.data.catalog$.subscribe((c) => {
        this.catalog = c;
        if (c && !this.filters.dateFrom && !this.filters.dateTo) this.defaultRange();
      }),
      this.data.summary$.subscribe((s) => {
        this.summary = s;
        if (s && this.state.tableLoading) this.showSummary(s);
      }),
      this.data.records$.subscribe((r) => {
        this.records = r;
        this.buildLookups();
        this.apply(false);
      })
    );
    this.data.restore().then(() => {
      if (this.isApi && !this.autoRefresh) this.toggleAuto(); // API mode: keep the loaded data fresh by default
    });
  }

  ngOnDestroy(): void {
    this.subs.forEach((s) => s.unsubscribe());
    this.stopTimer();
  }

  /** Always the current year, so the footer rolls over on 1 January by itself. */
  get year(): number {
    return new Date().getFullYear();
  }

  /** the dashboard (numbers + charts) is shown as soon as the rows OR just the summary numbers are there */
  get showResults(): boolean {
    return this.records.length > 0 || (this.queryMode && !!this.summary && this.summary.total > 0);
  }

  get tableCols(): number {
    return 3 + (this.hasDate ? 1 : 0) + (this.hasResult ? 1 : 0) + this.extraColumns.length;
  }

  /** chart helper: y-axis tick values */
  get ticks(): number[] {
    const m = this.trendMax;
    return [m, Math.round(m / 2), 0];
  }

  /** API mode (and the preview): the user fills Application + Part number + dates, then presses "Load data". */
  get queryMode(): boolean {
    return this.data.queryMode;
  }

  /** the part numbers offered in the filter: from the folder names (query mode) or from the loaded rows */
  get partOptions(): string[] {
    return this.queryMode && this.catalog ? this.catalog.parts : this.parts;
  }

  get canLoad(): boolean {
    const f = this.filters;
    return !this.state.loading && !this.state.catalogLoading && !!f.dateFrom && !!f.dateTo && f.dateFrom <= f.dateTo && !this.rangeTooLong;
  }

  /** days between From and To (both included) */
  get rangeDays(): number {
    const f = this.filters;
    if (!f.dateFrom || !f.dateTo) return 0;
    return Math.round((Date.parse(f.dateTo) - Date.parse(f.dateFrom)) / 86400000) + 1;
  }

  /** the server allows only a limited date range per load (loading ALL data would be lakhs of rows) */
  get maxRangeDays(): number {
    return this.queryMode ? this.catalog?.maxRangeDays ?? 0 : 0;
  }

  get rangeTooLong(): boolean {
    return this.maxRangeDays > 0 && this.rangeDays > this.maxRangeDays;
  }

  /** the filters differ from what is currently shown */
  get dirty(): boolean {
    const q = this.data.lastQuery;
    const f = this.filters;
    return !q || q.part.trim() !== f.part.trim() || q.from !== f.dateFrom || q.to !== f.dateTo;
  }

  /** "Load data": ask the NAS for the chosen part number and dates only. */
  load(): void {
    if (!this.canLoad) return;
    this.page = 1;
    this.openRows.clear();
    this.data.load({ part: this.filters.part, from: this.filters.dateFrom, to: this.filters.dateTo });
  }

  /** the part / date inputs: in query mode they wait for "Load data", otherwise they filter the loaded rows at once */
  onFilterChange(): void {
    if (!this.queryMode) this.apply();
  }

  /** newest 7 days of data that exists (so the first click on "Load data" shows something) */
  private defaultRange(): void {
    const max = this.catalog?.maxDate;
    if (!max) return;
    const end = new Date(max + 'T00:00:00');
    const start = new Date(end);
    start.setDate(end.getDate() - 6);
    const from = this.ymd(start);
    this.filters.dateTo = max;
    this.filters.dateFrom = this.catalog?.minDate && from < this.catalog.minDate ? this.catalog.minDate : from;
  }

  get isApi(): boolean {
    return this.state.mode === 'api';
  }

  get judged(): number {
    return this.kpi.pass + this.kpi.fail;
  }

  get activeFilterCount(): number {
    const f = this.filters;
    if (this.queryMode) return [f.result !== 'ALL'].filter(Boolean).length;
    return [f.part, f.dateFrom || f.dateTo, f.result !== 'ALL', f.source !== 'ALL'].filter(Boolean).length;
  }

  isOpen(id: number): boolean {
    return this.openRows.has(id);
  }
  toggleOpen(id: number): void {
    if (!this.openRows.delete(id)) this.openRows.add(id);
  }

  // ------------------------------------------------------------------ actions

  connect(): void {
    this.data.pickFolder();
  }
  reconnect(): void {
    this.data.reconnect();
  }
  /** The user picked another application in the drop-down. */
  changeApp(id: string): void {
    this.filters = { part: '', dateFrom: '', dateTo: '', result: 'ALL', source: 'ALL' };
    this.openRows.clear();
    this.page = 1;
    this.data.selectApp(id);
  }
  refresh(): void {
    this.data.refresh(true); // API mode: ask the server to re-scan the NAS now
  }
  demo(): void {
    this.data.loadDemo();
  }
  disconnect(): void {
    this.stopTimer();
    this.autoRefresh = false;
    this.data.disconnect();
  }

  onFilesPicked(ev: Event): void {
    const input = ev.target as HTMLInputElement;
    if (input.files?.length) this.data.loadFileList(input.files);
    input.value = '';
  }

  onDrop(ev: DragEvent): void {
    ev.preventDefault();
    if (this.isApi) return;
    this.dragOver = false;
    const files = ev.dataTransfer?.files;
    if (files?.length) this.data.loadFileList(files);
  }
  onDragOver(ev: DragEvent): void {
    ev.preventDefault();
    if (this.isApi) return;
    this.dragOver = true;
  }

  toggleAuto(): void {
    this.autoRefresh = !this.autoRefresh;
    if (this.autoRefresh) {
      this.timer = setInterval(() => {
        const ready = this.queryMode ? this.state.queried : !!this.state.folderName;
        if (!this.state.loading && !this.state.needsPermission && ready && !this.state.isDemo) {
          this.data.refresh();
        }
      }, APP_CONFIG.autoRefreshSeconds * 1000);
    } else {
      this.stopTimer();
    }
  }

  /** Settings (NAS paths) are for Admins, and need the API or the preview. */
  get canManage(): boolean {
    return this.queryMode && this.userRole === 'Admin';
  }
  openSettings(): void {
    this.router.navigate(['/settings']);
  }

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/login']);
  }

  // ------------------------------------------------------------------ filtering

  apply(resetPage = true): void {
    const q = this.filters.part.trim().toLowerCase();
    const { dateFrom, dateTo, result, source } = this.filters;

    const server = this.queryMode; // part + dates were already applied by the server (only matching reports were read)
    this.filtered = this.records.filter((r) => {
      if (!server && q && !r.part.toLowerCase().includes(q)) return false;
      if (result !== 'ALL' && r.result !== result) return false;
      if (source !== 'ALL' && r.source !== source) return false;
      if (!server && dateFrom && (!r.dateKey || r.dateKey < dateFrom)) return false;
      if (!server && dateTo && (!r.dateKey || r.dateKey > dateTo)) return false;
      return true;
    });

    this.sortRows();
    if (resetPage) this.page = 1;
    this.clampPage();
    this.computeStats();
    this.slice();
  }

  setResult(r: ResultFilter): void {
    this.filters.result = r;
    this.apply();
  }

  quickRange(kind: 'today' | '7' | '30' /* | 'all' */): void {
    const end = new Date();
    const start = new Date();
    /* ---- "All time" is switched off: loading ALL data of an application is lakhs of rows and does not answer in time.
          To bring it back, un-comment this block, the matching button in dashboard.component.html, add 'all' to the type above,
          and set Nas:AllowAllData to true in the API's appsettings.json.
    if (kind === 'all') {
      this.filters.dateFrom = this.queryMode ? this.catalog?.minDate ?? '' : '';
      this.filters.dateTo = this.queryMode ? this.catalog?.maxDate ?? '' : '';
    } else {
    */
    if (kind === '7') start.setDate(end.getDate() - 6);
    if (kind === '30') start.setDate(end.getDate() - 29);
    this.filters.dateFrom = this.ymd(start);
    this.filters.dateTo = this.ymd(end);
    /* } */
    this.onFilterChange();
  }

  resetFilters(): void {
    this.filters = { part: '', dateFrom: '', dateTo: '', result: 'ALL', source: 'ALL' };
    if (this.queryMode) this.defaultRange();
    this.apply();
  }

  get hasActiveFilters(): boolean {
    const f = this.filters;
    if (this.queryMode) return f.result !== 'ALL';
    return !!(f.part || f.dateFrom || f.dateTo || f.result !== 'ALL' || f.source !== 'ALL');
  }

  pickPart(p: string): void {
    this.filters.part = p;
    if (this.queryMode) return; // the list shows sheet values; loading is by folder part number
    this.apply();
  }

  // ------------------------------------------------------------------ sorting / paging

  sortBy(key: SortKey): void {
    if (this.sortKey === key) this.sortDir = this.sortDir === 1 ? -1 : 1;
    else {
      this.sortKey = key;
      this.sortDir = key === 'date' ? -1 : 1;
    }
    this.sortRows();
    this.slice();
  }

  setSortKey(key: string): void {
    this.sortKey = key as SortKey;
    this.sortDir = key === 'date' ? -1 : 1;
    this.sortRows();
    this.slice();
  }
  flipSort(): void {
    this.sortDir = this.sortDir === 1 ? -1 : 1;
    this.sortRows();
    this.slice();
  }

  arrow(key: SortKey): string {
    if (this.sortKey !== key) return '';
    return this.sortDir === 1 ? '▲' : '▼';
  }

  get totalPages(): number {
    return Math.max(1, Math.ceil(this.filtered.length / this.pageSize));
  }
  get rangeStart(): number {
    return this.filtered.length ? (this.page - 1) * this.pageSize + 1 : 0;
  }
  get rangeEnd(): number {
    return Math.min(this.page * this.pageSize, this.filtered.length);
  }

  go(p: number): void {
    this.page = p;
    this.clampPage();
    this.slice();
  }
  setPageSize(n: number): void {
    this.pageSize = +n;
    this.page = 1;
    this.slice();
  }

  // ------------------------------------------------------------------ export

  exportCsv(): void {
    const cols = ['Part Number', ...(this.hasDate ? ['Date'] : []), ...(this.hasResult ? ['Result'] : []), ...this.extraColumns, 'Source'];
    const esc = (v: string) => `"${(v ?? '').replace(/"/g, '""')}"`;
    const lines = [cols.map(esc).join(',')];
    for (const r of this.filtered) {
      lines.push(
        [
          r.part,
          ...(this.hasDate ? [r.dateLabel] : []),
          ...(this.hasResult ? [r.resultRaw || r.result] : []),
          ...this.extraColumns.map((c) => r.extra[c] ?? ''),
          r.source,
        ]
          .map(esc)
          .join(',')
      );
    }
    const blob = new Blob(['﻿' + lines.join('\r\n')], { type: 'text/csv;charset=utf-8' });
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = `nas-dashboard-${this.ymd(new Date())}.csv`;
    a.click();
    URL.revokeObjectURL(a.href);
  }

  // ------------------------------------------------------------------ internals

  trackById(_: number, r: ShipmentRecord): number {
    return r.id;
  }
  trackByKey(_: number, b: TrendBar): string {
    return b.key;
  }

  private buildLookups(): void {
    this.parts = Array.from(new Set(this.records.map((r) => r.part).filter(Boolean))).sort((a, b) =>
      a.localeCompare(b, undefined, { numeric: true })
    );
    this.sources = Array.from(new Set(this.records.map((r) => r.source))).sort();
    if (this.filters.source !== 'ALL' && !this.sources.includes(this.filters.source)) {
      this.filters.source = 'ALL';
    }
    this.partCount = this.parts.length;
    this.hasDate = this.records.some((r) => !!r.dateKey);
    this.hasResult = this.records.some((r) => !!r.resultRaw);
    if (!this.hasDate && this.sortKey === 'date') {
      this.sortKey = 'part';
      this.sortDir = 1;
    }
    if (!this.hasResult && this.sortKey === 'result') this.sortKey = 'part';
  }

  private sortRows(): void {
    const k = this.sortKey;
    const dir = this.sortDir;
    this.filtered = [...this.filtered].sort((a, b) => {
      let c = 0;
      if (k === 'part') c = a.part.localeCompare(b.part, undefined, { numeric: true });
      else if (k === 'date') c = (a.date?.getTime() ?? 0) - (b.date?.getTime() ?? 0);
      else if (k === 'result') c = a.result.localeCompare(b.result);
      else c = a.source.localeCompare(b.source);
      return c * dir;
    });
  }

  /** numbers and charts straight from the server's summary (the table rows are still on their way) */
  private showSummary(s: Summary): void {
    const judged = s.pass + s.fail;
    this.kpi = { total: s.total, pass: s.pass, fail: s.fail, other: s.other, rate: judged ? Math.round((s.pass / judged) * 1000) / 10 : 0 };
    this.partCount = s.partCount;
    this.hasDate = s.days.length > 0;
    this.hasResult = s.total > 0;
    const max = Math.max(1, ...s.days.map((d) => d.pass + d.fail));
    this.trendMax = max;
    this.trend = s.days.map((d) => {
      const [, m, day] = d.date.split('-');
      return { key: d.date, label: `${day}/${m}`, pass: d.pass, fail: d.fail, total: d.pass + d.fail, h: ((d.pass + d.fail) / max) * 100 };
    });
    this.topFails = s.topFails.map((t) => ({ part: t.part, fails: t.fails, total: t.total, rate: (t.fails / t.total) * 100 }));
    this.filtered = [];
    this.paged = [];
  }

  private computeStats(): void {
    if (this.state?.tableLoading && this.summary) {
      this.showSummary(this.summary);
      return;
    }
    let pass = 0;
    let fail = 0;
    let other = 0;
    const byDay = new Map<string, { pass: number; fail: number }>();
    const byPart = new Map<string, { fails: number; total: number }>();

    for (const r of this.filtered) {
      if (r.result === 'PASS') pass++;
      else if (r.result === 'FAIL') fail++;
      else other++;

      if (r.dateKey) {
        const d = byDay.get(r.dateKey) ?? { pass: 0, fail: 0 };
        if (r.result === 'PASS') d.pass++;
        else if (r.result === 'FAIL') d.fail++;
        byDay.set(r.dateKey, d);
      }
      if (r.part) {
        const p = byPart.get(r.part) ?? { fails: 0, total: 0 };
        p.total++;
        if (r.result === 'FAIL') p.fails++;
        byPart.set(r.part, p);
      }
    }

    const judged = pass + fail;
    this.kpi = {
      total: this.filtered.length,
      pass,
      fail,
      other,
      rate: judged ? Math.round((pass / judged) * 1000) / 10 : 0,
    };

    // last 14 days that have data, drawn as stacked bars (fail on top of pass)
    const keys = Array.from(byDay.keys()).sort().slice(-14);
    const max = Math.max(1, ...keys.map((k) => byDay.get(k)!.pass + byDay.get(k)!.fail));
    this.trendMax = max;
    this.trend = keys.map((k) => {
      const v = byDay.get(k)!;
      const [, m, d] = k.split('-');
      return {
        key: k,
        label: `${d}/${m}`,
        pass: v.pass,
        fail: v.fail,
        total: v.pass + v.fail,
        h: ((v.pass + v.fail) / max) * 100,
      };
    });

    this.topFails = Array.from(byPart.entries())
      .filter(([, v]) => v.fails > 0)
      .map(([part, v]) => ({ part, fails: v.fails, total: v.total, rate: (v.fails / v.total) * 100 }))
      .sort((a, b) => b.fails - a.fails || b.rate - a.rate)
      .slice(0, 5);
  }

  private clampPage(): void {
    this.page = Math.min(Math.max(1, this.page), this.totalPages);
  }

  private slice(): void {
    const s = (this.page - 1) * this.pageSize;
    this.paged = this.filtered.slice(s, s + this.pageSize);
  }

  private stopTimer(): void {
    if (this.timer) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }

  private ymd(d: Date): string {
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${d.getFullYear()}-${mm}-${dd}`;
  }
}
