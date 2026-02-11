# Contributing & Future Improvements

We welcome contributions to improve the **Health_Assistant_Tracker** project! Whether you want to fix a bug, improve the documentation, or add a shiny new feature, this guide will help you get started.

## How to Contribute

1.  **Fork the Repository**: Create your own copy of the project.
2.  **Create a Feature Branch**: `git checkout -b feature/amazing-feature`.
3.  **Make Changes**: Write your code and ensure it follows the project's style (standard C# conventions).
4.  **Test**: Run the application locally and verify your changes work.
5.  **Commit**: `git commit -m "Add amazing feature"`.
6.  **Push**: `git push origin feature/amazing-feature`.
7.  **Open a Pull Request**: Submit your changes for review.

## Development Setup

See the [README](../README.md) for detailed instructions on how to set up your `.env` file and run the project locally using Docker or the .NET CLI.

### Debugging Tips
*   **Failed Imports**: If data isn't showing up, check the logs (`docker-compose logs web`). The Python script output is captured there. Ensure your Garmin credentials are correct.
*   **Database**: The SQLite database is stored in the `data/` folder. You can use tools like *DB Browser for SQLite* to inspect the raw data.

---

## Future Improvements / Roadmap

Here are some ideas for features that would make this project even better. Feel free to pick one up!

### 🏗️ Architecture & Backend
*   **Native C# Garmin Client**: Replace the Python dependency with a pure C# HTTP client to remove the Python runtime requirement and improve performance.
*   **Hangfire Integration**: Replace the simple background service with [Hangfire](https://www.hangfire.io/) for more robust job scheduling and a dashboard for failed jobs.
*   **Message Queue**: Decouple data fetching from processing using a queue (e.g., RabbitMQ or simple in-memory Channels) for better scalability.

### 🎨 Frontend & UX
*   **Interactive Charts**: Replace the basic charts with a library like [Chart.js](https://www.chartjs.org/) or [ApexCharts](https://apexcharts.com/) for zooming and panning capabilities.
*   **Mobile App**: Create a progressive web app (PWA) manifest so users can install it on their phones.
*   **Dark Mode**: Implement a system-wide dark mode toggle.

### 🤖 AI & Insights
*   **Smarter Recommendation Agents**: Improve the prompt engineering to move from generic advice to specific, actionable coaching that feels "human" and context-aware.
*   **Dynamic Routine Incorporation**: Build logic that allows the AI to automatically propose and insert specific workouts into the "My Routine" calendar (e.g., "You are recovered, I added an interval run for Tuesday").
*   **Long-term Trend Analysis**: Enhance the AI service to analyze monthly or yearly trends rather than just the last week.
*   **Conversational Memory**: Store the chat history in the database so the AI remembers context from previous sessions.
*   **Personalized Training Plans**: Allow the AI to generate a structured 4-week training plan and insert it directly into the "My Routine" calendar.

### 🔒 Security
*   **User Accounts**: Currently, it's a single-user system. Add ASP.NET Identity to support multiple users with their own Garmin accounts.
*   **Vault Integration**: Support Docker Secrets or Azure Key Vault for managing credentials more securely than `.env` files.
