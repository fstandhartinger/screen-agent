# Screen Agent v0.1.6 preview

Unsigned .NET 8 WPF companion for Windows 10 2004+ / Windows 11 x64. This prerelease wires device pairing, confirmed screen IDs, draw highlights, dictation and correction learning to Agent Desk. Windows desktop behavior and the live Desk integration still require the manual checks below.

Download `ScreenAgent.exe` and verify it against `SHA256SUMS`. Optional `tessdata.zip` contains eng+deu models: extract the `tessdata` directory beside the exe. WebView2 Evergreen Runtime is required; the Tesseract fallback also needs the Visual C++ 2015–2022 x64 runtime. The .NET runtime is bundled. The executable is about 210 MB; exact size/hash are measured by Windows CI. No trimming or installer.

## First run, in plain words

1. **Start `ScreenAgent.exe`.** Nothing opens on screen: no console window, no panel. The app lives in the notification area
   (bottom right of the taskbar; on Windows 11 the round icon may be hidden under the `^` arrow, drag it out to keep it visible).
   A balloon says so at start until the device is connected. Starting the exe a second time simply opens the Desk window of the running app.
2. **Connect the device.** Click the tray icon (or right-click, **Desk öffnen**). The Desk window opens as a normal window: it can be
   minimised, resized and moved, it is not always on top, and it remembers its size and position. Click **Im Browser verbinden**.
   Your normal browser opens. If asked, request a Telegram login link, open it, and click **Dieses Gerät verbinden**.
   The app takes over automatically and opens Desk signed in. Do not copy a code or device token. Closing Desk only hides it; the app keeps running.
3. **Start a session.** Press **Ctrl+Win+Shift** (Strg+Win+Shift; any key order, press and release once) or use tray **Sitzung starten**. A dialog started with the purple button in the Desk window also counts as a session.
   A small grey note in the bottom-right corner appears with the state ("Sitzung · 3 IDs"). It never takes focus, cannot be clicked,
   and fades when the mouse comes near. Press the keys again, or use the tray, to stop; the note disappears. The tray icon always shows
   the state: grey ready, green session, blue dictating or drawing, yellow paused, orange needs attention. Hover it for the exact text.
4. **If the note is orange** it tells you what to do. "Nicht verbunden" or "Desk nicht angemeldet" means step 2 is not done.
   "Desk-Stimme startet nicht" means the Desk page did not confirm that voice started within two seconds: open the Desk window from the
   tray, check that you are signed in and allow the microphone when asked.
5. **Quit** from the tray menu (**Beenden**). Voice stops and the screen context is cleared first.

**Wispr keeps working.** Ctrl+Win alone is Wispr's chord and the app does not touch it: it does not swallow it, does not inject a key
and does not react until a third key makes the chord. Only when Shift (or, for dictation, Alt+Win) completes one of our chords does the app send
one invisible neutral key (VK 0xE8) while Win is still held, so that releasing Win does not open the Start menu. Ctrl+Win+Alt (Wispr's command
chord) and every other chord that is not exactly one of ours pass through untouched. (One corner: if you press Alt and Win first and Ctrl only
afterwards, the app has already added its single mask key, because at that moment the chord looked like its own Alt+Win. Press Ctrl first.)
Use the LEFT Alt key: on German keyboards the right Alt is AltGr, which Windows reports as Ctrl+Alt. The app swallows only Caps Lock (while a
session is on, so a Caps tap or hold can mean "draw") and the M of Ctrl+Win+Shift+M (only while a session runs).
**What may still happen with Wispr:** Wispr starts listening on Ctrl+Win, before our Shift arrives. If you press the three keys within about
a tenth of a second Wispr is likely to discard such a short recording; if you hold Ctrl+Win and add Shift late, Wispr may have started and may type
a few words. This cannot be checked without the laptop; see the acceptance table.
If your `config.json` still holds defaults from v0.1.0 to v0.1.3 the app replaces them with the new ones at start (the old file is kept as
`config.json.v0.1.1.bak`) and says so in a balloon.

## Keys (defaults)

| Keys | What it does |
| --- | --- |
| Ctrl+Win+Shift (any order, press once) | Start the voice session (dialog, screen context, frames); press again to end it |
| Ctrl+Win+Shift+M | Mute / unmute the microphone, only while a session runs. The status note turns red ("Mikro stumm"), same state as the Desk dock button |
| Ctrl+Win+Shift+P | Pause / resume capture |
| Ctrl+Win+Shift+D | Show / hide the drawing panel, only while a session runs (the tray has **Zeichnung zeigen** at any time) |
| Alt+Win (hold 250 ms) | Dictate; release to transcribe and insert |
| Alt+Win+Shift (any order, press once) | Dictation on; press again to end it and insert the text |
| Alt+Win+Y | Hands-free dictation toggle (second way to the same) |
| Caps Lock (hold 200 ms, only in a session) | Draw magenta, 5 px strokes; letting go of Caps sends the drawing; tap (under 200 ms) = normal Caps Lock |
| Ctrl+Win alone | Not ours: Wispr Flow |

The tray menu lists every key, and each chord can be changed under `Hotkeys` in `config.json`. **Only the Caps Lock key draws** (it is the `draw` entry; Shift cannot be bound to it: action keys are Caps, Space, A–Z or F1–F12, and the log line `client.app` and the `hotkey.draw.*` events name the key). Shift is only a modifier of the session and dictation chords.

## Controls

- Ctrl+Win+Shift, then release without another key: toggle the screen and Desk voice session together (or tray **Sitzung starten/beenden**). The action happens when the first of the three keys is released. Missing voice acknowledgement after two seconds shows an orange note and tray text with the next step.
- Ctrl+Win+Shift+M while a session runs: mute or unmute the microphone through the Desk page (`window.__deskVoice.toggleMuted()`); no audio leaves the device while muted, the session continues. The small note turns red.
- Alt+Win held at least 250 ms: dictate; release to transcribe and insert. Adding Ctrl discards. Adding Shift latches it (same as Alt+Win+Shift).
- Alt+Win+Shift: dictation toggle. First press starts recording, the second ends it, transcribes and inserts. Each recording is limited to 60 seconds; longer recordings are refused rather than silently truncated.
- Alt+Win+Y: hands-free dictation toggle, as before.
- Caps Lock outside a session works normally. In a session, tap under 200 ms toggles normally; hold at least 200 ms to draw magenta strokes with the mouse, over any window or across several windows. The drawing ends when you release Caps, not before: you hold the key, draw as many strokes as you like, let go. At that instant the drawing layer stops catching the mouse (your next click reaches the window below), the picture is taken without the layer, the strokes are drawn into it in memory (magenta, 5 physical pixels wide whatever the display scaling), and the crop around the strokes (15 % margin) plus the whole screen go to the Desk as the highlight; the strokes fade over 1.5 seconds. Holding Caps without drawing sends nothing. If a highlight cannot be sent the note says why.
- Ctrl+Win+Shift+P or tray **Pause**: pause capture. The tray menu also has **Desk öffnen**, **Gerät trennen**, **Beenden** and shows the current status and the session key. Closing the Desk hides it; minimising puts it on the taskbar like any window. The Desk window opens only on your request (tray click, tray menu, starting the exe again) and never takes focus by itself.

During a session the small note (and the tray tooltip) indicates session, dictation, drawing, a locked screen and the confirmed ID count. OCR is local; registry-confirmed IDs, the foreground process name and the frame version reach the native screen endpoint (a# recognition stays on top).

## Main Agent prompt (v0.1.6)

**What it is.** The voice agent can hand a prepared prompt to this app. (Which spoken words make the agent do so is decided by the Desk, not by this app; this app only reacts to the command.) The app types it into the message box of the Claude Code extension in **VS Code Insiders** (plain "Code" only when no Insiders window exists) and stops there: **it never presses Enter to send**, you read it and send it yourself. The Desk sends the text over the command channel (`…/commands`, the same long poll as "look at the screen"); the app answers `received` at once and `done`/`failed` with a code (`typed`, `typed-unverified`, `no-window`, `no-foreground`, `no-field`, `wrong-focus`, `interrupted`, `busy`, `no-session`), whether it landed and how many characters; the text itself never goes back and is not in the log or the trace.

**What it does, in order.** (1) Looks at the top-level VS Code Insiders windows (most recently used first; a title with `[WSL` goes first, but it is not required). (2) Searches each for the Claude message box through UI Automation (accessible name from `MainAgentInputNames`, about 2.5 s per window); the first window that has one is the one with an open Claude session, otherwise the most recent window is used and the command palette opens the box. (3) Brings that window forward (restores a minimized one and keeps it maximized), without waiting for or touching the mouse; if a modifier key is held it waits up to 2 s and then answers `busy`. (4) Puts the keyboard focus in the box: directly through UI Automation, or with Ctrl+Shift+P, "Claude Code: Focus input", Enter (that Enter is the only unshifted Enter of the feature and is sent only while the palette has the focus). (5) **Checks before the first character**: the focused element belongs to that VS Code process, is named like the message box and has no ancestor whose class contains `monaco-editor`, `xterm` or `terminal`. (6) Types with Unicode key events (umlauts, emoji, `#`), 24 characters at a time; a line break is Shift+Enter; a leading `/` or `@` gets a space in front because the extension would open a menu. Existing draft text stays: it adds a space after it. Before every batch it checks that VS Code is still in front and no key of yours is held; a key press of yours in between ends the typing (`interrupted`) and leaves what was typed. (7) Reads the box back and checks again half a second later that the text is still there.

**What it refuses.** If any check fails nothing is typed and the answer is `failed` with the code; a small topmost window "Nicht eingetippt: <Grund>" shows the text with **Kopieren** and **Schließen** (it never takes the focus and is left out of screen captures). **The clipboard is never used**; only the Kopieren button puts the text there. The text is never typed into an editor buffer or a terminal: the check in (5) is the gate. One prompt at a time (a second one is answered `busy`), a repeated command id is ignored, text is capped at 8000 characters, tabs become two spaces and other control characters are dropped.

**What is proven and what is not.** `ci/main-agent-e2e.ps1` (workflow *VS Code Main Agent prompt*) downloads the real VS Code Insiders archive, installs the real `anthropic.claude-code` extension and runs the feature on a Windows runner: VS Code behind Notepad, minimized (and maximized again), Claude panel closed (palette route), focus in the editor buffer, focus in the integrated terminal, two Insiders windows of which only the older has a Claude session, and the fail-closed case with the fallback window. Every scenario is judged afterwards through UI Automation (the box holds the text, still holds it 3 s later, the clipboard sequence number is unchanged) and leaves a screenshot. **Not proven:** your WSL remote window (the runner has no WSL window; the `[WSL` title is only used for ordering), your extension version (the runner installs the current marketplace version, 2.1.316 at the time of writing), non-English VS Code (the message box name comes from the extension and is configurable), and anything with a screen reader or accessibility flags changed. The test runs elevated; elevated VS Code with a non-elevated Screen Agent cannot be typed into (Windows blocks the keys: `interrupted`).

## Drawing panel (v0.1.6)

A small window "Screen Agent · Zeichnung" (default 440×340, resizable, minimum 320×240, always on top, no taskbar button) shows the board the voice agent draws on (the Desk page `/?panel=canvas`, same sign-in as the Desk window). It opens by itself when the agent has something on the board (config `CanvasPanel`: `"auto"`; `"manual"` = only on request), with Ctrl+Win+Shift+D (session only, like mute), from the tray (**Zeichnung zeigen**) or from the button on the Desk page. Closing it hides it and it **stays closed until you open it or the next voice session starts**; at the end of a session it goes away. It opens without taking the focus. Position: where you moved it last (if that spot is still on a monitor), otherwise bottom right of a monitor that does not hold the window you are working in (else bottom right of the current monitor), above the status note and clear of the taskbar.

The panel is left out of every screen capture (display affinity 0x11), otherwise the agent would see its own drawing. Windows has to confirm that setting when it is read back; if it does not, the panel is never opened (not even by the chord) and the note says so once.

## Screen vision (v0.1.5)

While a voice session runs the app sends the Desk one frame at session start, then every `IntervalMs` (4 s) and at once when the foreground window changes, but only when the picture changed or when the voice agent asks for a fresh look (long poll `…/commands`; a fresh look is always captured and sent at once). "Changed" is a finer test than before: the screen is reduced to a 320×180 grey grid, a pixel counts when it moved by more than 20 levels, and the picture counts as changed when any 20×20 patch of the grid has 8 such pixels. A new 5-digit number in a corner counts, a blinking caret does not. The comparison is always against the last frame the Desk accepted: a frame that was skipped, failed, rejected or rate-limited never becomes the reference. A frame is a JPEG of at most 1600 px width and 300 KB, kept in memory only, never written or logged.

**Which providers receive what.** The frame goes to the Desk gateway, which has Google Gemini describe it (and the marks you draw); Gemini is the only provider that receives images. The voice provider you chose (xAI Grok, OpenAI or Google) receives only the text description and the a# ids, never the image. Nothing is stored: images exist in memory only and are not logged. Screen capture happens only while a session is running and not paused, and the small note shows that.

A locked screen, the sign-in screen and a UAC prompt (the secure desktop) yield no frame; the note says "Bildschirm gesperrt" and the agent is told the screen is locked. Every other skipped frame has a reason in the log (`screen.frame` decision `skipped`, `why`: `locked`, `config-list`, `capture-failed`, `windows-changed`, `rejected`) and is simply tried again at the next tick. The Start menu, Alt-Tab and the like are ordinary windows. In the log, unchanged frames appear as one `deduped` line per run of unchanged frames, then one every 30 seconds.

The a# ids are read in the background (OCR on the native, full-resolution picture) and posted when ready; they never delay the frame. The Desk's vision model also lists the ids it reads on the picture; those the registry confirms are added to the ids shown in the note. A 409 from the Desk ends the frame loop until the next dialog. The Desk does the vision processing (see the gateway).

**What is kept.** Images: nothing, anywhere; they live in memory for one request. The app's own log has no text at all. The Desk session trace (30 days, for debugging) keeps what was said in the session, including numbers the agent reads aloud, plus the start of each screen description and of each marking with digits replaced by `#`.

## Logs

Hotkey events, frame decisions (sent, deduped, skipped and why), OCR timings (`screen.ocr`: ms, engine, counts), highlights (`screen.highlight`: ok, why, ms, bytes), ids seen, app version and session lifecycle go to a small rolling file `%LOCALAPPDATA%\ScreenAgent\logs\screenagent.log` (two files, 2 MB each) and in batches every five seconds to the Desk session trace (`POST /api/device/session/<sid>/trace`, best effort; set `TraceUpload` to false to stop). Neither contains images, OCR text, window titles or transcripts. Failed/unknown candidates are never forwarded. Unchanged frames renew the same confirmed IDs every 30 seconds. Frame versions use monotonic Unix milliseconds. Pause, stop, a locked screen and errors clear Desk context and companion chips (the Desk is told which: `locked`, `paused`, `stop` or `error`). Failed validation clears IDs immediately; network/409 failures retry with exponential backoff capped at 30 seconds and a warning note.

## Dictation and learning

The native WASAPI recorder produces 16 kHz, 16-bit mono WAV in memory. During a Desk session it requires a mic-mute acknowledgement within 500 ms before recording; after completion/discard it requests acknowledged unmute and retries every two seconds while the note shows **Desk-Mikro stumm**. Capture is independent of pending transcription; immutable clips are transcribed and inserted in recording order. Dictation sends the process, at most 500 preceding field characters, visible confirmed IDs and dictionary terms. The Desk reconciles Groq/OpenAI/Google transcripts. Google processes highlight images; Desk voice uses the chosen voice provider. No vendor credentials are stored in this app.

Target HWND is captured at chord key-down. UIA binding happens on the UI thread and refuses a changed HWND. Before insertion the app waits for modifiers up and revalidates HWND, UIA element, password status and elevation. Changed/unsafe targets receive an excluded-from-history clipboard copy and a short note. Short text and terminal targets use Unicode SendInput; long non-terminal text uses clipboard paste, restoring the clipboard after 400 ms only if its sequence is still ours. All newlines become spaces and controls are stripped; Enter is never sent. Spoken newline commands are not interpreted in this preview.

Correction learning excludes terminals, password fields, unreadable fields and ambiguous insertion spans. It polls the same safe UIA target for at most 45 seconds, requires a stable 1–3-word letters-only replacement, counts at most once per independent insertion, and promotes on the second sighting. Pending pairs survive restarts at `%APPDATA%\ScreenAgent\learn-pending.json`. A failed promotion retains the pair. Open Desk **/device** to inspect the learned-term list and delete a term; verify that it disappears from subsequent dictionary results.

## What is captured, and the optional exclusion lists

Nothing is excluded by default: while a session runs, the screen you see is the screen the agent sees, including any window. What is not optional: capture only during an active, unpaused session; the visible note and tray state; images in memory only; the lock screen and secure desktop give no frame; dictation never types into password fields or elevated windows. The app's own windows are left out of every capture (they request WDA_EXCLUDEFROMCAPTURE).

If you want windows left out, put them in `BlockProcesses` (process name prefixes), `BlockDomains` (browser address host names) or `BlockTitlePatterns` (regular expressions on the window title) in `config.json`. All three are **empty by default**; while they are empty the app does not enumerate windows or read browser addresses per frame at all, it only looks at the foreground window's process, title and address for the frame. With entries, a matching foreground window is not captured (the note says "Fenster ausgenommen (Liste)") and matching windows behind it are painted black before anything is sent. An unreadable browser address never matches. Config files from v0.1.2 to v0.1.4 that contain `IdsOnlyDomains` are read without error; the key is ignored.

`%APPDATA%\ScreenAgent\config.json` is created on first run. Fields: `DeskUrl` (HTTPS origin), `IntervalMs` (1000–60000), `BlockProcesses`, `BlockDomains`, `BlockTitlePatterns` (all three empty by default), `VoiceProvider`, `Hotkeys`, `Indicator`, `SendFrames` (true; false = ids only, no images), `TraceUpload` (true), `RetainScreenshots` (false), `UserEmail` (optional local only), `CanvasPanel` (`"auto"` default, or `"manual"`: the drawing panel opens only on request), `MainAgentInputNames` (default `["Message input"]`: the accessible names accepted for the Claude Code message box). `Indicator` is `"session"` (default: the small note while a session runs, plus short notices) or `"notices"` (only short notices; rely on the tray icon). Invalid configuration stops startup. The default chords are `session` Ctrl+Win+Shift, `mute` Ctrl+Win+Shift+M, `pause` Ctrl+Win+Shift+P, `dictation` Alt+Win (hold), `dictationToggle` Alt+Win+Shift, `handsFree` Alt+Win+Y, `draw` Caps, `canvas` Ctrl+Win+Shift+D. `session` and `dictationToggle` are three distinct modifiers (Ctrl, Alt, Win, Shift) and must differ; `dictation` is two of Ctrl/Alt/Win (the app warns at start if you pick Ctrl+Win, which belongs to Wispr); `mute`, `pause`, `canvas` and `handsFree` are two to four modifiers plus one action key. Missing keys take their defaults. Draw, mute, pause, canvas and hands-free action keys must differ and may be Caps, Space, A–Z or F1–F12. Invalid maps fail closed. `VoiceProvider` is passed to the companion page as its voice preference. Screenshot retention is disabled even if requested in config; the preview never writes captures to disk. UserEmail never enters API payloads.

Live and file OCR prefer Windows.Media.Ocr and fall back to bundled Tesseract eng+deu. The memory path never writes a temporary screenshot. Native OCR DLLs are embedded, extracted with SHA256 verification into local AppData, and loaded through an explicit search path. Tesseract 5.2.0 and pinned tessdata_fast 4.1.0 are Apache-2.0; NAudio 2.2.1 is MIT; WebView2 1.0.2903.40 uses Microsoft's license.

## Verification and manual checklist

Windows CI builds, runs pure xUnit tests (shared OCR vectors, hotkeys, exclusion lists, change test and frame planner, correction diff/promotion/span, payloads, config, window placement at 100–200 % scaling, status texts, voice-start logic), publishes a self-contained exe, renders synthetic OCR fixtures at 24/36/48 px, forces the in-memory Tesseract fallback at all three sizes, and runs overlay/insertion selftests. Since 0.1.2 it also checks that the exe is a GUI-subsystem program (no console window), that Ctrl+Win and Ctrl+Win+Alt pass through a real low-level keyboard hook untouched, that Ctrl+Win+Shift starts and ends the session exactly once in all six key orders with one mask key and no Start menu, that Alt+Win+Shift toggles dictation, Alt+Win hold still dictates and Ctrl+Win+Shift+M mutes only inside a session (`--selftest-hotkeys`; GitHub's `windows-latest` has an input desktop and runs it, a runner without one skips with exit 2 and the manual checklist applies), and that the Desk window starts hidden, is not topmost, opens, minimises, remembers its size, and that the status note is small, bottom-right, click-through and hides again (`--selftest-windows`). Since 0.1.3 the browser is hosted directly in the native client rectangle in device pixels, with bounds refreshed on open,
resize, monitor DPI change and rasterization-scale change; there is no temporary 400×300 WPF WebView layout.
Runtimes that cannot initialize an invisible parent get one bounded off-screen/no-activate tool-window show after six
seconds, hidden again when initialization settles (at most fifteen seconds later). This never appears in the taskbar
or on the monitor; opening Desk during startup cancels the off-screen state and keeps the user's ordinary window open.
The default Desk size is 900×800 DIP, clamped to the monitor work area. Windows selftests capture the real native WebView
at 100/125/150% rasterization (`artifacts/desk-controller-100.png`, `-125.png`, `-150.png`) and assert the DOM viewport in
physical pixels equals the entire native client rectangle after resizing and reopening. They also exercise pending and
completed pairing HTTP responses, DPAPI save/restore, a separate WebView cookie store, and credential-free request URLs.
These use synthetic responses/content; the CI monitor's Windows scaling setting is unchanged. Laptop mixed-monitor/DPI
movement and live microphone/provider tests remain required.

`--selftest-draw` draws a circle with real mouse input on the runner's desktop and checks that magenta strokes 5 physical pixels wide are visible, that a clean capture has none, that the crop and context image contain them, that after release the window under a stroke point is no longer ours and that a real click reaches a window beneath (exit 2 = the runner delivers no mouse input). `--selftest-frame <png>` runs the change test and the frame planner on a real bitmap. `--selftest-ui` proves that the status note accepts the capture-affinity API, not pixel-level exclusion. `--selftest-inject` skips with documented exit 2 if the hosted runner cannot supply an interactive non-elevated editor. A skip does not prove insertion. `--selftest-canvas-panel` opens the drawing panel over a synthetic page and checks that the window in front does not change, that Windows reads back the capture exclusion, that a real screen capture (`CopyFromScreen`) does not contain the panel's colour at its rectangle (and does contain it when the exclusion is switched off, as the control), and that close, toggle and a remembered position work. `--selftest-main-agent` is described above. Exact output and manifest accompany every CI artifact.

Before daily use, manually check on Windows:

- Tray → Desk öffnen → Im Browser verbinden → confirm in the normal browser; return automatically signed in, restart without pairing again,
  revoke remotely, disconnect locally; reject foreign-origin messages/navigation. The short-lived browser nonce alone cannot poll or receive credentials;
  the private poll proof stays in request bodies. A second one-use browser approval must reach a randomly addressed
  listener bound only to `127.0.0.1` on this machine before a credential can be delivered; this prevents a remote
  requester from enrolling their machine by sending Florian a pairing link. The listener expires after five minutes,
  closes on completion/disconnect, checks its exact random path and Host, bounds requests to 8 KB/3 seconds, and
  strips the one-use approval from the browser address without reflecting it into the page. Device tokens are stored with current-user DPAPI, and Desk cookies are installed in the app's own store.
- Toggle Ctrl+Win+Shift, exercise modifier orders, simultaneous left/right modifiers and Start-menu masking; check Wispr on Ctrl+Win with and without the app (does it type stray text when Shift follows late?); pause and resume from keys and tray; restore Caps baseline on exit.
- Dictate into non-elevated Notepad and Claude Code in Windows Terminal: correct target binding, no Enter, clipboard restore/interference/history exclusion, password-field and elevated-window refusal, Shift discard, hands-free toggle, 60-second limit, Desk mute acknowledgement/timeouts and unmute.
- Draw with several strokes across two windows and release Caps: one highlight arrives, the agent says what you marked; strokes visible 5 px wide, fade in 1.5 s, the next click reaches the window below; negative-origin mixed-DPI monitors; a tap on Caps still toggles Caps Lock; holding Caps without drawing sends nothing.
- Recognize IDs at https://bittensor-earnings.lovable.app/reax and confirm they appear in the current Desk session. OCR text must stay local.
- Lock the screen (Win+L) during a session: the note says "Bildschirm gesperrt", no frame; unlock: the next frame follows. Start menu and Alt-Tab never stop frames. With entries in the exclusion lists: foreground match is not captured, background match is black.
- Correct a term once in a readable editor insertion; verify one learned entry (config `LearnThreshold`, default 1 as in Florian's spec; 2 = older cautious mode). Check ambiguous spans, terminals, digits/email-like corrections and focus changes never learn.
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
| Desk outage, then recovery | Note reports connection failure; retries cap at 30 seconds; current IDs return after recovery | |
| Registry outage, then recovery | Old IDs/chips clear immediately; unknown IDs never forward; current confirmed IDs return | |
| Wispr still works while Screen Agent runs | Hold Ctrl+Win: Wispr dictates as without the app; Ctrl+Win+Shift toggles the session and Wispr leaves no stray text | |
| Session chord, any order | Ctrl+Win+Shift in all six orders starts, the second press ends; no Start menu opens | |
| Mic mute | Ctrl+Win+Shift+M mutes (note turns red, dock shows Stumm), again unmutes; session and context continue | |
| Screen vision | Ask "was siehst du?": the answer names what is really on screen, whatever window it is (bank, Earnings app included); after you change the screen the answer follows within one interval | |
| Console, Desk window, note | No console window; Desk hidden at start, opens from tray, not always on top, fits without scrolling the frame; note only while session runs |
| Browser pairing + restart | One browser confirmation, no copy/paste; app opens signed in, restart restores connection | |
| DPI and resize | Web content fills the native window at 100/125/150% Windows display scaling; resize and move between monitors without white regions | |
| Pause and session stop | Companion chips and server context clear; no new captures while paused/stopped | |
| Voice start/stop and ack failure | Toggle starts/stops Desk microphone forwarding; missing ack appears as orange note and tray text | |
| Lost unmute acknowledgement | Note keeps Desk-Mikro stumm until an acknowledged retry succeeds | |
| Learned-term deletion at /device | Learned list shows promoted term; deleting removes it and refreshed dictionary excludes it | |
| Earnings app on screen | Its a# ids appear in the note and the agent resolves them and uses their background; the agent sees the page like any other | |
| Highlight across two windows | One highlight with crop and whole-screen context reaches the agent; the agent says what was marked | |
| Exclusion lists empty / with entries | Empty: every window is captured. With an entry: a matching foreground window gives no frame, a matching background window is black | |
| Lock screen | "Bildschirm gesperrt", no frame while locked, frames resume after unlocking | |

Normal tray **Beenden** requests voice stop and context clear before exit. Force-kill, power loss and permanent network failure cannot guarantee remote cleanup; server expiry remains the fallback.
