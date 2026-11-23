# Print TaskHub events as they happen (PowerShell 7+).
#   .\events.ps1                      # everything
#   .\events.ps1 -Topics "task.*,reminder.due"
param([string]$Topics = "*")

$root = if ($env:TASKHUB_HOME) { $env:TASKHUB_HOME } else { Join-Path $env:LOCALAPPDATA "TaskHub" }
$d = Get-Content (Join-Path $root "api.json") -Raw | ConvertFrom-Json
$token = (Get-Content $d.tokenFile -Raw).Trim()

$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.Options.SetRequestHeader("Authorization", "Bearer $token")
$ws.Options.SetRequestHeader("X-TaskHub-Client", "events-ps1")
$ws.ConnectAsync([Uri]"ws://127.0.0.1:$($d.port)/v1/events?topics=$Topics", [Threading.CancellationToken]::None).Wait()
Write-Host "Connected. Ctrl+C to stop."

$buffer = [byte[]]::new(65536)
while ($ws.State -eq 'Open') {
    $text = ""
    do {
        $r = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $text += [Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count)
    } while (-not $r.EndOfMessage)
    if ($r.MessageType -eq 'Close') { break }
    $e = $text | ConvertFrom-Json
    "{0}  {1,-24} {2,-12} {3}" -f ([datetime]$e.time).ToLocalTime().ToString("HH:mm:ss"), $e.type, $e.source, $e.data.task.title
}
