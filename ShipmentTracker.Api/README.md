# ShipmentTracker.Api – .NET 8 backend

Reads the Excel reports (`.xlsx`, `.xlsm`, `.csv`) from a **NAS server**, logging in to the share with a username and
password, and serves the rows to the NAS Dashboard Angular dashboard as JSON.

```
Browser (Angular)  --login/JWT-->  ShipmentTracker.Api  --SMB login-->  \\NAS-SERVER\Shipments
                   <---- JSON ----   (cache in memory)   <-- Excel --
```

* No NuGet packages: only the shared .NET 8 / ASP.NET Core framework. `dotnet run` works on a PC with just the .NET 8 SDK.
* Only the reports a user asks for are read, and only **new or changed files** are re-read, so the
  dashboard is always answered from memory. If the NAS goes down, the last good data is still served with a warning.
* The built Angular app is already inside `wwwroot`, so one address serves both the website and the API.

## 1. Run it

Install the **.NET 8 SDK** (https://dotnet.microsoft.com/download/dotnet/8.0), then:

```bash
cd ShipmentTracker.Api
dotnet run
```

Open **http://localhost:5080** and sign in with `econsystems / NAS@2026` (change it, see section 4).

## Applications, and how data is loaded (fast)

`Nas:Path` is the **root folder** that holds all application folders, e.g.
`\\mfgshare.e-consystems.net\Production\...\Temp e-con Systems Daily Test Report - Server`.
Each entry of `Nas:Applications` is one item of the dashboard's **Application** drop-down; its `Folder` is relative to the root
(a full `\\server\share\...` path also works). Restart the API after adding an application.

```json
"Nas": {
  "Path": "\\\\mfgshare...\\Temp e-con Systems Daily Test Report - Server",
  "Applications": [
    { "Id": "diagcam", "Name": "DiagCAM",  "Folder": "DiagCAM\\DiagCAM" },
    { "Id": "blemish", "Name": "Blemish",  "Folder": "DiagCAM\\Blemish" }
  ]
}
```

**Nothing is read until the user asks.**

1. Choosing an application lists its folders only (**catalog**: part numbers, first / last date). No Excel file is opened, and the
   list is reused for `Nas:CatalogCacheSeconds`.
2. The user picks a date range (and optionally a part number) and presses **Load data**. The API reads only the reports whose
   folder names match. Folders are expected like `year / month / PART-NUMBER / yyyy-MM-dd / file.xlsx`; the part number and date
   are taken from the path. Files without a date in their path are matched by the day they were saved.
3. Files that did not change since the last read come from memory. **Refresh** runs the same question again.
4. One load reads at most `Nas:MaxFilesPerLoad` files (default 800); above that the user is asked to narrow the selection.

API: `GET /api/apps`, `GET /api/catalog?app=<id>`, `GET /api/shipments?app=<id>&part=<text>&from=yyyy-MM-dd&to=yyyy-MM-dd`.
On a domain PC that can already open the folder in Explorer, leave `Username` / `Password` empty.

### Settings page: change NAS paths without touching the code

- Sign in as an Admin and press the gear icon (top bar), or "Manage paths" at the bottom of the Application drop-down.
- You see the root path and every application with its folder. Edit a name or folder, add a new application, move it up or down, remove it, then **Save changes**.
- **Test** (after saving) opens that folder on the NAS and tells how many report files it found, how many have the date and the part number
  in their folder / file name, the first and last day, and the newest files. Use it when a new folder does not behave as expected.
- Changes are saved in `data/settings.json` next to the API and win over `appsettings.json`. "Restore appsettings.json" deletes that file.
- The NAS user name and password are NOT stored there; keep them in `appsettings.json` or the `Nas__Password` environment variable.
- Login of this version: user `e-consystems`, password `NAS@2003` (hash in `Auth:Users`). Make a new hash with
  `dotnet ShipmentTracker.Api.dll hash-password "<password>"`.

### Speed: disk cache + background reader (no database)

- Every report read from the NAS is saved as a small compressed file in `cache\<application>\` next to the API. A report of a
  past day never changes, so it is read from the NAS **once**; afterwards it comes from this disk in milliseconds, also after
  an API restart.
- A background reader looks every `Nas:WarmIntervalMinutes` (5) minutes for new or changed reports of the last `Nas:WarmDays` (14)
  days and reads only those. So when someone presses "Load data" the files are normally already read and the Total / Pass / Fail
  numbers appear immediately. Set `WarmDays` to 0 to switch it off.
- If the NAS cannot be reached (for example you are not in the office) the dashboard still shows everything that was read
  before, with a note "NAS not reachable right now, showing the saved copy". Reports never read before cannot be shown then.
- Delete the `cache` folder any time to start fresh (it is rebuilt automatically). Change the place with `Nas:CacheDirectory`.
- Run the API on a PC that is wired to the same network as the NAS (not on Wi-Fi) for the fastest first read.

### Numbers first, table after (two-step "Load data")

1. `GET /api/summary?app&part&from&to` - reads the matching reports and sends only the numbers (total, pass, fail, other,
   per-day, top failing parts). It is a few KB, so the dashboard cards and charts show up first.
2. `GET /api/shipments?...` - then the table rows are fetched (the files are already read and cached by step 1).
   While this runs the page shows "Loading the table...".


## 2. Point it at the NAS (`appsettings.json` > `Nas`)

```json
"Nas": {
  "Path": "\\\\NAS-SERVER\\Shipments",
  "Username": "DOMAIN\\svc-shipments",
  "Password": "",
  "Recursive": true,
  "Extensions": [ ".xlsx", ".xlsm", ".csv" ]
}
```

* **Path** – use the UNC form `\\server\share\folder`. In JSON every backslash is doubled (`\\\\server\\share`).
  A mapped drive letter such as `Z:\` does **not** work: drive letters belong to one user's desktop session, not to this service.
* **Username** – `DOMAIN\user`, `user@domain`, or just `user` together with `Domain`. The account only needs **read** access.
* **Password** – do **not** type it into `appsettings.json` (it would end up in backups and source control).
  Use one of these instead:

  **Environment variable** (works everywhere, also for services):
  ```powershell
  setx /M Nas__Username "DOMAIN\svc-shipments"
  setx /M Nas__Password "the-password"
  ```
  (open a new terminal / restart the service afterwards)

  **User-secrets** (development PC only):
  ```bash
  dotnet user-secrets init
  dotnet user-secrets set "Nas:Password" "the-password"
  ```

How the login works: when the folder is not readable, the API connects to `\\server\share` with that account
(the same as `net use \\server\share /user:...`, Windows API `WNetAddConnection2`) and then reads the files normally.
If the folder is already readable (for example the service account itself has access), no username / password is needed at all.

> **Best practice:** run the API under a Windows account that already has read permission on the share
> (IIS app-pool identity, or the service "Log on as" account). Then leave `Username` / `Password` empty.

On Linux / macOS mount the share yourself (`mount -t cifs //server/share /mnt/nas -o username=...`) and set `Nas:Path` to `/mnt/nas`.

## 3. Who can open the dashboard (`Auth`)

* Users live in `appsettings.json` > `Auth:Users`. Passwords are stored as PBKDF2 hashes.
* Create a hash for a new password:
  ```bash
  dotnet run -- hash-password "MyNewPassword"
  ```
  and paste the output into `PasswordHash`. **Change the default `econsystems` password before going live.**
* Set `Auth:JwtSecret` to a long random text (32+ characters). If it is empty a random key is created on every start,
  and everybody has to sign in again after a restart.
* 5 wrong passwords for a user = 5 minute pause for that user name.

## 4. Update the website

The Angular source is in the `shipment-tracker` project. After changes:

```bash
npm run build
# copy everything inside dist/shipment-tracker into ShipmentTracker.Api/wwwroot
```

Developing the UI with `ng serve` (http://localhost:4200) also works: the API allows that origin (`Cors:AllowedOrigins`).
If you open the dev site from another PC, add its address (e.g. `http://192.168.1.50:4200`) to that list.

## 5. Put it on a server

```bash
dotnet publish -c Release -o publish
```

Run `publish\ShipmentTracker.Api.exe` (needs the ASP.NET Core 8 runtime on the server), or choose:

* **IIS** – install the *.NET 8 Hosting Bundle*, create a site pointing to the `publish` folder. Set the application
  pool identity to an account that can read the NAS share. (Kestrel `Urls` is ignored under IIS; IIS bindings are used.)
* **Windows service** – run `dotnet add package Microsoft.Extensions.Hosting.WindowsServices`, add
  `builder.Host.UseWindowsService();` after `WebApplication.CreateBuilder(args)` in `Program.cs`, publish, then
  `sc create ShipmentTracker binPath= "C:\apps\shipment\ShipmentTracker.Api.exe" start= auto`.
  Set the service to log on as the NAS account, or use the `Nas__...` machine environment variables.
* **NSSM / Task Scheduler** – wrap `ShipmentTracker.Api.exe` the same way.

Open the firewall for the port (default 5080 – change `Urls` in `appsettings.json`):

```powershell
netsh advfirewall firewall add rule name="NAS Dashboard" dir=in action=allow protocol=TCP localport=5080
```

**Use HTTPS if people sign in over the network** (plain http sends the password readable on the wire):
terminate TLS in IIS or a reverse proxy, or give Kestrel a certificate (`Kestrel:Endpoints:Https` in appsettings).

## 6. API

| Method | URL | Notes |
| --- | --- | --- |
| POST | `/api/auth/login` | `{ "username", "password" }` -> `{ token, expiresAtUtc, user }` |
| GET | `/api/auth/me` | current user (needs `Authorization: Bearer <token>`) |
| GET | `/api/shipments` | all rows. `?refresh=true` scans the NAS first. Sends an `ETag`, answers `304` when nothing changed |
| GET | `/api/shipments/status` | connected?, folder, last scan, last error, file / row counts |
| POST | `/api/shipments/refresh` | scan the NAS now |
| GET | `/api/health` | open (no login): is the API up, can it see the NAS |

Row shape: `{ id, part, date: "2026-10-23T14:05:09", result: "PASS|FAIL|OTHER", resultRaw, source: "folder/file.xlsx › Sheet1", extra: { ...other columns } }`

## 7. What is read from the Excel files

* Every sheet of every `.xlsx / .xlsm / .csv` under `Nas:Path` (sub-folders too, up to `MaxDepth`). Excel's `~$...` lock files are ignored.
* The header row may be anywhere in the first 15 rows. Columns are found by name (case, spaces, symbols ignored):
  **Part** (Part Number, PartNo, PN, Material, Item...), **Date** (Date, Ship Date, Timestamp...), **Result** (Pass/Fail, Result, Status, QC Status...).
  A sheet with neither a Part nor a Result column is skipped.
* Result values: `pass, passed, ok, p, true, 1, yes, good, accepted` = PASS; `fail, failed, ng, f, false, 0, no, bad, reject, rejected` = FAIL; anything else = OTHER.
* Dates: real Excel dates (1900 and 1904 calendars) and text dates (`2026-10-23`, `23-10-2026 09:15:30`) are read as local time.
* Other columns are passed on as text, formatted the way Excel shows them (`1,500`, `25.6%`, `10:18:01`).
* Files that are open in Excel can still be read. Old binary **`.xls` files are not supported** – save them as `.xlsx`.

## 8. Troubleshooting

Open `/api/health` or the dashboard banner: the message says what failed.

| Message | Meaning / fix |
| --- | --- |
| `... wrong username or password` (Win32 86 / 1326) | Check `Nas:Username` (try `DOMAIN\user`) and the password variable |
| `... server or share name not found` (53 / 67) | `Nas:Path` is wrong, or DNS / VPN problem – try `ping NAS-SERVER` and the IP address instead of the name |
| `... access denied` (5) | The account has no read permission on that share / folder |
| `... already uses other credentials for that server` (1219) | Windows already has a connection to that server with another account: `net use * /delete` and restart the API |
| `Timed out after 20s ...` | Server off or blocked by a firewall |
| `... could not be read: Old .xls format` | Save the file as `.xlsx` |
| Dashboard says "Cannot reach the API" | The API is not running, or the URL / port / CORS setting is wrong |

Logs are written to the console (Windows service: Event Viewer, or redirect with NSSM).
