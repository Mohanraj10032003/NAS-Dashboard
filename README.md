# NAS Dashboard

| Folder | What it is |
| --- | --- |
| `ShipmentTracker.Api/` | **.NET 8 backend.** Logs in to the NAS server with a username and password, reads the Excel files and serves them as JSON. The built website is already inside its `wwwroot`, so this one folder is enough to run everything. |
| `shipment-tracker/` | Angular 16 frontend source (only needed if you want to change the UI). |

## Quick start (Windows, .NET 8 SDK installed)

1. Open `ShipmentTracker.Api\appsettings.json` and set the NAS details:
   ```json
   "Nas": { "Path": "\\\\NAS-SERVER\\Shipments", "Username": "DOMAIN\\your-user", "Password": "" }
   ```
2. Give the NAS password through an environment variable (never inside the file). In PowerShell:
   ```powershell
   $env:Nas__Password = "your-nas-password"
   ```
3. Run it in the same window:
   ```powershell
   cd ShipmentTracker.Api
   dotnet run
   ```
4. Open **http://localhost:5080** and sign in with ``.

Details (users, JWT secret, IIS / Windows service, HTTPS, troubleshooting): `ShipmentTracker.Api/README.md`.

## Changing the UI

```bash
cd shipment-tracker
npm install
npm run build
# copy everything inside dist/shipment-tracker into ShipmentTracker.Api/wwwroot
```

## Corrupt / unreadable Excel files
Damaged, half-saved, empty or lock (`~$...`) files are **skipped silently** - no exception, no error in the log.
The UI shows a soft note ("N file(s) were left out ..."). A skipped file is remembered (by size + modified time) so it is not re-opened on every call; once it is fixed/re-saved it is picked up again.

## Large data protection (All-data load is OFF)
In `appsettings.json` -> `Nas`:
- `AllowAllData` (false)  - when false, From+To dates are required.
- `MaxRangeDays` (31)     - longest date range per load.
- `MaxTableRows` (50000)  - table shows first N rows; counts/charts still cover everything.

To re-enable "All time": set `Nas:AllowAllData` = true, then in the frontend `dashboard.component.ts` / `.html` un-comment the blocks marked `All time` (the `'all'` branch in `quickRange` and the button).
