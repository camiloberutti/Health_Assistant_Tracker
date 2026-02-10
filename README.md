# App_Garmin

Small ASP.NET 9.0 Web API + Razor UI for reading Garmin activity data via the [garminconnect](https://github.com/cyberjunky/python-garminconnect) Python library.

This repository contains:
- `GarminTempApi/` — ASP.NET Web API + Razor Pages project (targets .NET 9.0)
- `web/Dockerfile` — multi-stage Dockerfile for building and running the API
- `docker-compose.yml` — spins up the API container (no extra services required)

---

## Prerequisites

| Requirement             | Notes                                              |
| ----------------------- | -------------------------------------------------- |
| .NET SDK 9.0+           | Build & run the API                                |
| Python 3.10+ with `pip` | Used by the `garminconnect` helper script          |
| Docker (optional)       | Recommended for the self-contained container image |
| Git                     | Version control                                    |

## Configuration

**All secrets and credentials are managed through environment variables.**  
No API keys, passwords, or personal data should ever be committed to the repository.

### 1. Create your `.env` file

Copy the provided template and fill in your values:

```powershell
cp .env.example .env
```

Then edit `.env` with your credentials:

```dotenv
# Required — Garmin Connect credentials
GARMIN_USERNAME=your-garmin-email@example.com
GARMIN_PASSWORD=your-garmin-password

# Optional — date range and limits
GARMIN_START_DATE=2025-01-01
GARMIN_END_DATE=2025-12-31
GARMIN_MAX_ACTIVITIES=50
GARMIN_REFRESH_MINUTES=60

# Optional — enables AI-powered Insights page
CHATGPT_API_KEY=sk-...your-openai-api-key...
```

> **The `.env` file is git-ignored and will never be committed.**

### 2. Environment variables reference

| Variable                 | Required | Description                                              |
| ------------------------ | -------- | -------------------------------------------------------- |
| `GARMIN_USERNAME`        | Yes      | Your Garmin Connect e-mail                               |
| `GARMIN_PASSWORD`        | Yes      | Your Garmin Connect password                             |
| `GARMIN_START_DATE`      | No       | Start date for activity import (`YYYY-MM-DD`)            |
| `GARMIN_END_DATE`        | No       | End date for activity import (`YYYY-MM-DD`)              |
| `GARMIN_MAX_ACTIVITIES`  | No       | Maximum number of activities to fetch per sync           |
| `GARMIN_REFRESH_MINUTES` | No       | Interval (minutes) between automatic sync runs           |
| `CHATGPT_API_KEY`        | No       | OpenAI API key for AI insights                           |
| `APP_DATA_DIR`           | No       | Directory for the SQLite database (defaults to `./data`) |

### 3. Local development settings

For local development, copy the template:

```powershell
cp GarminTempApi/appsettings.Development.json.template GarminTempApi/appsettings.Development.json
```

Edit the `PythonExecutable` path if your Python installation is not on `PATH`.  
This file is git-ignored so machine-specific paths stay out of the repo.

---

## Quickstart — build and run locally

```powershell
# 1. Install the Python dependency
pip install garminconnect

# 2. Create your .env (see Configuration above)
cp .env.example .env
# ... edit .env with your credentials ...

# 3. Build
dotnet build App_Garmin.sln -c Debug

# 4. Run the API + Razor UI
cd GarminTempApi
dotnet run -c Debug

# 5. Trigger an initial Garmin import (creates the DB on first run)
Invoke-RestMethod -Method Post http://localhost:5180/api/admin/trigger-import

# 6. Inspect activities
Invoke-RestMethod http://localhost:5180/api/garmindb/activities?limit=10 | Format-Table
```

> The host/port may differ — check the `dotnet run` output for `Now listening on:` and adjust accordingly.

- **Razor UI** is available at `http://localhost:5180/` (Activities / Sleep / Steps / Insights pages).
- The importer stores data in a local SQLite database (`garmin_app.db`) inside the directory set by `APP_DATA_DIR` (defaults to `./data`).

## Run with Docker

```powershell
# 1. Create your .env file (see Configuration above)
cp .env.example .env

# 2. Build and start the container (includes Python + garminconnect)
docker-compose up -d --build

# 3. Trigger an import
Invoke-RestMethod -Method Post http://localhost:5000/api/admin/trigger-import

# 4. Follow logs
docker-compose logs -f web

# 5. Open the UI
Start-Process http://localhost:5000/
```

- The container stores SQLite data under `/app/data` (persisted in the `garmin-data` Docker volume).
- All environment variables from your `.env` file are forwarded automatically by `docker-compose`.

---

## CI

A GitHub Actions workflow is included at `.github/workflows/dotnet.yml` that restores and builds the solution on push and pull requests.

---

## Third-party libraries and licenses

This project depends on the following open-source libraries:

### Python

| Library                                                  | License | Repository                                                                            |
| -------------------------------------------------------- | ------- | ------------------------------------------------------------------------------------- |
| [garminconnect](https://pypi.org/project/garminconnect/) | MIT     | [cyberjunky/python-garminconnect](https://github.com/cyberjunky/python-garminconnect) |

### .NET (NuGet)

| Package                                                                                                     | License      | Repository                                                |
| ----------------------------------------------------------------------------------------------------------- | ------------ | --------------------------------------------------------- |
| [Microsoft.AspNetCore.OpenApi](https://www.nuget.org/packages/Microsoft.AspNetCore.OpenApi)                 | MIT          | [dotnet/aspnetcore](https://github.com/dotnet/aspnetcore) |
| [Microsoft.EntityFrameworkCore](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore)               | MIT          | [dotnet/efcore](https://github.com/dotnet/efcore)         |
| [Microsoft.EntityFrameworkCore.Sqlite](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite) | MIT          | [dotnet/efcore](https://github.com/dotnet/efcore)         |
| [Markdig](https://www.nuget.org/packages/Markdig)                                                           | BSD-2-Clause | [xoofx/markdig](https://github.com/xoofx/markdig)         |

### Disclaimer

This project is **not affiliated with, endorsed by, or connected to Garmin Ltd.** in any way.  
"Garmin" and "Garmin Connect" are registered trademarks of Garmin Ltd. or its subsidiaries.  
This application accesses Garmin Connect data through the unofficial [`garminconnect`](https://github.com/cyberjunky/python-garminconnect) Python library, which uses a reverse-engineered API. **Use at your own risk** — Garmin may change or restrict access to this API at any time.

## License

This repository is released under the **MIT License** — see the [LICENSE](LICENSE) file for details.

---

## User Guide

Check out the [User Guide](docs/USER_GUIDE.md) for screenshots and a feature walkthrough.

