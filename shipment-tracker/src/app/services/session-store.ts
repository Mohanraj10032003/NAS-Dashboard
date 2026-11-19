// Tiny helper that keeps the signed-in user and API token in the browser (session or local storage).
// Used by AuthService and by the HTTP interceptor (kept separate to avoid a circular dependency).
export interface SessionUser {
  username: string;
  name: string;
  role: string;
}

const USER_KEY = 'st-session';
const TOKEN_KEY = 'st-token';

function read(key: string): string | null {
  try {
    return sessionStorage.getItem(key) ?? localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function getUser(): SessionUser | null {
  try {
    const raw = read(USER_KEY);
    return raw ? (JSON.parse(raw) as SessionUser) : null;
  } catch {
    return null;
  }
}

export function getToken(): string | null {
  return read(TOKEN_KEY);
}

export function saveSession(user: SessionUser, token: string | null, remember: boolean): void {
  clearSession();
  try {
    const store = remember ? localStorage : sessionStorage;
    store.setItem(USER_KEY, JSON.stringify(user));
    if (token) store.setItem(TOKEN_KEY, token);
  } catch {
    /* storage blocked – ignore */
  }
}

export function clearSession(): void {
  try {
    for (const s of [sessionStorage, localStorage]) {
      s.removeItem(USER_KEY);
      s.removeItem(TOKEN_KEY);
    }
  } catch {
    /* ignore */
  }
}
