# Real page screenshots (testdata/screens: public explainer, Earnings with its text replaced, real badges) at 100-200 % scaling.
# Every id a human can read on them must come out of the app's OCR path. WinRT OCR only exists on Windows, so this runs on the runner.
# Readings that are not in the list stay in `extra`; in the app the registry check drops them.
param([string]$Mode = '--selftest-find-ids', [int]$MaxMissing = 0)
. ./ci/invoke-sa.ps1
$expected = Get-Content testdata/screens/expected.json -Raw | ConvertFrom-Json
$failed = 0
foreach ($p in $expected.PSObject.Properties) {
  $out = Invoke-SA $Mode "testdata/screens/$($p.Name)"
  $json = $out | ConvertFrom-Json
  $missing = @($p.Value | Where-Object { $json.Ids -notcontains $_ })
  $extra = @($json.Ids | Where-Object { $p.Value -notcontains $_ })
  Write-Host "$($p.Name): engine=$($json.Engine) found=$($p.Value.Count - $missing.Count)/$($p.Value.Count) ms=$($json.Ms) missing=[$($missing -join ',')] extra=[$($extra -join ',')]"
  if ($missing.Count -gt $MaxMissing) { $failed++ }
}
if ($failed) { throw "$failed screenshot(s) lost ids" }
