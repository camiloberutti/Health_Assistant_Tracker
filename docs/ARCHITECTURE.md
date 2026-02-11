# System Architecture

This document describes the technical architecture of the **Health_Assistant_Tracker** project.

## High-Level Overview

The application is a monolith built with **ASP.NET Core 9.0** that serves both as a REST API and a web server for server-side rendered UI (Razor Pages).

It utilizes a hybrid approach for data fetching:
1.  **C# / .NET**: Manages the application lifecycle, database, UI, and business logic.
2.  **Python**: Handles the interaction with Garmin Connect's private API using the `garminconnect` library.

### Technology Stack

*   **Framework**: .NET 9.0 (ASP.NET Core)
*   **Language**: C# 12
*   **Database**: SQLite (via Entity Framework Core)
*   **Frontend**: Razor Pages, Bootstrap 5, Vanilla JavaScript
*   **External Integration**: Python 3.10+ (for `garminconnect` library)
*   **AI**: OpenAI API (GPT-4o / GPT-3.5) for insights

---

## Core Components

### 1. Data Ingestion Pipeline (`GarminTempApi/Services`)

The ingestion process is unique as it bridges .NET and Python.

*   **`GarminConnectImporter.cs`**: This C# service acts as an orchestrator. It builds a process to execute the `garmin_fetch.py` script.
*   **`scripts/garmin_fetch.py`**: A Python script that:
    1.  Logs in to Garmin Connect using credentials passed via arguments.
    2.  Fetches Activity, Sleep, and Step data for the requested date range.
    3.  Outputs the data as a standard JSON payload to `stdout`.
*   **`GarminDataSyncService.cs`**: Parses the JSON output from the Python script and persists it into the SQLite database, handling deduplication and updates.
*   **`GarminSyncHostedService.cs`**: A generic host background service that triggers the sync periodically (based on `GARMIN_REFRESH_MINUTES`).

### 2. Database Model (`GarminTempApi/Data`, `GarminTempApi/Models`)

We use **Entity Framework Core** with the SQLite provider.

*   **`AppDbContext`**: The main DB context.
*   **Key Entities**:
    *   `Activity`: Represents a workout (Run, Cycle, Swim, etc.). Contains summary metrics.
    *   `ActivityPoint`: Granular telemetry data (lat/lon, heart rate, elevation) for a specific activity.
    *   `SleepSummary`: Daily sleep statistics.
    *   `SleepDetailSnapshot`: Detailed sleep stage data (Rem, Deep, Light).
    *   `RoutineEvent`: Custom user-defined calendar events.

### 3. AI Insights (`GarminTempApi/Services/OpenAiInsightService.cs`)

The application provides AI-driven coaching advice.

*   **Data Aggregation**: `InsightDataBuilder` compiles a "Context Payload" containing recent sleep, activity load, and calendar events.
*   **Prompt Engineering**: The service constructs a system prompt that gives the AI a persona (Elite Sports Performance Coach) and constraints (JSON output).
*   **Execution**: Sends the payload to the OpenAI Chat Completion API and parses the response for display in the UI.

### 4. User Interface (`GarminTempApi/Pages`)

*   **`_Layout.cshtml`**: Defines the responsive sidebar navigation and global styles.
*   **Razor Pages**:
    *   `Index.cshtml`: Dashboard with summary widgets.
    *   `Activities/*`: List and detailed views of workouts.
    *   `Insights/Index.cshtml`: Interact with the AI coach.
    *   `MyRoutine.cshtml`: Drag-and-drop calendar for planning.

---

## Project Structure

```text
App_Garmin/
├── GarminTempApi/           # Main ASP.NET Core Project
│   ├── Background/          # Background services (Cron jobs)
│   ├── Controllers/         # API Endpoints (REST)
│   ├── Data/                # EF Core Context & Migrations
│   ├── Models/              # Database Entities
│   ├── Pages/               # Razor UI Pages
│   ├── Services/            # Business Logic & Parsers
│   └── Utilities/           # Helper classes
├── docs/                    # Documentation
├── garmindb/                # Legacy/Docker context support
├── scripts/                 # Python scripts for data fetching
├── web/                     # Dockerfile for the web service
└── docker-compose.yml       # Orchestration
```

## External Links

*   **Garmin Connect Library**: [cyberjunky/python-garminconnect](https://github.com/cyberjunky/python-garminconnect)
*   **ASP.NET Core Docs**: [learn.microsoft.com](https://learn.microsoft.com/en-us/aspnet/core/?view=aspnetcore-9.0)
*   **Bootstrap 5**: [getbootstrap.com](https://getbootstrap.com/docs/5.3/getting-started/introduction/)
