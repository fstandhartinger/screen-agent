# ScreenAgent.exe is a WinExe (no console window). PowerShell neither waits for
# GUI-subsystem processes nor captures their stdout, so selftests run through
# Start-Process with redirected output. Sets $global:LASTEXITCODE like `&` does.
function Invoke-SA {
  $stdout = New-TemporaryFile
  $p = Start-Process -FilePath ./publish/ScreenAgent.exe -ArgumentList $args -Wait -PassThru -NoNewWindow -RedirectStandardOutput $stdout.FullName
  $global:LASTEXITCODE = $p.ExitCode
  Get-Content $stdout.FullName
  Remove-Item $stdout.FullName
}
