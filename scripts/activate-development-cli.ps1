#!/usr/bin/env pwsh

# Dot-source this file so the function it defines remains in the current shell:
#   . scripts/activate-development-cli.ps1

if ($MyInvocation.InvocationName -ne '.') {
    Write-Error 'this script must be dot-sourced: . scripts/activate-development-cli.ps1'
    exit 2
}

$wrightyDevScriptDir = Split-Path -Parent $PSCommandPath
$wrightyDevRoot = (Resolve-Path -LiteralPath (Join-Path $wrightyDevScriptDir '..')).Path
$wrightyDevConfiguration = if ($env:WRIGHTY_DEV_CONFIGURATION) { $env:WRIGHTY_DEV_CONFIGURATION } else { 'Debug' }
$wrightyDevProject = Join-Path $wrightyDevRoot 'src/Highbyte.Wrighty.Cli/Highbyte.Wrighty.Cli.csproj'
$wrightyDevOutputDir = Join-Path $wrightyDevRoot "src/Highbyte.Wrighty.Cli/bin/$wrightyDevConfiguration/net10.0"
$wrightyDevDll = Join-Path $wrightyDevOutputDir 'wrighty.dll'
# Windows PowerShell 5.1 predates $IsWindows and only ever runs on Windows.
$wrightyDevOnWindows = $IsWindows -or ($PSVersionTable.PSVersion.Major -le 5)
$wrightyDevExecutable = Join-Path $wrightyDevOutputDir $(if ($wrightyDevOnWindows) { 'wrighty.exe' } else { 'wrighty' })

$wrightyDevCleanup = {
    Remove-Variable -Name wrightyDevScriptDir, wrightyDevRoot, wrightyDevConfiguration,
        wrightyDevProject, wrightyDevOutputDir, wrightyDevDll, wrightyDevOnWindows,
        wrightyDevExecutable, wrightyDevCleanup -ErrorAction SilentlyContinue
}

if ($env:WRIGHTY_DEV_NO_BUILD -ne '1') {
    dotnet build $wrightyDevProject --configuration $wrightyDevConfiguration --nologo
    if ($LASTEXITCODE -ne 0) {
        . $wrightyDevCleanup
        return
    }
}
elseif (-not (Test-Path -LiteralPath $wrightyDevDll -PathType Leaf)) {
    Write-Error "development artifact does not exist: $wrightyDevDll"
    . $wrightyDevCleanup
    return
}

if (-not (Test-Path -LiteralPath $wrightyDevExecutable -PathType Leaf)) {
    Write-Error "development executable does not exist: $wrightyDevExecutable"
    . $wrightyDevCleanup
    return
}

$env:WRIGHTY_DEV_DLL = $wrightyDevDll

# A shell function is convenient in this shell, but child processes cannot normally inherit it.
# Prepending the apphost directory to PATH also makes `wrighty` available to agent CLIs launched
# after activation and to the command shells those agents create.
if (-not (Test-Path Env:WRIGHTY_DEV_ORIGINAL_PATH)) {
    $env:WRIGHTY_DEV_ORIGINAL_PATH = $env:PATH
}
$env:PATH = $wrightyDevOutputDir + [System.IO.Path]::PathSeparator + $env:WRIGHTY_DEV_ORIGINAL_PATH

function global:wrighty {
    dotnet $env:WRIGHTY_DEV_DLL @args
}

function global:wrighty_deactivate {
    if (Test-Path Env:WRIGHTY_DEV_ORIGINAL_PATH) {
        $env:PATH = $env:WRIGHTY_DEV_ORIGINAL_PATH
        Remove-Item Env:WRIGHTY_DEV_ORIGINAL_PATH
    }
    if (Test-Path Env:WRIGHTY_DEV_DLL) {
        Remove-Item Env:WRIGHTY_DEV_DLL
    }
    if (Test-Path Function:wrighty) {
        Remove-Item Function:wrighty
    }
    Write-Host 'Development Wrighty command deactivated.'
    if (Test-Path Function:wrighty_deactivate) {
        Remove-Item Function:wrighty_deactivate
    }
}

Write-Host "Development Wrighty command activated ($wrightyDevConfiguration)."
Write-Host 'Try: wrighty --help'
Write-Host 'Deactivate with: wrighty_deactivate'

. $wrightyDevCleanup
