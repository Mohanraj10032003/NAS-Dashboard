import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { APP_CONFIG } from '../config';
import { clearSession, getToken, getUser, saveSession, SessionUser } from './session-store';

export type { SessionUser } from './session-store';

export type LoginResult = { ok: true } | { ok: false; message: string };

interface LoginResponse {
  token: string;
  expiresAtUtc: string;
  user: SessionUser;
}

@Injectable({ providedIn: 'root' })
export class AuthService {
  constructor(private http: HttpClient) {}

  get user(): SessionUser | null {
    return getUser();
  }

  get token(): string | null {
    return getToken();
  }

  get isLoggedIn(): boolean {
    return this.user !== null && (!APP_CONFIG.useApi || !!this.token);
  }

  async login(username: string, password: string, remember: boolean): Promise<LoginResult> {
    // ---- stand-alone mode: check against APP_CONFIG.users ----
    if (!APP_CONFIG.useApi) {
      const found = APP_CONFIG.users.find(
        (u) => u.username.toLowerCase() === username.toLowerCase() && u.password === password
      );
      if (!found) return { ok: false, message: 'Invalid username or password.' };
      saveSession({ username: found.username, name: found.name, role: found.role }, null, remember);
      return { ok: true };
    }

    // ---- API mode ----
    try {
      const res = await firstValueFrom(
        this.http.post<LoginResponse>(`${APP_CONFIG.apiBaseUrl}/api/auth/login`, { username, password })
      );
      saveSession(res.user, res.token, remember);
      return { ok: true };
    } catch (e) {
      const err = e as HttpErrorResponse;
      if (err.status === 0) {
        return {
          ok: false,
          message: `Cannot reach the NAS Dashboard server at ${APP_CONFIG.apiBaseUrl || location.origin}. Check that the API is running.`,
        };
      }
      return { ok: false, message: err.error?.message ?? `Sign in failed (error ${err.status}).` };
    }
  }

  logout(): void {
    clearSession();
  }
}
