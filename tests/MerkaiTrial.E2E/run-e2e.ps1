<#
=====================================================================
 FILE: tests/MerkaiTrial.E2E/run-e2e.ps1

 Runs Merkai's end-to-end tests. In order:
   1. Builds the solution.
   2. Starts the WebApi and Admin.Web against the TEST database
      (MerkaiTrial_E2E), on their own ports, with NO email key and NO
      WhatsApp token — so nothing is ever sent to anyone during a run.
   3. Runs the tests (Chromium is downloaded on the first run).
   4. Stops both apps again.

 Examples (from the solution folder, in PowerShell):

   .\tests\MerkaiTrial.E2E\run-e2e.ps1                     # everything
   .\tests\MerkaiTrial.E2E\run-e2e.ps1 -Headed              # watch the browser
   .\tests\MerkaiTrial.E2E\run-e2e.ps1 -Filter "Name~Pipeline"
   .\tests\MerkaiTrial.E2E\run-e2e.ps1 -ServersOnly         # start the apps and
                                     # leave them running, for Test Explorer

 The test database is your development connection string with the
 database name swapped for MerkaiTrial_E2E. Override with
 -ConnectionString "..." if yours lives elsewhere.

 090: also passes the WebApi's address to the tests (E2E_API_URL).

 100: a signing key for THIS RUN ONLY. Some tests call the WebApi
      directly, as a given person, so they need to make the same kind of
      token Admin.Web makes. The script makes a fresh random key each
      run and gives it to BOTH test servers (Jwt__SigningKey, with
      Jwt__Issuer=merkaitrial-admin and Jwt__Audience=merkaitrial-api)
      and to the tests (E2E_JWT_KEY). It is never your real key and is
      never saved in appsettings.
        -ServersOnly: the key is also written to e2e-logs\e2e-jwt.key
        while the servers run, so Test Explorer finds it, and the file is
        deleted when you press Enter to stop them.
        A server that is ALREADY running keeps the key it started with.
        If e2e-logs\e2e-jwt.key exists (a -ServersOnly window is open),
        that key is reused; if not, the script warns you, because the
        direct-API tests will then be refused.
=====================================================================
#>
[CmdletBinding()]
param(
    [string]$ConnectionString,
    [string]$Database = 'MerkaiTrial_E2E',
    [switch]$Headed,
    [string]$Filter,
    [switch]$ServersOnly,
    [switch]$SkipBuild,
    [int]$AdminPort = 7393,
    [int]$ApiPort = 7391
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

# ── Find the solution and the two apps ──────────────────────────────
$root = $here
while ($root -and -not (Get-ChildItem -Path $root -Filter *.sln -File -ErrorAction SilentlyContinue)) {
    $parent = Split-Path $root -Parent
    if (-not $parent -or $parent -eq $root) { $root = $null } else { $root = $parent }
}
if (-not $root) { throw "Could not find a .sln file in any folder above $here." }
$sln = (Get-ChildItem -Path $root -Filter *.sln -File | Select-Object -First 1).FullName

function Find-Project([string]$name) {
    $p = Get-ChildItem -Path $root -Recurse -Filter "$name.csproj" -File -ErrorAction SilentlyContinue |
         Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
         Select-Object -First 1
    if (-not $p) { throw "Could not find $name.csproj under $root." }
    return $p
}

$adminProj = Find-Project 'MerkaiTrial.Admin.Web'
$apiProj   = Find-Project 'MerkaiTrial.WebApi'
$testProj  = Join-Path $here 'MerkaiTrial.E2E.csproj'

$adminUrl = "https://localhost:$AdminPort"
$apiUrl   = "https://localhost:$ApiPort"

# ── The test database ────────────────────────────────────────────────
if (-not $ConnectionString) { $ConnectionString = $env:E2E_DB }
if (-not $ConnectionString) {
    $devCs = $null
    foreach ($f in @('appsettings.Development.json', 'appsettings.json')) {
        $path = Join-Path $adminProj.DirectoryName $f
        if (-not (Test-Path $path)) { continue }
        try {
            # Windows PowerShell's JSON reader refuses // comments; drop them.
            $raw  = (Get-Content $path -Raw) -replace '(?m)^\s*//.*$', ''
            $json = $raw | ConvertFrom-Json
            if ($json.ConnectionStrings -and $json.ConnectionStrings.Default) {
                $devCs = $json.ConnectionStrings.Default
                break
            }
        } catch { }
    }
    if (-not $devCs) {
        throw "Couldn't read ConnectionStrings:Default from Admin.Web's appsettings. " +
              "Pass it yourself: -ConnectionString 'Server=...;Database=MerkaiTrial_E2E;...'"
    }

    $b = New-Object System.Data.Common.DbConnectionStringBuilder
    $b.set_ConnectionString($devCs)
    foreach ($k in @('Database', 'Initial Catalog')) { if ($b.ContainsKey($k)) { [void]$b.Remove($k) } }
    $b['Database'] = $Database
    $ConnectionString = $b.ConnectionString
}

$check = New-Object System.Data.Common.DbConnectionStringBuilder
$check.set_ConnectionString($ConnectionString)
$dbName = if ($check.ContainsKey('Database')) { $check['Database'] } elseif ($check.ContainsKey('Initial Catalog')) { $check['Initial Catalog'] } else { '' }
if (-not ("$dbName" -match '_E2E$')) {
    throw "Refusing to run against database '$dbName'. The tests change data, so the database name must end in _E2E."
}

Write-Host ""
Write-Host "Merkai end-to-end tests" -ForegroundColor Cyan
Write-Host "  Solution : $sln"
Write-Host "  Database : $dbName"
Write-Host "  Admin.Web: $adminUrl"
Write-Host "  WebApi   : $apiUrl"
Write-Host ""

# ── Build ────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Host "Building the solution..." -ForegroundColor Cyan
    & dotnet build $sln -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "The build failed. If Visual Studio is running Merkai, stop debugging first (it locks the files)."
    }
}

# ── Start the apps ───────────────────────────────────────────────────
$logDir = Join-Path $here 'e2e-logs'
New-Item -ItemType Directory -Force -Path $logDir | Out-Null

# ── 100: this run's signing key ──────────────────────────────────────
$jwtIssuer   = 'merkaitrial-admin'
$jwtAudience = 'merkaitrial-api'
$keyFile     = Join-Path $logDir 'e2e-jwt.key'
$wroteKeyFile = $false

function New-SigningKey {
    # 48 random bytes as 96 hex characters: well over the 32 bytes
    # HMAC-SHA256 needs, and nothing in it that a shell could mangle.
    $bytes = New-Object byte[] 48
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return (($bytes | ForEach-Object { $_.ToString('x2') }) -join '')
}

function Test-Up([string]$url) {
    $code = & curl.exe -k -s -o NUL -w "%{http_code}" --max-time 3 $url 2>$null
    return ($code -and $code -ne '000')
}

function Wait-Up([string]$url, [System.Diagnostics.Process]$proc, [string]$name, [int]$seconds = 120) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Up $url) { return }
        if ($proc -and $proc.HasExited) {
            $tail = Get-Content (Join-Path $logDir "$name.out.log") -Tail 30 -ErrorAction SilentlyContinue
            $errTail = Get-Content (Join-Path $logDir "$name.err.log") -Tail 30 -ErrorAction SilentlyContinue
            throw "$name stopped while starting. Last lines of its log:`n$($tail -join "`n")`n$($errTail -join "`n")"
        }
        Start-Sleep -Milliseconds 700
    }
    throw "$name did not answer at $url within $seconds seconds. See $logDir."
}

function Start-App($proj, [string]$name, [string]$urls, [hashtable]$extra) {
    # Every setting that makes a run safe. Environment variables beat
    # appsettings AND user secrets, so these win over your dev keys.
    $vars = @{
        'ASPNETCORE_ENVIRONMENT'     = 'Development'
        'ASPNETCORE_URLS'            = $urls
        'ConnectionStrings__Default' = $ConnectionString
        'Email__ApiKey'              = ' '     # blank = NullEmailSender: queue, never send
        'Email__RedirectAllTo'       = 'e2e-sink@example.invalid'
        'WhatsApp__Enabled'          = 'false'
        'WhatsApp__AccessToken'      = ' '
        'Email__AppBaseUrl'          = $adminUrl
    }
    foreach ($k in $extra.Keys) { $vars[$k] = $extra[$k] }

    $saved = @{}
    foreach ($k in $vars.Keys) {
        $saved[$k] = [Environment]::GetEnvironmentVariable($k, 'Process')
        [Environment]::SetEnvironmentVariable($k, $vars[$k], 'Process')
    }
    try {
        $runArgs = @('run', '--no-build', '--no-launch-profile', '--project', "`"$($proj.FullName)`"")
        return Start-Process -FilePath 'dotnet' -ArgumentList $runArgs `
            -WorkingDirectory $proj.DirectoryName `
            -RedirectStandardOutput (Join-Path $logDir "$name.out.log") `
            -RedirectStandardError  (Join-Path $logDir "$name.err.log") `
            -NoNewWindow -PassThru
    }
    finally {
        foreach ($k in $saved.Keys) { [Environment]::SetEnvironmentVariable($k, $saved[$k], 'Process') }
    }
}

function Stop-App($proc) {
    if ($proc -and -not $proc.HasExited) {
        # /T: dotnet run starts the app as a child process; stop both.
        try { & taskkill.exe /PID $proc.Id /T /F *> $null } catch { }
    }
}

$api = $null; $admin = $null
$exitCode = 0
try {
    # 100: one key for both servers and the tests. If either server is
    # already up, it has the key it was started with — the one in the key
    # file, when a -ServersOnly window started it.
    $apiAlreadyUp   = Test-Up $apiUrl
    $adminAlreadyUp = Test-Up "$adminUrl/Account/Login"
    $jwtKey = $null
    if (($apiAlreadyUp -or $adminAlreadyUp) -and (Test-Path $keyFile)) {
        $jwtKey = (Get-Content $keyFile -Raw).Trim()
        Write-Host "Reusing this run's signing key from $keyFile (the running server has it)." -ForegroundColor Yellow
    }
    if (-not $jwtKey) {
        $jwtKey = New-SigningKey
        if ($apiAlreadyUp -or $adminAlreadyUp) {
            Write-Host "WARNING: a test server is already running, but $keyFile does not exist, so its" -ForegroundColor Yellow
            Write-Host "signing key is unknown. The tests that call the WebApi directly will be refused." -ForegroundColor Yellow
            Write-Host "Stop the running server(s) and run this script again to fix that." -ForegroundColor Yellow
        }
    }
    $jwtVars = @{
        'Jwt__SigningKey' = $jwtKey
        'Jwt__Issuer'     = $jwtIssuer
        'Jwt__Audience'   = $jwtAudience
    }

    if ($apiAlreadyUp) {
        Write-Host "WebApi is already running at $apiUrl - using it." -ForegroundColor Yellow
    } else {
        Write-Host "Starting WebApi..." -ForegroundColor Cyan
        $api = Start-App $apiProj 'webapi' $apiUrl $jwtVars
        Wait-Up $apiUrl $api 'webapi'
    }

    if ($adminAlreadyUp) {
        Write-Host "Admin.Web is already running at $adminUrl - using it." -ForegroundColor Yellow
    } else {
        Write-Host "Starting Admin.Web..." -ForegroundColor Cyan
        $adminVars = @{ 'WebApi__BaseUrl' = "$apiUrl/" }
        foreach ($k in $jwtVars.Keys) { $adminVars[$k] = $jwtVars[$k] }
        $admin = Start-App $adminProj 'adminweb' $adminUrl $adminVars
        Wait-Up "$adminUrl/Account/Login" $admin 'adminweb'
    }

    Write-Host "Both apps are up. Logs: $logDir" -ForegroundColor Green

    if ($ServersOnly) {
        # 100: Test Explorer has no E2E_JWT_KEY, so leave the key where
        # E2ESettings looks for it — only while these servers run.
        if ($api -or $admin) {
            Set-Content -Path $keyFile -Value $jwtKey -NoNewline -Encoding ASCII
            $wroteKeyFile = $true
        }
        Write-Host ""
        Write-Host "Servers are running for Test Explorer. In Visual Studio, run the tests in" -ForegroundColor Green
        Write-Host "MerkaiTrial.E2E. (Set HEADED=1 in your environment to watch the browser.)" -ForegroundColor Green
        Write-Host "If your SQL Server is not 'localhost', set the E2E_DB environment variable to:" -ForegroundColor Green
        Write-Host "  $ConnectionString"
        Read-Host "Press Enter here to stop the servers"
        return
    }

    # ── Run the tests ────────────────────────────────────────────────
    $env:E2E_BASE_URL = $adminUrl
    $env:E2E_API_URL  = $apiUrl     # 090: the attachment test checks files cannot be fetched from the API directly
    $env:E2E_DB       = $ConnectionString
    $env:E2E_JWT_KEY      = $jwtKey          # 100: the direct-API tests sign their tokens with it
    $env:E2E_JWT_ISSUER   = $jwtIssuer
    $env:E2E_JWT_AUDIENCE = $jwtAudience
    if ($Headed) { $env:HEADED = '1'; if (-not $env:E2E_SLOWMO) { $env:E2E_SLOWMO = '150' } }

    $testArgs = @('test', $testProj, '--nologo', '--logger', 'console;verbosity=normal')
    if ($Filter) { $testArgs += @('--filter', $Filter) }

    # Everything the tests print also goes to a file, so the whole run can
    # be attached instead of copied out of the window piece by piece.
    $runLog     = Join-Path $logDir 'test-run.txt'
    $summaryLog = Join-Path $logDir 'summary.txt'

    Write-Host "Running the tests..." -ForegroundColor Cyan
    & dotnet @testArgs | Tee-Object -FilePath $runLog
    $exitCode = $LASTEXITCODE

    # A short version: each failed test and its message, once.
    $lines = Get-Content $runLog
    $summary = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*Failed\s+(\S.*?)\s+\[') {
            $name = $Matches[1]
            if ($seen.ContainsKey($name)) { continue }
            $seen[$name] = $true
            $message = ''
            for ($j = $i + 1; $j -lt [Math]::Min($i + 6, $lines.Count); $j++) {
                if ($lines[$j] -match 'Error Message:') {
                    if ($j + 1 -lt $lines.Count) { $message = $lines[$j + 1].Trim() }
                    break
                }
            }
            $summary.Add("FAILED  $name")
            if ($message) { $summary.Add("        $message") }
        }
        elseif ($line -match '^\s*(Total tests|Passed|Failed|Skipped|Total time)\s*:') {
            $summary.Add($line.Trim())
        }
    }
    $summary | Set-Content -Path $summaryLog -Encoding UTF8

    $artifacts = Join-Path $here 'bin\Debug\net8.0\e2e-artifacts'
    Write-Host ""
    Write-Host "Full output : $runLog"
    Write-Host "Short list  : $summaryLog"
    if ($exitCode -ne 0) {
        Write-Host ""
        Write-Host "Some tests failed. Send me summary.txt and test-run.txt from e2e-logs." -ForegroundColor Red
        Write-Host "Traces and screenshots of the failures are in:" -ForegroundColor Red
        Write-Host "  $artifacts" -ForegroundColor Red
        Write-Host "Open a trace by dragging the .zip onto https://trace.playwright.dev" -ForegroundColor Red
    } else {
        Write-Host ""
        Write-Host "All tests passed." -ForegroundColor Green
    }
}
finally {
    Stop-App $admin
    Stop-App $api
    # 100: the key file lives only as long as the servers it belongs to.
    if ($wroteKeyFile -and (Test-Path $keyFile)) {
        Remove-Item $keyFile -Force -ErrorAction SilentlyContinue
    }
    Remove-Item Env:\E2E_JWT_KEY -ErrorAction SilentlyContinue
}

exit $exitCode
