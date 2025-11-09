# App_Garmin

Small ASP.NET 9.0 Web API + Razor UI for reading Garmin activity data via the [garminconnect](https://github.com/cyberjunky/python-garminconnect) Python library.

This repository contains:
- `GarminTempApi/` — ASP.NET Web API + Razor Pages project (targets .NET 9.0)
- `web/Dockerfile` — multi-stage Dockerfile for building and running the API
- `docker-compose.yml` — spins up the API container (no extra services required)

Quick goals for this first GitHub version:
- Build and verify the solution builds on CI
- Provide Dockerfile to run locally
- Provide minimal documentation and license

## Prerequisites
- .NET SDK 9.0 (or matching runtime for development)
- Python 3.10+ with `pip` (used for the garminconnect helper script)
- Docker (optional, recommended for the self-contained container image)
- Git

## Quickstart — build and run locally
From repository root (PowerShell):

```powershell
# 1. Configure credentials (required for real Garmin imports)
$env:GARMIN_USERNAME = "<your-garmin-username>"
$env:GARMIN_PASSWORD = "<your-garmin-password>"
# Optional: limit results or control the initial date range
$env:GARMIN_MAX_ACTIVITIES = "50"
$env:GARMIN_START_DATE = "2025-01-01"
$env:GARMIN_END_DATE = "2025-11-09"

# 2. Build
dotnet build "C:\Users\camil\Desktop\App_Garmin\App_Garmin.sln" -c Debug

# 3. Run API + Razor UI locally
cd "C:\Users\camil\Desktop\App_Garmin\GarminTempApi"
dotnet run -c Debug

# 4. Trigger a Garmin import (first run will create the DB)
Invoke-RestMethod -Method Post http://localhost:5180/api/admin/trigger-import

# 5. Inspect activities
Invoke-RestMethod http://localhost:5180/api/garmindb/activities?limit=10 | Format-Table
```
> The host/port may differ; check the `dotnet run` logs for `Now listening on:` and adjust the URLs accordingly.

- Razor UI is available at `http://localhost:5180/` (Activities/Sleep/Steps/Insights pages).
- The importer stores activities in a local SQLite database (`garmin_app.db`) inside the directory pointed to `APP_DATA_DIR` (defaults to `./data` when running without Docker).
- When running locally ensure the Python package `garminconnect` is installed (`pip install garminconnect`). The app invokes `scripts/garmin_fetch.py` to call the Garmin Connect API and streams the resulting JSON into the .NET sync service.

## Run with Docker

```powershell
# 1. (Optional) add credentials to a .env file alongside docker-compose.yml
# GARMIN_USERNAME=<your-user>
# GARMIN_PASSWORD=<your-password>

# 2. Build and start the container (includes Python + garminconnect)
cd "C:\Users\camil\Desktop\App_Garmin"
docker-compose up -d --build

# 3. Trigger an import inside the container
Invoke-RestMethod -Method Post http://localhost:5000/api/admin/trigger-import

# 4. Follow logs if needed
docker-compose logs -f web

# 5. Browse the UI
Start-Process http://localhost:5000/
```

- The container stores SQLite data under `/app/data` (mounted to the `garmin-data` volume).
- Configure fetch behaviour with `GARMIN_START_DATE`, `GARMIN_END_DATE`, `GARMIN_MAX_ACTIVITIES`, and `GARMIN_REFRESH_MINUTES` (minutes between automatic sync runs).

## CI
A GitHub Actions workflow is included at `.github/workflows/dotnet.yml` that restores and builds the solution on push and pull requests.

## License
This repository is released under the MIT license — see `LICENSE` file.

