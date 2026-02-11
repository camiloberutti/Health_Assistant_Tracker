# Health_Assistant_Tracker

A comprehensive personal health dashboard built with **ASP.NET Core 9.0**, **AI-driven insights**, and a lot of **vibecoding**. This application aggregates data from your **Garmin** devices (via Garmin Connect), visualizes your performance, and uses OpenAI to provide daily coaching recommendations.

> **Note**: Currently, this project supports **Garmin devices only**.

## 🎯 Project Vision

The core goal of this project is to evolve the **Recommendation Agents** into a truly intelligent coaching system. We aim to move beyond simple summaries and create an agent that:
1.  **Gives better recommendations**: Deeper context awareness, less generic advice, and "smarter" coaching.
2.  **Incorporates Routines**: Seamlessly suggests and adds workouts to your routine calendar based on your recovery and goals.

##  Documentation

*   **[User Guide](docs/USER_GUIDE.md)**: Walkthrough of the dashboard features.
*   **[Architecture](docs/ARCHITECTURE.md)**: Technical deep-dive into the stack, data flow, and components.
*   **[Contributing & Roadmap](docs/CONTRIBUTING.md)**: How to help out and future ideas.

---

##  How to Run

You can run this project using **Docker** (recommended) or locally with **.NET**.

### Prerequisites
*   **Garmin Connect Account**: Email and password.
*   **OpenAI API Key** (Optional): For AI coaching features.

### Option A: Docker Compose (Recommended)

This is the easiest way to get started. It builds the container (including .NET and Python dependencies) and starts the server.

1.  **Clone the repository**:
    \\\ash
    git clone https://github.com/camiloberutti/Health_Assistant_Tracker.git
    cd Health_Assistant_Tracker
    \\\

2.  **Configure Environment**:
    Copy the example file and edit it with your credentials.
    \\\powershell
    cp .env.example .env
    # Edit .env with your GARMIN_USERNAME, GARMIN_PASSWORD, etc.
    \\\

3.  **Run**:
    \\\powershell
    docker-compose up -d
    \\\
    The app will be available at **http://localhost:5180**.

4.  **Update/Rebuild**:
    If you make code changes, run:
    \\\powershell
    ./apply-changes.ps1
    \\\

### Option B: Local Development (.NET CLI)

1.  **Install Dependencies**:
    *   [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
    *   [Python 3.10+](https://www.python.org/downloads/) (Make sure 'python' is in your PATH)
    *   Install the Garmin library:
        \\\ash
        pip install garminconnect
        \\\

2.  **Configure**:
    Ensure your \.env\ file is created (see step 2 in Option A). The app reads this file on startup.

3.  **Run**:
    \\\powershell
    cd GarminTempApi
    dotnet watch run
    \\\

---

##  Configuration (.env)

| Variable                 | Description                                           | Default |
| ------------------------ | ----------------------------------------------------- | ------- |
| \GARMIN_USERNAME\        | Your Garmin Connect login email (Required)            | -       |
| \GARMIN_PASSWORD\        | Your Garmin Connect password (Required)               | -       |
| \GARMIN_REFRESH_MINUTES\ | Activity sync interval (minutes)                      | \60\    |
| \GARMIN_START_DATE\      | Start of history import (YYYY-MM-DD)                  | -       |
| \GARMIN_END_DATE\        | End date (Leave empty to always sync up to **today**) | -       |
| \CHATGPT_API_KEY\        | OpenAI Key for insights (Optional)                    | -       |

---

##  Contributing

We want to make this the best self-hosted Garmin dashboard available. Check out the **[Contributing Guide](docs/CONTRIBUTING.md)** for:
*   Step-by-step development instructions.
*   List of planned features (Mobile support, Dark mode, etc.).
*   Architecture details.

