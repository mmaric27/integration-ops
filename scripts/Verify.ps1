[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ui = Join-Path $root 'ui'
$solution = Join-Path $root 'IntegrationOps.slnx'
$api = 'src/IntegrationOps.Api/IntegrationOps.Api.csproj'

# SDK selection follows the working directory, not the solution argument.
Push-Location $root
try {
    foreach ($tool in @('dotnet', 'docker', 'node', 'npm')) {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "Full verification requires $tool. No database tests will be skipped."
        }
    }

    dotnet --version
    if ($LASTEXITCODE -ne 0) { throw 'The SDK configured by global.json is unavailable.' }
    $dockerOs = docker info --format '{{.OSType}}'
    if ($LASTEXITCODE -ne 0 -or ($dockerOs -join '').Trim() -ne 'linux') {
        throw 'Full verification requires a running, accessible Linux Docker engine. Database tests are mandatory.'
    }
    docker compose version
    if ($LASTEXITCODE -ne 0) { throw 'Docker Compose is required for the supported development setup.' }

    dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the pinned local dotnet-ef tool failed.' }

    dotnet restore $solution --locked-mode -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore or its vulnerability audit failed.' }

    dotnet build $solution --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    dotnet test $solution --no-build
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }

    dotnet format $solution --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet format verification failed.' }

    # Use the runtime registration/model without contacting an interactive development database.
    # Child-process configuration does not replace the caller's connection string or User Secrets.
    $driftStart = New-Object System.Diagnostics.ProcessStartInfo
    $driftStart.FileName = (Get-Command dotnet -ErrorAction Stop).Source
    $driftStart.WorkingDirectory = $root
    $driftStart.UseShellExecute = $false
    $driftStart.Arguments = "ef migrations has-pending-model-changes --no-build --project $api --startup-project $api --context IntegrationOpsDbContext -- --environment Development"
    $driftStart.EnvironmentVariables['ConnectionStrings__IntegrationOps'] = 'Host=127.0.0.1;Database=integration_ops_model_check;Username=integration_ops;Pooling=false'
    $drift = [System.Diagnostics.Process]::Start($driftStart)
    try {
        $drift.WaitForExit()
        if ($drift.ExitCode -ne 0) { throw 'EF model and checked-in migrations differ, or the model check failed.' }
    }
    finally {
        $drift.Dispose()
    }

    $auditLines = dotnet package list --project $solution --no-restore --vulnerable --include-transitive --format json --output-version 1
    if ($LASTEXITCODE -ne 0) { throw 'NuGet vulnerability review failed.' }
    $auditJson = $auditLines -join [Environment]::NewLine
    $auditJson
    $audit = $auditJson | ConvertFrom-Json -ErrorAction Stop
    if ($audit.version -ne 1 -or $audit.projects -isnot [Array] -or $audit.projects.Count -eq 0) {
        throw 'NuGet vulnerability review returned an unsupported or incomplete report.'
    }
    if ($audit.logs) {
        throw 'NuGet vulnerability review reported diagnostics; review them before claiming a clean audit.'
    }

    foreach ($project in $audit.projects) {
        if ($project.logs) {
            throw 'NuGet vulnerability review reported project diagnostics; the audit is incomplete.'
        }
        foreach ($framework in $project.frameworks) {
            foreach ($package in (@($framework.topLevelPackages) + @($framework.transitivePackages))) {
                if ($package.vulnerabilities) {
                    throw 'NuGet vulnerability review found one or more vulnerable packages.'
                }
            }
        }
    }

    Set-Location $ui
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }

    npm run lint
    if ($LASTEXITCODE -ne 0) { throw 'npm lint failed.' }

    npm run test
    if ($LASTEXITCODE -ne 0) { throw 'npm test failed.' }

    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm build failed.' }

    npm audit --audit-level=moderate
    if ($LASTEXITCODE -ne 0) { throw 'npm audit failed.' }
}
finally {
    Pop-Location
}
