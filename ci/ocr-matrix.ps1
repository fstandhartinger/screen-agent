foreach ($passes in @('3:110','4:110','3:80','3:150','3:110,4:80')) {
  $env:SCREENAGENT_OCR_PASSES=$passes
  Write-Host "=== $passes"
  ./ci/ocr-screens.ps1 -MaxMissing 99
}
