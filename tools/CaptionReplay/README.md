# CaptionReplay

Replays the live subtitle of a recording offline, on a simulated clock, through the app's own Core logic (`LiveCaption`, `RevisePolicy`, `DictationJoin`) and writes a
**snapshot log**: every change of what the overlay shows (`{t, confirmed, tentative}`) and every paid call (`{t, kind, ms, usd}`). It does not score anything; the scorer
(`score.mjs` of the job's `harness/` folder) turns the logs into error rates, stray punctuation, latency and cost.

```
CaptionReplay <evalDir> --modes v116,revise [--polish http://127.0.0.1:8791] [--revise http://127.0.0.1:8792] [--cases a,b] [--out runs] [--tag rep2]
```

`<evalDir>` holds `cases.json` (`id`, `seconds`, `wav`), `windows/<id>.json` (the app's MAI windows with their real latency and cost) and `final-<gateway>[-<tag>]/<id>.json`
(`main`, `new`, `newg`, `newl`: the dictation chunks `{from, to, ms, text, overlap, cost}` of the production / the new gateway, without or with a Gemini typed-text model; `--tag rep2` reads the
folders of a repeat run). The servers answer the clean-up and re-read calls with the real gateway code (cached); they are not part of this repository. No case data, text or audio belongs in the repository.

| mode | what it replays |
| --- | --- |
| `v116` | windows + live clean-up (`/api/device/polish`, mode live) + chunks: the app of v0.1.16 (`Settle(joined)` without audio end, as production) |
| `v116-nochunk` | the same without the chunk step (a voice session has none) |
| `v116+newg`, `v116+newl` | control: today's logic with the chunk text of the Gemini typed-text runs |
| `revise` | windows + re-read of the last seconds (`/api/device/revise`, recogniser `mai`, the gateway's default model) + chunks `new`; the live clean-up is off; v0.1.19 seam logic (`PauseTracker`, closed / open re-reads) |
| `revise-nochunk` | the same without chunks |
| `revise-gl`, `revise-g38` | the revise server's model is `gemini-3.5-flash-lite` / `gemini-3.8-flash` (Google API, text only) |
| `+newg`, `+newl` suffix | chunks from the `newg` / `newl` run instead of `new` |
| `-hostaudio` suffix | what `CaptionHost.TickRevise` does literally: the audio of every request runs from `FromAudio` to now (also for a closed re-read whose `ToAudio` is the pause) |
| `-f1`, `-f2`, `-f12` | what-ifs of the tool, not product code: f1 = an open answer whose `Shown` no longer stands is dropped; f2 = a closed request sends only the tentative words inside its audio as `Shown` |
| `revise2` | recognisers `mai,openai` (round 1) |
| `old`, `new`, `new-nosettle` | v0.1.15, and v0.1.16 under its former names (`new` = `v116`, `new-nosettle` = `v116-nochunk`) |

The re-read loop is the app's (`CaptionHost.TickRevise`): a 250 ms tick, every 20 ms audio block goes to `PauseTracker.Add(end, 0.02, rms)`, `speaking` = speech (RMS >= 400 per 20 ms block) within
the last 2 s, `RevisePolicy.Next(live, now, audioNow, speaking, pauses, 25)` decides, the audio sent is `[FromAudio, ToAudio]` as designed, `open` = `ReviseRequest.Open`, a request that takes
longer than 6 s counts as failed. Chunks call `Settle(joined, JoinedSeconds, final)` like `DictationManager` (`JoinedSeconds` = sum of chunk length minus its overlap lead-in, `final` = the last chunk).
