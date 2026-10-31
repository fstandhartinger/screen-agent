# Screen Agent v0.1.1 preview

Unsigned .NET 8 WPF companion for Windows 10 2004+ / Windows 11 x64. This prerelease wires device pairing, confirmed screen IDs, draw highlights, dictation and correction learning to Agent Desk. Windows desktop behavior and the live Desk integration still require the manual checks below. Use a non-sensitive test desktop first.

Download `ScreenAgent.exe` and verify it against `SHA256SUMS`. Optional `tessdata.zip` contains eng+deu models: extract the `tessdata` directory beside the exe. WebView2 Evergreen Runtime is required; the Tesseract fallback also needs the Visual C++ 2015–2022 x64 runtime. The .NET runtime is bundled. The executable is about 210 MB; exact size/hash are measured by Windows CI. No trimming or installer.

On first run the docked Desk window opens `/device` on `https://desk.app.mintapis.com`. Sign in there and connect the device. The native app stores its scoped token with DPAPI CurrentUser at `%APPDATA%\ScreenAgent\device.bin`, then opens `/?companion=1`. Native requests use a bearer token on `/api/device/*`, without cookie scraping, vendor keys or embedded credentials. The page origin is checked before accepting WebView messages; navigation outside the configured origin is blocked. Use the Desk device list to revoke a token server-side. Tray **Gerät trennen** removes the local token and returns to pairing; it does not revoke the server record.

## Controls

- Ctrl+Win+Shift, then release all three without another key: toggle the screen and Desk voice session together. The pill reports missing voice acknowledgement after two seconds.
- Ctrl+Win held at least 250 ms without Shift: dictate; release to transcribe and insert. Adding Shift discards. Quit Wispr or rebind it before using these keys.
- Ctrl+Win+Space: hands-free dictation toggle. Each recording is limited to 60 seconds; longer recordings are refused rather than silently truncated.
- Caps Lock outside a session works normally. In a session, tap under 200 ms toggles normally; hold at least 200 ms draws magenta strokes. Finish after 700 ms idle following a stroke or on Caps release. The app composites strokes onto a privacy-masked capture in memory, sends the bounding crop with 15% margin and a downscaled masked context JPEG, then fades strokes over 1.5 seconds.
- Ctrl+Win+Shift+P, the Pause button or tray **Pause**: pause capture. Tray also provides **Desk anzeigen**, **Gerät trennen**, **Beenden**. Closing or minimizing the Desk hides it to the tray; it never automatically takes focus from an editor.

During a session the pill indicates session, dictation, drawing, privacy refusal and confirmed ID count. Screen capture runs on session start, foreground changes and every four seconds, with perceptual hash deduplication. OCR is local; only registry-confirmed IDs, process class, blocked flag and frame version reach the native screen endpoint. Failed/unknown candidates are never forwarded. Unchanged frames renew the same confirmed IDs every 30 seconds. Frame versions use monotonic Unix milliseconds. Pause, stop and privacy refusal clear Desk context and companion chips. Failed validation clears IDs immediately; network/409 failures retry with exponential backoff capped at 30 seconds and a pill warning.

## Dictation and learning

The native WASAPI recorder produces 16 kHz, 16-bit mono WAV in memory. During a Desk session it requires a mic-mute acknowledgement within 500 ms before recording; after completion/discard it requests acknowledged unmute and retries every two seconds while the pill shows **Desk-Mikro stumm**. Capture is independent of pending transcription; immutable clips are transcribed and inserted in recording order. Dictation sends the process, at most 500 preceding field characters, visible confirmed IDs and dictionary terms. The Desk reconciles Groq/OpenAI/Google transcripts. Google processes highlight images; Desk voice uses the chosen voice provider. No vendor credentials are stored in this app.

Target HWND is captured at chord key-down. UIA binding happens on the UI thread and refuses a changed HWND. Before insertion the app waits for modifiers up and revalidates HWND, UIA element, password status, elevation and privacy. Changed/unsafe targets receive an excluded-from-history clipboard copy and a pill notice. Short text and terminal targets use Unicode SendInput; long non-terminal text uses clipboard paste, restoring the clipboard after 400 ms only if its sequence is still ours. All newlines become spaces and controls are stripped; Enter is never sent. Spoken newline commands are not interpreted in this preview.

Correction learning excludes terminals, password/private targets, unreadable fields and ambiguous insertion spans. It polls the same safe UIA target for at most 45 seconds, requires a stable 1–3-word letters-only replacement, counts at most once per independent insertion, and promotes on the second sighting. Pending pairs survive restarts at `%APPDATA%\ScreenAgent\learn-pending.json`. A failed promotion retains the pair. Open Desk **/device** to inspect the learned-term list and delete a term; verify that it disappears from subsequent dictionary results.

## Privacy and configuration

The privacy filter enumerates visible uncloaked windows and black-fills blocked rectangles before OCR. Process, title and browser address-bar URL rules are additive; unreadable browser URLs fail closed. Blocked foreground, secure desktop, lock/UAC/Alt-Tab/Task View/toast hosts refuse capture. Window order, geometry, title, URL and foreground are checked again after capture. Draw uploads recheck the remembered underlying window and the window beneath every stroke point with overlays hidden. Own windows request WDA_EXCLUDEFROMCAPTURE.

Defaults cover common German banks, neobanks, brokers, exchanges, password managers, Google account/password pages and the Mint Earnings password route. The default **ids-only** domain `bittensor-earnings.lovable.app` permits local OCR and confirmed IDs only. Highlights there send `{ids,bbox,noImage:true}` using OCR boxes intersecting the drawing bounds, with no crop or context image. Its pixels are black-filled in image uploads from other foreground windows. Existing process/title/domain blocks retain full blocking, even on an ids-only domain. This is best effort: unknown institutions, custom browser controls, notifications within other windows and rapid transitions remain manual review gates. Do not treat it as a complete privacy boundary.

`%APPDATA%\ScreenAgent\config.json` is created on first run. Fields: `DeskUrl` (HTTPS origin), `IntervalMs` (1000–60000), `BlockProcesses`, `BlockDomains`, `BlockTitlePatterns`, `IdsOnlyDomains` (additive; includes the Earnings domain), `VoiceProvider`, `Hotkeys`, `RetainScreenshots` (false), `UserEmail` (optional local only). Defaults cannot be removed. Invalid configuration stops startup. The dictation chord can use any two distinct Ctrl/Alt/Win modifiers; session adds Shift, hands-free adds one action key, and pause adds Shift plus one action key. Draw, pause and hands-free action keys must differ and may be Caps, Space, A–Z or F1–F12. Invalid maps fail closed. `VoiceProvider` is passed to the companion page as its voice preference. Screenshot retention is disabled even if requested in config; the preview never writes captures to disk. UserEmail never enters API payloads.

Live and file OCR prefer Windows.Media.Ocr and fall back to bundled Tesseract eng+deu. The memory path never writes a temporary screenshot. Native OCR DLLs are embedded, extracted with SHA256 verification into local AppData, and loaded through an explicit search path. Tesseract 5.2.0 and pinned tessdata_fast 4.1.0 are Apache-2.0; NAudio 2.2.1 is MIT; WebView2 1.0.2903.40 uses Microsoft's license.

## Verification and manual checklist

Windows CI builds, runs pure xUnit tests (shared OCR vectors, hotkeys, privacy, correction diff/promotion/span, payloads, config), publishes a self-contained exe, renders synthetic OCR fixtures at 24/36/48 px, forces the in-memory Tesseract fallback at all three sizes, and runs overlay/insertion selftests. `--selftest-ui` proves that the pill accepts the capture-affinity API, not pixel-level exclusion. `--selftest-inject` skips with documented exit 2 if the hosted runner cannot supply an interactive non-elevated editor. A skip does not prove insertion. Exact output and manifest accompany every CI artifact.

Before daily use, manually check on Windows:

- Pair at `/device`, reconnect after restart, revoke remotely, disconnect locally; reject foreign-origin messages/navigation.
- Toggle Ctrl+Win+Shift, exercise modifier orders, simultaneous left/right modifiers and Start-menu masking; pause and resume from keys and tray; restore Caps baseline on exit.
- Dictate into non-elevated Notepad and Claude Code in Windows Terminal: correct target binding, no Enter, clipboard restore/interference/history exclusion, protected-target refusal, Shift discard, hands-free toggle, 60-second limit, Desk mute acknowledgement/timeouts and unmute.
- Draw with several strokes and idle/release completion; verify crop/context, 1.5-second fade, negative-origin mixed-DPI monitors, overlay exclusion, and changed/private window refusal.
- Recognize IDs at https://bittensor-earnings.lovable.app/reax and confirm they appear in the current Desk session. OCR text must stay local.
- Test privacy blocklist foreground and background masking, browser address changes/unreadable address bars, lock/UAC/Alt-Tab/toasts, and pause/session changes during OCR or upload.
- Correct the same term in two independent readable editor insertions; verify one learned entry. Check ambiguous spans, terminals, digits/email-like corrections and focus changes never learn.
- Validate OCR on the actual screen with local language availability and the optional model folder removed/present. CI exercises synthetic file/memory WinRT and forced Tesseract paths. Force-kill/power-loss cannot run Caps/mic cleanup handlers.

No manual gate is claimed passed by a green workflow. This is an unsigned review prerelease.

## Laptop acceptance record (CoS fills PASS/FAIL)

Installed exe SHA256: **pending**. Windows build: **pending**. Monitor layout/scales: **pending**.
Record the observed result, evidence and latency for each row. A blank result is untested.

| Case | Expected result | PASS/FAIL + evidence |
| --- | --- | --- |
| Rapid second dictation while first transcribes | Second recording starts promptly; both texts insert once, in order; no Enter | |
| Restart after many frames | First new screen frame is accepted promptly, without a 409 catch-up period | |
| Static screen for over 30 minutes | Same confirmed IDs remain in Desk screen context; renewal requests every 30 seconds | |
| Desk outage, then recovery | Pill reports connection failure; retries cap at 30 seconds; current IDs return after recovery | |
| Registry outage, then recovery | Old IDs/chips clear immediately; unknown IDs never forward; current confirmed IDs return | |
| Pause and session stop | Companion chips and server context clear; no new captures while paused/stopped | |
| Voice start/stop and ack failure | Toggle starts/stops Desk microphone forwarding; missing ack appears on pill | |
| Lost unmute acknowledgement | Pill keeps Desk-Mikro stumm until an acknowledged retry succeeds | |
| Learned-term deletion at /device | Learned list shows promoted term; deleting removes it and refreshed dictionary excludes it | |
| Earnings ids-only screen and highlight | Local IDs appear; highlight sends only intersecting confirmed IDs + bbox + noImage; no image bytes leave | |
| Earnings behind another window | Its complete rectangle is black-filled in crop/context image uploads | |
| Blocklisted foreground/background | Foreground refuses capture; background rectangle black-fills before OCR and uploads | |
| Missing process/window metadata | Capture is skipped or unknown rectangle black-fills; no unmasked capture is sent | |

Normal tray **Beenden** requests voice stop and context clear before exit. Force-kill, power loss and permanent network failure cannot guarantee remote cleanup; server expiry remains the fallback.
