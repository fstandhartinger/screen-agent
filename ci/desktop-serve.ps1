$ErrorActionPreference = 'Stop'
$probe = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$probe.Start()
$port = $probe.LocalEndpoint.Port
$probe.Stop()
$base = "http://127.0.0.1:$port"
$oldToken = $env:SA_SERVE_TOKEN
$env:SA_SERVE_TOKEN = 'desktop-ci-local-token'
$headers = @{ Authorization = "Bearer $env:SA_SERVE_TOKEN" }
$p = $null
foreach ($badToken in @('', 'short')) {
  $env:SA_SERVE_TOKEN = $badToken
  $refused = Start-Process ./publish/ScreenAgent.exe -ArgumentList @('--computer-serve', $port) -PassThru -NoNewWindow -RedirectStandardError artifacts/desktop/serve-refused.log
  if (!$refused.WaitForExit(5000)) { $refused.Kill($true); throw 'server started without a strong token' }
  if ($refused.ExitCode -eq 0) { throw 'missing/short token returned success' }
  if (!(Get-Content artifacts/desktop/serve-refused.log -Raw).Contains('SA_SERVE_TOKEN')) { throw 'missing clear token error' }
}
$env:SA_SERVE_TOKEN = 'desktop-ci-local-token' 
try {
  $p = Start-Process ./publish/ScreenAgent.exe -ArgumentList @('--computer-serve', $port) -PassThru -NoNewWindow -RedirectStandardOutput artifacts/desktop/serve.log
  for ($i = 0; $i -lt 100; $i++) {
    try { $health = Invoke-RestMethod "$base/health" -Headers $headers -TimeoutSec 1; break } catch { Start-Sleep -Milliseconds 100 }
  }
  if (!$health.ok) { throw 'debug server did not become healthy' }
  $denied = Invoke-WebRequest "$base/health" -SkipHttpErrorCheck
  if ($denied.StatusCode -ne 401) { throw 'missing token was accepted' }
  $wrong = Invoke-WebRequest "$base/health" -Headers @{ Authorization = 'Bearer wrong-token-at-least-sixteen' } -SkipHttpErrorCheck
  if ($wrong.StatusCode -ne 401) { throw 'wrong token was accepted' }
  $simple = Invoke-WebRequest "$base/start" -Method Post -Headers $headers -ContentType text/plain -Body '{}' -SkipHttpErrorCheck
  if ($simple.StatusCode -ne 415) { throw 'simple cross-origin content type was accepted' }
  $start = Invoke-RestMethod "$base/start" -Method Post -Headers $headers -ContentType application/json -Body '{"goal":"Desktop transport test"}'
  if (!$start.result.ok -or !$start.run) { throw "start failed: $($start | ConvertTo-Json -Depth 10)" }
  $shot = Invoke-RestMethod "$base/act" -Method Post -Headers $headers -ContentType application/json -Body '{"tool":"screenshot","args":{}}'
  if (!$shot.ok -or $shot.code -ne 'done' -or !$shot.result.jpeg -or !$shot.result.text.StartsWith('frame: screen')) { throw 'invalid screenshot result envelope' }
  $command = Invoke-RestMethod "$base/act" -Method Post -Headers $headers -ContentType application/json -Body '{"tool":"run_cmd","args":{"command":"echo debug-transport"}}'
  if (!$command.ok -or !$command.result.text.Contains('debug-transport')) { throw 'command through server failed' }
  $stop = Invoke-RestMethod "$base/stop" -Method Post -Headers $headers -ContentType application/json
  if (!$stop.ok -or !$p.WaitForExit(5000)) { throw 'server did not stop' }
  if ($p.ExitCode -ne 0) { throw "server exited $($p.ExitCode)" }
  @{ok=$true; run=$start.run; screenshot_chars=$shot.result.jpeg.Length; auth='401 without token'; exit=$p.ExitCode} | ConvertTo-Json | Set-Content artifacts/desktop/serve-result.json
} finally {
  if ($p -and !$p.HasExited) { $p.Kill($true) }
  $env:SA_SERVE_TOKEN = $oldToken
}
