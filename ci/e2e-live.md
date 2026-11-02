# End-to-end run
`e2e-live.yml` runs the real exe on a Windows runner against a Desk you start yourself, with a device token in repository secrets only for the run:
`E2E_DESK_URL`, `E2E_DEVICE_TOKEN` ({"token","deviceId","expiresAt"}). Judge the result from the session trace (`desk-trace last`). Remove both secrets and the device afterwards.
