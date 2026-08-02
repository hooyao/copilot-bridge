<#
.SYNOPSIS
  Scans bridge logs for request durations and stream-ending causes.

.DESCRIPTION
  Backs the claims in docs/stream-cap-investigation.md. Answers, from a
  local log corpus, the question that closed PR #54: does Copilot impose a fixed
  ~300s cap on a token-less stream?

  The discriminating evidence is not the maximum duration on its own but the
  DISTRIBUTION OF `premature_eof` DURATIONS. A fixed cap forces those disconnects to
  pile up at one value; ordinary transport failure scatters them. Note the actor
  behind each cut is UNKNOWN (peer, proxy, or network all look alike here) -- the
  argument rests on where they land, never on who caused them.
  So the script reports every disconnect individually rather than summarising them.

  Read-only. Skips any log a running bridge still holds open.

.PARAMETER LogDir
  Directory of bridge-*.log files. Defaults to the desktop install.

.PARAMETER KeepAliveSince
  Timestamp when a keepalive-injecting build was deployed (0.4.24-beta shipped
  2026-07-28 21:13). Splits the report either side of it. Injected pings are
  DOWNSTREAM events the bridge sends and never reset its own stream-idle budget, so
  they cannot extend an upstream stream -- but the split is reported anyway so a
  reader can confirm that rather than take it on trust. Pass a far-future date to
  disable the split.

.EXAMPLE
  pwsh scripts/scan-stream-durations.ps1
  pwsh scripts/scan-stream-durations.ps1 -LogDir C:\path\to\log
#>
[CmdletBinding()]
param(
    [string]   $LogDir         = "$env:USERPROFILE\Desktop\copilot-bridge\log",
    [datetime] $KeepAliveSince = '2026-07-28 21:13:49'
)

if (-not (Test-Path $LogDir)) { throw "Log directory not found: $LogDir" }

$rxDuration = [regex]'duration_ms=(\d+)'
$rxOut      = [regex]'out:(\d+)'

$rows    = New-Object System.Collections.Generic.List[object]
$skipped = New-Object System.Collections.Generic.List[string]

foreach ($file in Get-ChildItem $LogDir -File -Filter 'bridge-*.log') {
    try {
        $lines = [System.IO.File]::ReadLines($file.FullName)
        foreach ($line in $lines) {
            # Only per-request summary lines carry usage= and a duration.
            if ($line -notmatch 'usage=') { continue }
            $m = $rxDuration.Match($line)
            if (-not $m.Success) { continue }

            # Cause is read from the summary line's own error= text. NOTE the bridge
            # writes the phase two different ways: `phase=stream_idle` on the separate
            # WRN line, but `UpstreamTimeoutException: upstream stream_idle timeout` on
            # the summary line matched here — match both or every bridge timeout is
            # silently misfiled as 'other'.
            $cause =
                if     ($line -match 'premature_eof')                            { 'premature_eof' }
                elseif ($line -match 'phase=stream_idle|upstream stream_idle')   { 'bridge_stream_idle' }
                elseif ($line -match 'phase=first_byte|upstream first_byte')     { 'bridge_first_byte' }
                elseif ($line -match 'cancelled by client')                      { 'cancelled_by_client' }
                elseif ($line -match 'net_http_|No such host|ssl_connection')    { 'transport_error' }
                elseif ($line -match 'error=\(none\)')                           { 'clean' }
                else                                                             { 'other' }

            $mo = $rxOut.Match($line)
            $rows.Add([pscustomobject]@{
                File      = $file.Name
                Seconds   = [math]::Round([int]$m.Groups[1].Value / 1000.0, 1)
                Ms        = [int]$m.Groups[1].Value
                Cause     = $cause
                OutTokens = if ($mo.Success) { [int]$mo.Groups[1].Value } else { $null }
                Streaming = if ($line -match 'streaming=(\w+)') { $matches[1] } else { '?' }
                # Attributed by the log file's mtime, not a per-line timestamp: summary
                # lines carry only a time-of-day, so the file is the only date anchor.
                PostKeepAlive = $file.LastWriteTime -gt $KeepAliveSince
            })
        }
    }
    catch [System.IO.IOException] {
        # A running bridge holds its current log open. Reported, never silently dropped.
        $skipped.Add($file.Name)
    }
}

if ($rows.Count -eq 0) { throw "No request-summary lines found under $LogDir" }

Write-Host "corpus: $($rows.Count) request summaries from $LogDir"
if ($skipped.Count) { Write-Host "skipped (file in use): $($skipped -join ', ')" }

Write-Host "`n--- ending cause ---"
$rows | Group-Object Cause | Sort-Object Count -Descending |
    Format-Table @{n='cause';e={$_.Name}}, Count -AutoSize

$clean = $rows | Where-Object { $_.Cause -eq 'clean' -and $_.Streaming -eq 'true' } |
         Sort-Object Seconds
if ($clean.Count) {
    Write-Host "--- clean streaming runs: $($clean.Count) ---"
    'p50 {0,8:N1}s' -f $clean[[int]($clean.Count * 0.50)].Seconds
    'p95 {0,8:N1}s' -f $clean[[int]($clean.Count * 0.95)].Seconds
    'p99 {0,8:N1}s' -f $clean[[int]($clean.Count * 0.99)].Seconds
    'max {0,8:N1}s' -f $clean[-1].Seconds
    $past300 = @($clean | Where-Object Seconds -gt 300)
    # Deliberately NOT captioned as evidence against a token-less cap. Summary lines
    # carry no first-token time, so a run past 300s may simply have started emitting
    # before 300s and continued -- which such a cap permits. Reported as context only;
    # the disconnect distribution below is what actually discriminates.
    Write-Host "clean runs past 300s: $($past300.Count)  (total duration only -- first-token time is NOT in these logs)"
    $past300 | ForEach-Object { '  {0,8:N1}s  out={1}' -f $_.Seconds, $_.OutTokens }
}

# The load-bearing table: where do mid-body disconnects actually land?
$eof = @($rows | Where-Object Cause -eq 'premature_eof' | Sort-Object Seconds)
Write-Host "`n--- mid-body disconnects (premature_eof): $($eof.Count) ---"
if ($eof.Count) {
    $eof | ForEach-Object { '  {0,8:N1}s  out={1,-6} {2}' -f $_.Seconds, $_.OutTokens, $_.File }
    $near300 = @($eof | Where-Object { $_.Seconds -ge 280 -and $_.Seconds -le 330 }).Count
    # This one IS discriminating, and does not need first-token timing: whatever
    # triggers a fixed cap, the disconnects it produces must cluster at the cap value.
    # Scatter across two orders of magnitude is not that.
    Write-Host "  within 280-330s: $near300 of $($eof.Count)  <-- a fixed ~300s cap predicts most of them here"
}

# Millisecond-exact clustering on a round number is a LOCAL timer, not a server.
$round = @($rows | Where-Object { $_.Ms -ge 599000 -and $_.Ms -le 601000 })
if ($round.Count) {
    Write-Host "`n--- runs at ~600s (check for local-timer signature) ---"
    $round | ForEach-Object { '  {0} ms  {1}' -f $_.Ms, $_.Cause }
    Write-Host "  millisecond-tight against a round value = a fixed local timer"
}

# Split either side of keepalive injection, so a reader can check for themselves that
# the headline figures predate it rather than being an artifact of it.
Write-Host "`n--- keepalive split (injection since $($KeepAliveSince.ToString('yyyy-MM-dd HH:mm'))) ---"
foreach ($era in @($false, $true)) {
    $slice = @($rows | Where-Object { $_.PostKeepAlive -eq $era })
    if (-not $slice.Count) { continue }
    $label      = if ($era) { 'post-keepalive' } else { 'pre-keepalive ' }
    $cleanSlice = @($slice | Where-Object { $_.Cause -eq 'clean' -and $_.Streaming -eq 'true' })
    $maxClean   = if ($cleanSlice.Count) { ($cleanSlice | Measure-Object Seconds -Maximum).Maximum } else { 0 }
    $eofSlice   = @($slice | Where-Object Cause -eq 'premature_eof' | Sort-Object Seconds)
    '{0}  summaries={1,-6} clean_max={2,7:N1}s  clean>300s={3,-3} eof={4}' -f `
        $label, $slice.Count, $maxClean,
        @($cleanSlice | Where-Object Seconds -gt 300).Count,
        $(if ($eofSlice.Count) { ($eofSlice | ForEach-Object { '{0:N1}' -f $_.Seconds }) -join ', ' } else { 'none' })
}
