param(
    [ValidateRange(1024, 65535)][int]$Port = 5286,
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$appRoot = Join-Path $PSScriptRoot 'PlaytestOps.Web'
$projectPath = Join-Path $appRoot 'PlaytestOps.Web.csproj'
$appDll = Join-Path $appRoot 'bin\Debug\net10.0\PlaytestOps.Web.dll'
$databasePath = Join-Path $appRoot 'App_Data\playtestops.db'
$url = "http://localhost:$Port"
if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
    throw "Port $Port is already in use. Stop the existing dashboard normally before starting another."
}
if (!$NoBuild) {
    & dotnet build $projectPath --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed; no server was started.' }
}
if (!(Test-Path -LiteralPath $appDll)) { throw 'Build the dashboard before using -NoBuild.' }
$logRoot = Join-Path $appRoot 'App_Data\logs'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$stdoutPath = Join-Path $logRoot "dashboard-$stamp.log"
$stderrPath = Join-Path $logRoot "dashboard-$stamp-errors.log"
$settings = @{
    ASPNETCORE_ENVIRONMENT = 'Development'
    ASPNETCORE_URLS = $url
    ConnectionStrings__DefaultConnection = "Data Source=$databasePath"
}
$previousSettings = @{}
try {
    foreach ($name in $settings.Keys) {
        $previousSettings[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    $server = Start-Process -FilePath (Get-Command dotnet -CommandType Application).Source `
        -ArgumentList ('"' + $appDll + '" --contentRoot "' + $appRoot + '"') `
        -WorkingDirectory $appRoot -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
}
finally {
    foreach ($name in $previousSettings.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previousSettings[$name], 'Process')
    }
}
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ($server.HasExited) { break }
    try {
        $response = Invoke-WebRequest "$url/Editors" -TimeoutSec 2
        if ($response.StatusCode -eq 200) { $ready = $true; break }
    }
    catch { Start-Sleep -Milliseconds 250 }
}
if (!$ready) {
    if (!$server.HasExited) { Stop-Process -Id $server.Id }
    throw "Dashboard did not become ready. Inspect $stderrPath and $stdoutPath."
}
[pscustomobject]@{
    ProcessId = $server.Id
    Url = $url
    SourceRoot = $PSScriptRoot
    Database = $databasePath
    OutputLog = $stdoutPath
    ErrorLog = $stderrPath
}
