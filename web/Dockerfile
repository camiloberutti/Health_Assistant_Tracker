
#---------------
# 1. Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

WORKDIR /src

# Copy only project file first and restore dependencies
COPY GarminTempApi/*.csproj ./GarminTempApi/
WORKDIR /src/GarminTempApi
RUN dotnet restore

# Copy everything else
# Copy the project source into the working directory (do NOT create an extra nested GarminTempApi folder)
COPY GarminTempApi/. .
COPY scripts ../scripts/

# (optional) Clean any residual build outputs in case they exist
# Note: this runs inside the container, not necessary in most cases but harmless
RUN rm -rf bin obj || true

# Publish the application using the project file explicitly
RUN dotnet publish "GarminTempApi.csproj" -c Release -o /app/publish

# 2. Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1

RUN apt-get update \
    && apt-get install -y --no-install-recommends python3 python3-venv git sqlite3 build-essential \
    && rm -rf /var/lib/apt/lists/*

# Install Garmin Connect dependencies in an isolated virtual environment.
RUN python3 -m venv /opt/garmin \
    && /opt/garmin/bin/pip install --upgrade pip \
    && /opt/garmin/bin/pip install --no-cache-dir garminconnect python-dateutil requests

ENV PATH="/opt/garmin/bin:${PATH}"

WORKDIR /app
COPY --from=build /app/publish .

# Default data directory inside the container to persist the SQLite database.
RUN mkdir -p /app/data

# Expose port and define entrypoint
EXPOSE 8080
ENTRYPOINT ["dotnet", "GarminTempApi.dll"]
