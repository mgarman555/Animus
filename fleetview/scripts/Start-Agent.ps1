# Start one agent from fleet.json with FleetView identity env set.
# Usage: .\scripts\Start-Agent.ps1 -Project animus -Agent refactorer
[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)][string]$Project,
  [Parameter(Mandatory = $true)][string]$Agent
)
$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Fleet = Get-Content (Join-Path $Root "fleet.json") -Raw | ConvertFrom-Json
$Reporter = Join-Path $Root "reporter/report.js"

$proj = $Fleet.projects | Where-Object { $_.name -eq $Project } | Select-Object -First 1
if (-not $proj) { Write-Error "Project '$Project' not found in fleet.json"; exit 1 }
$ag = $proj.agents | Where-Object { $_.name -eq $Agent } | Select-Object -First 1
if (-not $ag) { Write-Error "Agent '$Agent' not found in project '$Project'"; exit 1 }

$workdir = if ($ag.cwd) { $ag.cwd } else { $proj.path }
$port = if ($Fleet.port) { $Fleet.port } else { 4700 }

$env:AGENT_PROJECT = $Project
$env:AGENT_NAME = $Agent
$env:FLEETVIEW_URL = "http://127.0.0.1:$port"
if ($ag.env) { $ag.env.PSObject.Properties | ForEach-Object { Set-Item "env:$($_.Name)" $_.Value } }

Set-Location $workdir
Write-Host "[FleetView] $Project/$Agent  in  $workdir" -ForegroundColor Cyan

# boot breadcrumb so the card lights up immediately
node $Reporter --state working --title "launching..." --source claude-code | Out-Null

try {
  if ($ag.type -eq "claude-code") {
    $cargs = @()
    if ($ag.args) { $cargs += $ag.args }
    if ($ag.prompt) { $cargs += $ag.prompt }
    & claude @cargs
  }
  else {
    # sdk / custom: run the given command line
    Invoke-Expression $ag.cmd
  }
}
finally {
  # Whatever happened (normal exit, Ctrl-C, crash) — mark the agent offline.
  node $Reporter --state offline --title "process exited ($LASTEXITCODE)" --source claude-code | Out-Null
}
