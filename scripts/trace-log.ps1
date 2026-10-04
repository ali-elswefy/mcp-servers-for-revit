<#
.SYNOPSIS
  Reads the Revit MCP add-in logs: the add-in stream, the MCP protocol stream, or both merged by time.

.DESCRIPTION
  Logs live in <Revit addins>\revit_mcp_plugin\Logs as mcp_addin_yyyyMMdd.log (add-in activity: startup,
  dispatch, execution on the Revit UI thread) and mcp_protocol_yyyyMMdd.log (the wire: connections, requests
  received, responses sent). Both carry the same req=<id> tag, so -RequestId follows one request across both.
  Set the level with "settings.logLevel" in Commands\commandRegistry.json (Debug also records request and
  result bodies) or the REVIT_MCP_LOG_LEVEL environment variable.

.EXAMPLE
  .\trace-log.ps1 -Channel protocol -Last 40
.EXAMPLE
  .\trace-log.ps1 -RequestId 1759574400123456
.EXAMPLE
  .\trace-log.ps1 -Follow -MinLevel Warn
#>
param(
    [ValidateSet('both', 'addin', 'protocol')][string]$Channel = 'both',
    [string]$RequestId,
    [string]$RevitVersion,
    [string]$LogDir,
    [string]$Date = (Get-Date -Format 'yyyyMMdd'),
    [ValidateSet('Debug', 'Info', 'Warn', 'Error')][string]$MinLevel = 'Debug',
    [int]$Last = 0,
    [switch]$Follow
)

$ErrorActionPreference = 'Stop'
$rank = @{ 'DEBUG' = 0; 'INFO' = 1; 'WARN' = 2; 'ERROR' = 3 }
$minRank = $rank[$MinLevel.ToUpper()]
$entryPattern = '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (DEBUG|INFO |WARN |ERROR) '
$idPattern = if ($RequestId) { 'req=' + [regex]::Escape($RequestId) + '(\s|$)' } else { $null }

if (-not $LogDir) {
    $roots = Get-ChildItem (Join-Path $env:APPDATA 'Autodesk\Revit\Addins') -Directory -ErrorAction SilentlyContinue |
        Where-Object { (-not $RevitVersion) -or $_.Name -eq $RevitVersion } |
        ForEach-Object { Join-Path $_.FullName 'revit_mcp_plugin\Logs' } |
        Where-Object { Test-Path $_ }
    $LogDir = $roots | Sort-Object { (Get-Item $_).LastWriteTime } -Descending | Select-Object -First 1
}
if (-not $LogDir -or -not (Test-Path $LogDir)) { throw 'Log folder not found. Pass -LogDir or -RevitVersion.' }

$channels = if ($Channel -eq 'both') { @('addin', 'protocol') } else { @($Channel) }
Write-Host "Logs: $LogDir  (date $Date, channel $Channel, level >= $MinLevel$(if ($RequestId) { ", req $RequestId" }))" -ForegroundColor DarkGray

function Get-Color($level) {
    switch ($level.Trim()) { 'ERROR' { 'Red' } 'WARN' { 'Yellow' } 'DEBUG' { 'DarkGray' } default { 'Gray' } }
}

# Splits text into entries; indented continuation lines (stack traces) stay with their entry.
function ConvertTo-Entries($lines, $channelName) {
    $current = $null
    foreach ($line in $lines) {
        if ($line -match $entryPattern) {
            if ($current) { $current }
            $current = [pscustomobject]@{ Time = $Matches[1]; Level = $Matches[2].Trim(); Channel = $channelName; Text = $line }
        }
        elseif ($current) { $current.Text += "`n" + $line }
    }
    if ($current) { $current }
}

function Test-Wanted($entry) {
    if ($rank[$entry.Level] -lt $minRank) { return $false }
    if ($idPattern -and $entry.Text -notmatch $idPattern) { return $false }
    return $true
}

function Read-Shared($path, $fromByte) {
    $stream = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite,Delete')
    try {
        [void]$stream.Seek($fromByte, 'Begin')
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        $text = $reader.ReadToEnd()
        return @{ Text = $text; Length = $stream.Length }
    }
    finally { $stream.Dispose() }
}

$state = @{}
$entries = foreach ($name in $channels) {
    $path = Join-Path $LogDir "mcp_${name}_$Date.log"
    $state[$name] = @{ Path = $path; Pos = 0; Partial = '' }
    if (Test-Path $path) {
        $read = Read-Shared $path 0
        $state[$name].Pos = $read.Length
        ConvertTo-Entries ($read.Text -split "`r?`n") $name
    }
}

$shown = @($entries | Where-Object { Test-Wanted $_ } | Sort-Object Time)
if ($Last -gt 0) { $shown = $shown | Select-Object -Last $Last }
foreach ($entry in $shown) {
    Write-Host $entry.Text -ForegroundColor (Get-Color $entry.Level)
}
if (-not $shown) { Write-Host '(no matching entries yet)' -ForegroundColor DarkGray }

if ($Follow) {
    Write-Host '--- following (Ctrl+C to stop) ---' -ForegroundColor DarkGray
    $lastWanted = @{}
    while ($true) {
        Start-Sleep -Milliseconds 500
        foreach ($name in $channels) {
            $s = $state[$name]
            if (-not (Test-Path $s.Path)) { continue }
            if ((Get-Item $s.Path).Length -le $s.Pos) { continue }
            $read = Read-Shared $s.Path $s.Pos
            $s.Pos = $s.Pos + [Text.Encoding]::UTF8.GetByteCount($read.Text)
            $text = $s.Partial + $read.Text
            $lines = $text -split "`r?`n"
            $s.Partial = $lines[-1]   # an incomplete last line is kept for the next poll
            foreach ($line in $lines[0..($lines.Length - 2)]) {
                if ($line -match $entryPattern) {
                    $entry = [pscustomobject]@{ Level = $Matches[2].Trim(); Text = $line }
                    $lastWanted[$name] = Test-Wanted $entry
                    if ($lastWanted[$name]) { Write-Host $line -ForegroundColor (Get-Color $entry.Level) }
                }
                elseif ($lastWanted[$name]) { Write-Host $line -ForegroundColor DarkGray }
            }
        }
    }
}
