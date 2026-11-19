# NAS Dashboard – Angular 16 frontend

Dashboard for the Excel shipment / test reports that live on your NAS server: pass rate, daily results,
filters (part number, date, result), sortable table, CSV export, day and night themes, mobile friendly.

It works in two ways (`useApi` in `src/app/config.ts`):

| Mode | How the data arrives | Needs |
| --- | --- | --- |
| **API mode** (default) | The **ShipmentTracker.Api** (.NET 8) logs in to the NAS server, reads the Excel files and sends JSON | The API running (see its README) |
| Stand-alone (`useApi: false`) | The browser reads a folder you pick (Chrome / Edge) | Nothing else |

## Run (development)

```bash
npm install
npm start          # http://localhost:4200  (talks to the API on http://<same host>:5080)
```

Start the API first (`dotnet run` in the `ShipmentTracker.Api` folder). Users are defined in the API's `appsettings.json`
(default `econsystems / NAS@2026`, `user / user123`).

API on another address? Set `API_URL_OVERRIDE` at the top of `src/app/config.ts`, and add the frontend address to
`Cors:AllowedOrigins` in the API's `appsettings.json`.

## Build for production

```bash
npm run build      # output: dist/shipment-tracker
```

Copy everything inside `dist/shipment-tracker` into the API's `wwwroot` folder. The API then serves the website and the
data from one address (e.g. `http://server:5080`), so no CORS or URL settings are needed.

## Other settings (`src/app/config.ts`)

- `companyName` – footer text: `© <current year> e-con Systems. All rights reserved.` (the year follows the calendar)
- `showLoginHint` – show / hide the "Demo login" line on the sign-in page
- `autoRefreshSeconds` – how often the dashboard asks for fresh data when *Auto refresh* is on

## Excel columns the API understands

Names are matched flexibly (case, spaces and symbols ignored). Other columns are shown as extra table columns.

| Purpose | Accepted header examples |
| --- | --- |
| Part number | Part Number, PartNo, Part, PN, Material, Item |
| Date | Date, Ship Date, DateTime, Timestamp |
| Pass / Fail | Pass, Result, Status, Pass/Fail, QC Status |

The header row can be anywhere in the first 15 rows (title rows above it are ignored).
