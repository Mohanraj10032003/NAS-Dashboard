import { Component, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { APP_CONFIG } from '../../config';
import { AuthService } from '../../services/auth.service';
import { CheckResult, resolveFolder, SettingsApp, SettingsModel, SettingsService } from '../../services/settings.service';

interface Row {
  key: number;
  id?: string;
  name: string;
  folder: string;
  check: CheckResult | null;
  checking: boolean;
  checkError: string;
}

@Component({
  selector: 'app-settings',
  templateUrl: './settings.component.html',
  styleUrls: ['./settings.component.scss'],
})
export class SettingsComponent implements OnInit {
  appName = APP_CONFIG.appName;
  userName = '';
  userRole = '';

  loading = true;
  loadError = '';
  saving = false;
  saveError = '';
  savedAt: Date | null = null;
  confirmReset = false;
  confirmRemove: number | null = null;

  root = '';
  rows: Row[] = [];
  customised = false;
  readonly canEdit: boolean;

  private baseline = '';
  private saved = new Map<string, { name: string; folder: string }>(); // id -> what is saved on the server
  private seq = 0;

  constructor(private settings: SettingsService, private auth: AuthService, private router: Router) {
    this.canEdit = (this.auth.user?.role ?? '') === 'Admin' || !APP_CONFIG.useApi;
  }

  ngOnInit(): void {
    const u = this.auth.user;
    this.userName = u?.name ?? '';
    this.userRole = u?.role ?? '';
    void this.load();
  }

  // ------------------------------------------------------------------ load / save

  async load(): Promise<void> {
    this.loading = true;
    this.loadError = '';
    try {
      this.setModel(await this.settings.get());
    } catch (e) {
      this.loadError = this.settings.errorOf(e);
    } finally {
      this.loading = false;
    }
  }

  private setModel(m: SettingsModel): void {
    this.root = m.root;
    this.customised = m.customised;
    this.rows = m.applications.map((a) => this.toRow(a));
    this.saved = new Map(m.applications.filter((a) => a.id).map((a) => [a.id!, { name: a.name, folder: a.folder }]));
    this.baseline = this.snapshot();
  }

  private toRow(a: SettingsApp): Row {
    return { key: ++this.seq, id: a.id, name: a.name, folder: a.folder, check: null, checking: false, checkError: '' };
  }

  private snapshot(): string {
    return JSON.stringify([this.root.trim(), this.rows.map((r) => [r.id ?? '', r.name.trim(), r.folder.trim()])]);
  }

  get dirty(): boolean {
    return this.snapshot() !== this.baseline;
  }

  get valid(): boolean {
    return this.rows.length > 0 && this.rows.every((_, i) => !this.rowError(i)) && !this.rootError;
  }

  get rootError(): string {
    const needsRoot = this.rows.some((r) => r.folder.trim() && !this.isFull(r.folder));
    return needsRoot && !this.root.trim() ? 'Enter the root path (the rows below are relative to it).' : '';
  }

  rowError(i: number): string {
    const r = this.rows[i];
    const name = r.name.trim();
    if (!name) return 'Enter a name.';
    if (this.rows.some((o, j) => j !== i && o.name.trim().toLowerCase() === name.toLowerCase())) return 'Another application has this name.';
    if (!r.folder.trim()) return 'Enter the folder.';
    if (/[<>"|?*]/.test(r.folder)) return 'The folder has characters that a folder name cannot have.';
    return '';
  }

  fullPath(r: Row): string {
    return resolveFolder(this.root, r.folder);
  }

  private isFull(folder: string): boolean {
    const f = folder.trim();
    return f.startsWith('\\\\') || f.startsWith('//') || f.includes(':') || f.startsWith('/');
  }

  async save(): Promise<void> {
    if (!this.dirty || !this.valid || this.saving) return;
    this.saving = true;
    this.saveError = '';
    try {
      const apps: SettingsApp[] = this.rows.map((r) => ({ id: r.id, name: r.name, folder: r.folder }));
      this.setModel(await this.settings.save(this.root, apps));
      this.savedAt = new Date();
    } catch (e) {
      this.saveError = this.settings.errorOf(e);
    } finally {
      this.saving = false;
    }
  }

  discard(): void {
    void this.load();
    this.saveError = '';
  }

  async resetToDefaults(): Promise<void> {
    this.confirmReset = false;
    this.saving = true;
    this.saveError = '';
    try {
      this.setModel(await this.settings.reset());
      this.savedAt = new Date();
    } catch (e) {
      this.saveError = this.settings.errorOf(e);
    } finally {
      this.saving = false;
    }
  }

  // ------------------------------------------------------------------ rows

  add(): void {
    this.rows.push({ key: ++this.seq, name: '', folder: '', check: null, checking: false, checkError: '' });
    setTimeout(() => document.getElementById('name-' + this.rows[this.rows.length - 1].key)?.focus());
  }

  remove(i: number): void {
    if (this.confirmRemove !== i) {
      this.confirmRemove = i;
      return;
    }
    this.rows.splice(i, 1);
    this.confirmRemove = null;
  }

  move(i: number, by: number): void {
    const j = i + by;
    if (j < 0 || j >= this.rows.length) return;
    [this.rows[i], this.rows[j]] = [this.rows[j], this.rows[i]];
    this.confirmRemove = null;
  }

  /** A row can be tested once its folder is saved (the API tests what is saved). */
  canTest(r: Row): boolean {
    if (!r.id) return false;
    const s = this.saved.get(r.id);
    return !!s && s.folder === r.folder.trim() && this.root.trim() === this.savedRoot;
  }

  private get savedRoot(): string {
    return JSON.parse(this.baseline)[0];
  }

  async test(r: Row): Promise<void> {
    if (!r.id || r.checking) return;
    r.checking = true;
    r.checkError = '';
    r.check = null;
    try {
      r.check = await this.settings.check(r.id);
    } catch (e) {
      r.checkError = this.settings.errorOf(e);
    } finally {
      r.checking = false;
    }
  }

  pct(part: number, total: number): string {
    return total ? Math.round((part / total) * 100) + '%' : '0%';
  }

  trackRow(_: number, r: Row): number {
    return r.key;
  }

  back(): void {
    this.router.navigate(['/dashboard']);
  }

  logout(): void {
    this.auth.logout();
    this.router.navigate(['/login']);
  }
}
