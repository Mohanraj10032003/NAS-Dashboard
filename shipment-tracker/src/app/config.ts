// ---------------------------------------------------------------------------
//  App configuration
// ---------------------------------------------------------------------------
export interface AppUser {
  username: string;
  password: string;
  name: string;
  role: string;
}

/**
 * Where the .NET API lives.
 *  - `ng serve` (port 4200): the API is expected on the same machine, port 5080.
 *  - Built app copied into the API's wwwroot folder: same address as the page (empty string = same origin).
 * Need something else (API on another server)? Put the full address in API_URL_OVERRIDE, e.g. 'http://192.168.1.20:5080'.
 */
const API_URL_OVERRIDE = '';
const apiBaseUrl =
  API_URL_OVERRIDE ||
  (typeof location !== 'undefined' && location.port === '4200' ? `${location.protocol}//${location.hostname}:5080` : '');

export const APP_CONFIG = {
  appName: 'NAS Dashboard',
  // Shown in the footer as "© <current year> <companyName>. All rights reserved."
  companyName: 'e-con Systems',
  // Set to false to hide the "Demo login" hint on the login page
  showLoginHint: false,

  // true  = login + Excel data come from the .NET API (the API reads the NAS server).
  // false = old stand-alone mode: users below + browser folder picker, no backend needed.
  useApi: true,
  apiBaseUrl,

  // ---- stand-alone mode only (useApi: false). With the API, users are defined in the API's appsettings.json ----
  users: <AppUser[]>[
    // { username: 'e-consystems', password: 'NAS@2003', name: 'e-con Systems', role: 'Admin' },
  ],

  // How often the dashboard asks the API for fresh data when "Auto refresh" is on (seconds)
  autoRefreshSeconds: 30,
};
