# End to end for the Main Agent prompt: the real VS Code Insiders (portable archive) with the real Claude Code extension, on the runner's interactive desktop.
# Nothing is ever sent to Anthropic: the key is a placeholder (it only makes the extension show its message box without a login) and the test types, never submits.
# Usage (repo root, after `dotnet publish ... -o publish`): ./ci/main-agent-e2e.ps1 [-Only A,B]
param([string]$Root = (Join-Path $PWD 'e2e-vscode'), [string]$Out = (Join-Path $PWD 'artifacts/main-agent'), [string]$Only = '')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Root, $Out, (Join-Path $Root 'work') | Out-Null
$code = Join-Path $Root 'vscode'
if (-not (Test-Path (Join-Path $code 'Code - Insiders.exe'))) {
  $zip = Join-Path $Root 'vscode-insiders.zip'
  Write-Host 'Downloading VS Code Insiders (win32-x64 archive)'
  if ($env:SA_VSCODE_REVISION -and $env:SA_VSCODE_REVISION -notmatch '^(latest|[0-9a-f]{40})$') { throw 'Invalid VS Code revision' }
  $version = if ($env:SA_VSCODE_REVISION -and $env:SA_VSCODE_REVISION -ne 'latest') { 'commit:' + $env:SA_VSCODE_REVISION } else { 'latest' }
  Invoke-WebRequest -Uri "https://update.code.visualstudio.com/$version/win32-x64-archive/insider" -OutFile $zip -UseBasicParsing
  Write-Host ("zip bytes: {0}" -f (Get-Item $zip).Length)
  New-Item -ItemType Directory -Force $code | Out-Null
  & 7z x -y "-o$code" $zip | Out-Null
  if (-not (Test-Path (Join-Path $code 'Code - Insiders.exe'))) { throw 'VS Code Insiders archive did not unpack as expected' }
  Remove-Item $zip
}
# Portable mode: a data folder next to the exe holds settings, extensions and logs.
$user = Join-Path $code 'data/user-data/User'
New-Item -ItemType Directory -Force $user, (Join-Path $code 'data/extensions'), (Join-Path $code 'data/tmp') | Out-Null
@'
{
  "claudeCode.disableLoginPrompt": true,
  "claudeCode.hideOnboarding": true,
  "window.restoreWindows": "none",
  "window.newWindowDimensions": "maximized",
  "workbench.startupEditor": "none",
  "workbench.tips.enabled": false,
  "workbench.enableExperiments": false,
  "security.workspace.trust.enabled": false,
  "update.mode": "none",
  "extensions.autoUpdate": false,
  "extensions.autoCheckUpdates": false,
  "telemetry.telemetryLevel": "off",
  "git.enabled": false,
  "chat.disableAIFeatures": true,
  "terminal.integrated.defaultProfile.windows": "PowerShell"
}
'@ | Set-Content (Join-Path $user 'settings.json') -Encoding UTF8
# Setup-only keys (used by the test to arrange states; the feature under test uses the palette or UIA, never these).
@'
[
  { "key": "ctrl+alt+shift+f9",  "command": "claude-vscode.focus" },
  { "key": "ctrl+alt+shift+f10", "command": "workbench.action.focusActiveEditorGroup" },
  { "key": "ctrl+alt+shift+f11", "command": "workbench.action.terminal.focus" },
  { "key": "ctrl+alt+shift+f12", "command": "workbench.action.closeAuxiliaryBar" }
]
'@ | Set-Content (Join-Path $user 'keybindings.json') -Encoding UTF8
$cli = Join-Path $code 'bin/code-insiders.cmd'
Write-Host 'Installing anthropic.claude-code from the marketplace'
& $cli --install-extension anthropic.claude-code --force 2>&1 | Tee-Object (Join-Path $Out 'install-extension.txt')
& $cli --version 2>&1 | Tee-Object (Join-Path $Out 'vscode-version.txt')
& $cli --list-extensions --show-versions 2>&1 | Tee-Object (Join-Path $Out 'extensions.txt')
if (-not (Select-String -Quiet -Path (Join-Path $Out 'extensions.txt') -Pattern 'anthropic.claude-code@')) { throw 'Claude Code extension is not installed' }
if ($env:SA_E2E_PREPARE_ONLY) { exit 0 }
$env:ANTHROPIC_API_KEY = 'not-a-real-key'
if ($Only) { $env:SA_E2E_ONLY = $Only }
. ./ci/invoke-sa.ps1
try {
  Invoke-SA --selftest-main-agent $code (Join-Path $Root 'work') $Out | Out-Null
  $exit = $LASTEXITCODE
} finally {
  if (Test-Path (Join-Path $code 'data/user-data/logs')) { Copy-Item (Join-Path $code 'data/user-data/logs') (Join-Path $Out 'vscode-logs') -Recurse -Force -ErrorAction SilentlyContinue }
  Get-Process -Name 'Code - Insiders' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
if (Test-Path (Join-Path $Out 'main-agent.log')) { Get-Content (Join-Path $Out 'main-agent.log') | Select-Object -Last 120 }
Write-Host "main-agent selftest exit code: $exit"
exit $exit
