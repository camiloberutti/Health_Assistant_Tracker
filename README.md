# App_Garmin

Small ASP.NET 9.0 Web API for reading Garmin activity data (first-version skeleton).

This repository contains:
- `GarminTempApi/` — ASP.NET Web API project (targets .NET 9.0)
- `web/Dockerfile` — multi-stage Dockerfile for building and running the API
- `garmindb/` — DB Docker context

Quick goals for this first GitHub version:
- Build and verify the solution builds on CI
- Provide Dockerfile to run locally
- Provide minimal documentation and license

## Prerequisites
- .NET SDK 9.0 (or matching runtime for development)
- Docker (optional, for container runs)
- Git

## Quickstart — build and run locally
From repository root (PowerShell):

```powershell
# Build
dotnet build "C:\Users\camil\Desktop\App_Garmin\App_Garmin.sln" -c Debug

# Run API locally
cd "C:\Users\camil\Desktop\App_Garmin\GarminTempApi"
dotnet run -c Debug

# Once started, test:
Invoke-RestMethod http://localhost:8080/weatherforecast
```

(If the app listens on a different port, check the console log for "Now listening on:" and adjust the URL.)

## Run with Docker

```powershell
# Build image
docker build -f "C:\Users\camil\Desktop\App_Garmin\web\Dockerfile" -t appgarmin-web "C:\Users\camil\Desktop\App_Garmin"

# Run container (map host 8080 to container 8080)
docker run -d -p 8080:8080 --name appgarmin-run appgarmin-web

# Check logs
docker logs --tail 200 appgarmin-run
```

## CI
A GitHub Actions workflow is included at `.github/workflows/dotnet.yml` that restores and builds the solution on push and pull requests.

## License
This repository is released under the MIT license — see `LICENSE` file. Replace the author name in the LICENSE file with your own before publishing if desired.

## Next steps / suggestions
- Add a `CONTRIBUTING.md` and issue/PR templates if you plan to accept contributions.
- Fix nullable warnings in the codebase to reduce noise.
- Add a lightweight health/readiness endpoint (e.g., `/health`).
- Configure branch protection and required status checks for the `main` branch.
