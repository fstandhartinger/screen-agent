# CaptionReplay

Replays the live subtitle of a recording offline, on a simulated clock, through the app's own Core logic (`LiveCaption`, `RevisePolicy`, `DictationJoin`) and writes a
**snapshot log**: every change of what the overlay shows (`{t, confirmed, tentative}`) and every paid call (`{t, kind, ms, usd}`). It does not score anything; the scorer
(`score.mjs` of the job's `harness/` folder) turns the logs into error rates, stray punctuation, latency and cost.

```
CaptionReplay <evalDir> --modes v116,revise [--polish http://127.0.0.1:8791] [--revise http://127.0.0.1:8792] [--cases a,b] [--out runs]
```

`<evalDir>` holds `cases.json` (`id`, `seconds`, `wav`), `windows/<id>.json` (the app's MAI windows with their real latency and cost) and `final-main/<id>.json`,
`final-new/<id>.json` (the dictation chunks of the production / the new gateway). The servers answer the clean-up and re-read calls with the real gateway code (cached);
they are not part of this repository. No case data, text or audio belongs in the repository.

| mode | what it replays |
| --- | --- |
| `v116` | windows + live clean-up (`/api/device/polish`, mode live) + chunks: the app of v0.1.16 |
| `v116-nochunk` | the same without the chunk step (a voice session has none) |
| `revise`, `revise2` | windows + re-read of the last seconds (`/api/device/revise`, recognisers `mai` / `mai,openai`) + chunks; the live clean-up is off |
| `revise-nochunk`, `revise2-nochunk` | the same without chunks |
| `revise-max25[-nochunk]` | what-if: `RevisePolicy.Next` gets `maxSeconds = 25` instead of `min(25, audio so far)` (as `CaptionHost.TickRevise` passes it) |
| `old`, `new`, `new-nosettle` | v0.1.15, and v0.1.16 under its former names (`new` = `v116`, `new-nosettle` = `v116-nochunk`) |

The re-read loop is the app's: a 250 ms tick, `speaking` = speech (RMS >= 400 per 20 ms block) within the last 2 s, `RevisePolicy.Next(...)` decides, the audio sent is
`[FromAudio, now]` of the recording, a request that takes longer than 6 s counts as failed, `open` is always true (as `CaptionHost.Revise` sends it).
