$env:SCREENAGENT_OCR_DEBUG='1'
foreach ($passes in @('3:48','3:110','2:48','4:48','2:110','4:110','3:32','3:48,3:110','2:64,4:64')) {
  $env:SCREENAGENT_OCR_PASSES=$passes
  Write-Host "=== $passes"
  ./ci/ocr-screens.ps1 -MaxMissing 99
}
