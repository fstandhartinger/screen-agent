# Screen Agent: Windows risk spike

Private, unsigned .NET 8 WPF prototype for Windows 10 2004+ / Windows 11 x64.
**Review build: not ready for everyday use.** No device pairing, audio upload, dictation endpoint, correction learner or screenshot upload is enabled.

Download `ScreenAgent-win-x64-spike` from the Windows workflow on branch `spike`; extract the exe **and `tessdata` beside it**. Run `ScreenAgent.exe`. Windows may require approval for the unsigned app. WebView2 Evergreen Runtime and the Visual C++ 2015–2022 x64 runtime (Tesseract) are prerequisites. The .NET runtime is bundled. Build/publish happens only on Windows CI.

The Desk opens at `https://desk.app.mintapis.com/?companion=1`. Sign in on the Desk's own page; no native cookie scraping or embedded login secrets. Its WebView2 profile is stored locally. Device-token pairing and DPAPI storage come next. The window is docked to the right, topmost, no-activate until clicked; use native minimize to collapse it. Full pill collapse is pending.

## Hotkeys

- Ctrl+Win+Shift, then release all three without another key: toggle screen session.
- Ctrl+Win held for 250 ms without Shift: dictation start/stop **events only**. Adding Shift discards. Quit Wispr or rebind it before using this chord. No microphone audio is recorded by the native spike.
- Caps Lock outside a session: normal behavior. Inside: tap under 200 ms toggles Caps normally; hold at least 200 ms draws. Release composites magenta 5 px strokes onto a clean masked bitmap in memory, then discards it. No upload.
- Ctrl+Win+Shift+P, or the separate Pause button: pause capture. The pill is click-through; the adjacent Pause control is interactive.

Modifiers pass through; the Win chord uses dummy VK 0xE8 while Win is down to suppress Start. The low-level hook has its own message thread, a queue consumer, and a 30 s reinstall watchdog. The callback does bounded native mask/swallow work only; no UI, OCR, UIA or network. Caps baseline is restored on normal stop and managed crash handlers; force-kill/power-loss cannot run a handler. Both left/right modifier variants are recognized; simultaneous left+right versions of one modifier still need refinement.

## Privacy

Captures start only in an active, unpaused session, every four seconds on the cursor's monitor. EnumWindows enumerates visible, uncloaked intersecting windows in z-order; process, title and browser address-bar UIA rules decide which rectangles to black-fill **before OCR**. Unreadable browser URLs are blocked. A blocked/missing foreground, inaccessible input desktop or known shell/task/lock/UAC host skips the frame. Window order, geometry, title, URL and foreground are rechecked after capture; any change discards it. Own overlays are excluded with WDA_EXCLUDEFROMCAPTURE, and drawing remembers the underlying window.

Defaults cover common German banks, neobanks, brokers, exchanges, password managers, Google account/password pages and the Mint Earnings password route. This is **best effort, not a complete privacy boundary**: unknown institutions, custom browser UI, notifications inside another window, transitions and secure-desktop edge cases require Windows manual review. Additions load from `%APPDATA%\ScreenAgent\config.json`: `BlockProcesses` (prefixes), `BlockDomains` (host names), `BlockTitlePatterns` (regular expressions), all arrays; defaults cannot be removed. Invalid config stops startup. OCR text and frames stay in memory. No screenshot retention or local OCR log by default. OCR is local Windows.Media.Ocr first in the file selftest; local Tesseract eng+deu fallback. Live capture currently uses Tesseract in memory. No ids leave the machine in this spike because /exists confirmation is not wired.

Only browsing/signing into the Desk contacts its host. Desk voice features, if explicitly used there, use its chosen voice provider. Future screen vision would use Google; future dictation ensemble uses Groq/OpenAI/Google. Native capture does not send to any processor yet. A page protocol message requests Desk mic muting on dictation events, but acknowledgement and real audio gating are still pending.

## Verification

`ScreenAgent.exe --selftest-ocr fixture.png` prints JSON with engine, OCR text, matched ids, and any WinRT failure; fixture text is synthetic. Windows CI renders 24/36/48 px fixtures and asserts both ids through the published unpackaged exe. Shared registry v2 vectors are copied byte-for-byte into testdata and exercised through xUnit, alongside hotkey threshold and privacy rules.

`--selftest-inject` launches an owned empty Notepad, binds its HWND/UIA editor, pastes `Jev und JevBench`, reads ValuePattern/TextPattern, sends a second phrase via SendInput Unicode, reads it back and asserts exact contents. Clipboard paste is marked excluded from history and restored after 400 ms only if still ours. Before insertion it waits for modifiers up and rechecks focused HWND/element, IsPassword, elevation and privacy; unsafe targets receive only a local clipboard fallback. Newlines become spaces and controls are removed; Enter is never injected. Exit 2 means runner lacks an interactive Notepad editor; CI records the skip rather than claiming the injection passed. Exit 1 is a real failure.

`--selftest-ui` verifies a pill HWND accepts WDA_EXCLUDEFROMCAPTURE. SHA256 and measured exe/model byte sizes are stored in the artifact manifest. Tesseract fast models are pinned to upstream `4.1.0` (Apache-2.0); Tesseract wrapper 5.2.0 (Apache-2.0), WebView2 SDK 1.0.2903.40 (Microsoft license). No trimming. Native OCR libraries are embedded and extracted with SHA256 comparison into `%LOCALAPPDATA%\ScreenAgent\native\tesseract-5.2.0\x64`, with an explicit Tesseract search path because its loader otherwise assumes a non-single-file assembly location. This extraction path is source-reviewed but remains unproven until CI can execute.

Manual Windows release gates: all modifier-order permutations and Start-menu masking; Caps repeat/tap/hold and baseline recovery; watchdog; negative-origin mixed-DPI monitors and draw capture exclusion; background blocked-window black fill and URL races; lock/UAC/Alt-Tab/toasts; Notepad and Windows Terminal injection/read-back including elevated/password targets, clipboard interference; Desk login, WebView2 mic permission, focus preservation and acknowledged mic mute. There is no correction-learning or terminal capability probe yet. Do not treat a green workflow as these manual gates passing.

Next: validate device pairing, add dictation ensemble endpoint and consent/target safety integration, confirmed-id bridge protocol, then correction-learning (two sightings, letters-only 1–3 word pairs), crop/context upload and draw fade/debounce.

## Current CI blocker

The initial Windows run did **not start**: GitHub's check annotation says “The job was not started because an Actions budget is preventing further use.” No build, xUnit run, executable, selftest output, size or SHA256 has been produced yet. The repository remains private; billing and visibility were not changed. Resume the Windows workflow on `spike` once the account budget permits.
