# End-to-end run
`e2e-live.yml` runs the real exe on a Windows runner against a Desk you start yourself, with a device token in repository secrets only for the run:
`E2E_DESK_URL`, `E2E_DEVICE_TOKEN` ({"token","deviceId","expiresAt"}). Judge the result from the session trace (`desk-trace last`). Remove both secrets and the device afterwards.

`e2e-actions.yml` (push to a branch `e2eact/**`) does the same for the voice action tools: real VS Code Insiders with the real extension behind a browser, the recorded microphone in `testdata/e2e-actions.wav` says a Main Agent sentence, a Claude Code sentence and asks for a drawing. The Desk needs a stub or test bridge for `job.start`.
