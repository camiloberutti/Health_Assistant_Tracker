
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

# (optional) Clean any residual build outputs in case they exist
# Note: this runs inside the container, not necessary in most cases but harmless
RUN rm -rf bin obj || true

# Publish the application using the project file explicitly
RUN dotnet publish "GarminTempApi.csproj" -c Release -o /app/publish

# 2. Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Expose port and define entrypoint
EXPOSE 80
ENTRYPOINT ["dotnet", "GarminTempApi.dll"]
