import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { APP_CONFIG } from '../config';
import { ShipmentDataService } from './shipment-data.service';

export interface SettingsApp {
  id?: string;
  name: string;
  folder: string;
  fullPath?: string;
}

export interface SettingsModel {
  root: string;
  customised: boolean;
  applications: SettingsApp[];
}

export interface CheckResult {
  appId: string;
  path: string;
  reachable: boolean;
  error: string | null;
  files: number;
  withDate: number;
  withPart: number;
  parts: number;
  minDate: string | null;
  maxDate: string | null;
  sample: string[];
  hints: string[];
  ms: number;
}

/** Reads and saves the NAS root path and the folder of every application (Settings page). */
@Injectable({ providedIn: 'root' })
export class SettingsService {
  /** preview only (no API): the settings live in memory */
  private demo: SettingsModel = {
    root: String.raw`\\mfgshare.e-consystems.net\Production\Over All Test Report\7 Temporary Testing Report\Temp e-con Systems Daily Test Report - Server`,
    customised: false,
    applications: [
      { id: 'diagcam', name: 'DiagCAM', folder: String.raw`DiagCAM\DiagCAM` },
      { id: 'calibration', name: 'Calibration', folder: 'Calibration' },
      { id: 'blemish', name: 'Blemish', folder: String.raw`DiagCAM\Blemish` },
      { id: 'firmware', name: 'Parallel Firmware Flasher', folder: String.raw`DiagCAM\ParallelFirmwareFlasher` },
      { id: 'ezee-focus', name: 'e-zee Focus', folder: 'e-zee Focus' },
      { id: 'active-alignment', name: 'Active Alignment', folder: 'Active Alignment' },
      { id: 'carrier-board', name: 'Carrier Board', folder: 'Carrier Board' },
      { id: 'leak-tester', name: 'Leak_Tester', folder: 'Leak_Tester' },
      { id: 'new-foco', name: 'New_Foco', folder: 'New_Foco' },
    ],
  };

  constructor(private http: HttpClient, private data: ShipmentDataService) {}

  /** Settings need the API (or the preview). The old stand-alone mode has no server to keep them. */
  get available(): boolean {
    return this.data.queryMode;
  }

  private get api(): string {
    return `${APP_CONFIG.apiBaseUrl}/api/settings`;
  }

  async get(): Promise<SettingsModel> {
    if (!APP_CONFIG.useApi) return this.withPaths(this.demo);
    return firstValueFrom(this.http.get<SettingsModel>(this.api));
  }

  async save(root: string, apps: SettingsApp[]): Promise<SettingsModel> {
    let model: SettingsModel;
    if (!APP_CONFIG.useApi) {
      const used = new Set<string>();
      this.demo = {
        root: root.trim(),
        customised: true,
        applications: apps.map((a, i) => {
          let id = a.id || a.name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-|-$/g, '') || `app${i + 1}`;
          const base = id;
          for (let n = 2; used.has(id); n++) id = `${base}-${n}`;
          used.add(id);
          return { id, name: a.name.trim(), folder: a.folder.trim() };
        }),
      };
      model = this.withPaths(this.demo);
    } else {
      model = await firstValueFrom(
        this.http.put<SettingsModel>(this.api, {
          root,
          applications: apps.map((a) => ({ id: a.id ?? '', name: a.name, folder: a.folder })),
        })
      );
    }
    await this.data.reloadApps(model.applications.map((a) => ({ id: a.id!, name: a.name })));
    return model;
  }

  /** Back to what appsettings.json says. */
  async reset(): Promise<SettingsModel> {
    if (!APP_CONFIG.useApi) {
      this.demo = { ...this.demo, customised: false };
      return this.withPaths(this.demo);
    }
    const model = await firstValueFrom(this.http.delete<SettingsModel>(this.api));
    await this.data.reloadApps(model.applications.map((a) => ({ id: a.id!, name: a.name })));
    return model;
  }

  /** What can the API see in the (saved) folder of this application? */
  async check(appId: string): Promise<CheckResult> {
    if (!APP_CONFIG.useApi) {
      await new Promise((r) => setTimeout(r, 700));
      return {
        appId,
        path: this.withPaths(this.demo).applications.find((a) => a.id === appId)?.fullPath ?? '',
        reachable: true,
        error: null,
        files: 1917,
        withDate: 1917,
        withPart: 1840,
        parts: 24,
        minDate: '2026-08-25',
        maxDate: '2026-11-12',
        sample: [
          'DiagCAM/2026/november/ECAM-AR0234-MIPI/2026-11-12/CameraDiagnosticLog_PassReport (0).xlsx',
          'DiagCAM/2026/november/ECAM-AR0521-USB/2026-11-12/CameraDiagnosticLog_PassReport (1).xlsx',
        ],
        hints: ['Preview only: these numbers are sample data.'],
        ms: 640,
      };
    }
    return firstValueFrom(this.http.post<CheckResult>(`${this.api}/check?app=${encodeURIComponent(appId)}`, {}));
  }

  errorOf(e: unknown): string {
    const err = e as HttpErrorResponse;
    if (err?.status === 0) return 'Cannot reach the NAS Dashboard server. Check that the API is running.';
    if (err?.status === 401) return 'Your session has ended. Sign in again.';
    if (err?.status === 403) return 'Only an Admin user can change the settings.';
    return err?.error?.message ?? (e as Error)?.message ?? 'Something went wrong.';
  }

  private withPaths(m: SettingsModel): SettingsModel {
    return { ...m, applications: m.applications.map((a) => ({ ...a, fullPath: resolveFolder(m.root, a.folder) })) };
  }
}

/** Same rule as the API: a full path is used as it is, a relative folder is added to the root. */
export function resolveFolder(root: string, folder: string): string {
  const f = (folder ?? '').trim();
  const r = (root ?? '').trim();
  if (!f) return r;
  if (f.startsWith('\\\\') || f.startsWith('//') || f.includes(':') || f.startsWith('/')) return f;
  return `${r.replace(/[\\/]+$/, '')}\\${f.replace(/^[\\/]+/, '')}`;
}
