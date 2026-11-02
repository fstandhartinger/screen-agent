# Real page screenshots (testdata/screens: public explainer, Earnings with its text replaced, real badges) at 100-200 % scaling.
# Every id a human can read on them must come out of the app's OCR path. WinRT OCR only exists on Windows, so this runs on the runner.
. ./ci/invoke-sa.ps1
$expected = Get-Content testdata/screens/expected.json -Raw | ConvertFrom-Json
$failed = 0
foreach ($p in $expected.PSObject.Properties) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $out = Invoke-SA --selftest-memory-ocr "testdata/screens/$($p.Name)"
  $sw.Stop()
  $json = $out | ConvertFrom-Json
  $missing = @($p.Value | Where-Object { $json.Ids -notcontains $_ })
  $extra = @($json.Ids | Where-Object { $p.Value -notcontains $_ })
  Write-Host "$($p.Name): engine=$($json.Engine) found=$($p.Value.Count - $missing.Count)/$($p.Value.Count) ms=$($sw.ElapsedMilliseconds) missing=[$($missing -join ',')] extra=[$($extra -join ',')]"
  if ($env:OCR_TEXT) { Write-Host $json.Text }
  if ($missing.Count) { $failed++ }
}
if ($failed) { throw "$failed screenshot(s) lost ids" }
