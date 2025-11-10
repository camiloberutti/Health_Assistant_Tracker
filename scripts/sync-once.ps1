param(
    [switch]$NoBuild
)

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    if (Test-Path '.env') {
        Get-Content '.env' | ForEach-Object {
            if ($_ -match '^\s*#' -or -not $_) { return }
            $name, $value = $_ -split '=', 2
            [Environment]::SetEnvironmentVariable($name, $value, 'Process')
        }
    }

    [Environment]::SetEnvironmentVariable('GARMIN_REFRESH_MINUTES', '0', 'Process')
    [Environment]::SetEnvironmentVariable('GARMIN_SYNC_ONCE', '1', 'Process')

    $arguments = @('--project', 'GarminTempApi/GarminTempApi.csproj')
    if ($NoBuild) {
        $arguments += '--no-build'
    }

    dotnet run @arguments
}
finally {
    Pop-Location
}
