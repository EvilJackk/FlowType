<div align="center">

# FlowType

**Speak anywhere. Local AI voice typing for Windows.**

[![Download](https://img.shields.io/badge/Download-Windows%20x64-111111)](../../releases/latest)
[![License](https://img.shields.io/badge/License-MIT-555555)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-555555)](https://dotnet.microsoft.com/)
[![Offline](https://img.shields.io/badge/100%25-Offline-111111)](SECURITY.md)

*Created by EvilJackk*

</div>

---

FlowType is an open Wispr-Flow-style dictation app: hold a key, talk, release —
clean, formatted text appears wherever your cursor is, in any app. Everything
runs **100% locally** (OpenAI Whisper via [Whisper.net](https://github.com/sandrohanea/whisper.net)/whisper.cpp).
No audio, no text, no telemetry ever leaves your PC.

---

## Install

1. Download `FlowType-<version>-win-x64.zip` from
   **[Releases](../../releases/latest)**
2. Unzip it anywhere (e.g. `C:\Programs\FlowType`)
3. Run **`FlowType.exe`**

No installer, no compiling, no .NET download — everything is bundled. Tick
*Launch at sign-in* in Settings if you want it always running.

A 4-step wizard gets you going: microphone → AI model → hotkey → live practice.

> First launch may show a SmartScreen warning ("unknown publisher"). That's
> expected for unsigned indie software — see [SECURITY.md](SECURITY.md) for
> exactly what FlowType does and why, and what removes the warning.

## Using it

| Action | How |
| ------ | --- |
| Dictate | **Hold** your hotkey, talk, release |
| Hands-free | **Quick-tap** the hotkey; tap again to finish |
| Cancel | **Esc** — or press any other key while holding |
| Toggle by mouse | Click the flow bar at the bottom of your screen |
| Change hotkey | Settings → Dictation → **Detect key…**, then press it |

Say **"scratch that"** to drop everything before it, **"new line"** /
**"new paragraph"** for breaks. Filler words (um, uh) are removed automatically.

## Features

- **Wispr-style activation** — hold-to-talk, quick-tap for hands-free, Esc to
  cancel. Modifier hotkeys (Ctrl) wait a beat so Ctrl+C never opens the mic.
- **Detect key** — binds to whatever your keyboard *actually* sends, which
  matters if vendor software (Logitech G HUB, Razer Synapse) or a remapper is
  in the path. Mouse side-buttons work too.
- **The flow bar** — a slim pill hugging the taskbar: quiet when idle, live
  waveform + timer while listening, "✓ 42 words" when done. Never taller than
  a line of text, so it stays out of chat boxes. Position it bottom, top, or
  hidden.
- **British or American English** — converts spelling both ways (colour/color,
  realise/realize, centre/center, travelled/traveled) *and* biases recognition
  toward that vocabulary. Great for mixed UK/US teams.
- **Local AI formatting** — filler removal, "scratch that" corrections, line
  commands, optional spoken punctuation, no stray period after emails/URLs.
- **Built for unclear speech** — beam-search decoding with temperature
  fallback, audio conditioning (high-pass, gain lift for quiet takes, padding
  so sub-second utterances aren't dropped) and a short capture tail after you
  release the key so the last word is never clipped.
- **GPU accelerated** — Vulkan runtime with automatic CPU fallback. On a
  dedicated GPU the full Turbo model runs faster than Small does on a CPU.
- **In-app model downloads** — Tiny (75 MB) → Large (1.1 GB), from Hugging Face.
- **Dictionary** — teach names/terms (biases recognition) or create
  rewrites and snippets ("my email" → your address).
- **Notes & stats** — searchable history with text export, words dictated,
  speaking pace, day streak, top apps.
- **Privacy controls** — turn off transcript saving entirely; pause dictation
  with one checkbox for games or screen shares.

## Accuracy: mumbling, accents, quiet rooms

Two things matter far more than any other setting:

1. **Model size.** `Small` handles clear speech well; `Turbo · Full` (1.6 GB)
   is in a different league on quiet, rushed or mumbled speech and on strong
   accents, and on any dedicated GPU it is still faster than Small on a CPU.
   FlowType recommends it automatically when it sees a discrete GPU.
   `Large · Maximum` squeezes out a little more on the hardest audio at
   several times the latency.
2. **Accurate decoding** (Settings → AI model, on by default) — beam search
   instead of the first guess. Free on a GPU, ~1.5–2× slower on CPU.

Add recurring proper nouns to the Dictionary — vocabulary entries are fed to
the recognizer and noticeably improve names it keeps mangling.

To measure instead of guess: `FlowType.exe --bench <folder>` transcribes every
`.wav` (16 kHz mono) that has a sibling `.txt` reference with each downloaded
model in both decoding modes and writes word-error rates and timings to
`%APPDATA%\FlowType\bench.txt`.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0):

```bash
dotnet run --project src/FlowType -c Release
```

Package a release (self-contained folder + zip):

```bash
pwsh build/publish.ps1
```

## Troubleshooting

- **Hotkey does nothing** — open Settings → Dictation and watch the live
  indicator: press your key and see whether "hotkey HELD" lights up and what
  "last key seen" reports. Wrong name = your keyboard is remapped; nothing at
  all = its software is swallowing the key. Either way, **Detect key…** (or a
  mouse button) fixes it. For a raw trace: `FlowType.exe --keylog`, press keys
  for 20 s, read `%APPDATA%\FlowType\keylog.txt`.
- **Works everywhere except one window** — that app is running as
  administrator; Windows blocks input from non-elevated apps. Run FlowType as
  admin too, or use the flow bar there.
- **Slow transcription** — Settings → AI model shows `GPU (Vulkan)` or `CPU`.
  On CPU, prefer Base or Small, and try turning off *Accurate decoding*.
- **Nothing inserted in a specific app** — Settings → Output → *Type it out*.
- **Verify your install** — `FlowType.exe --selftest` writes a full pipeline
  report to `%APPDATA%\FlowType\selftest.txt`.

## Data

Everything lives in `%APPDATA%\FlowType`: `settings.json`, `notes.json`,
`dictionary.json`, `stats.json`, `models\ggml-*.bin`. Recordings are temporary
WAVs deleted immediately after transcription.

## Project layout

```
src/FlowType/
├── App.xaml(.cs)      # tray, wiring, onboarding gate, --selftest / --keylog
├── Core/              # settings, paths, atomic JSON, Win32 interop, injection guard
├── Audio/             # 16 kHz recorder + mic monitor, sound cues
├── Engine/            # model catalog/downloader, whisper transcriber (GPU→CPU),
│                      # text formatter, British/American English
├── Input/             # hotkey manager (hook + polling watchdog), text injector
├── Session/           # dictation state machine, notes/dictionary/stats stores
└── UI/                # flow bar, main window, onboarding wizard, theme
build/publish.ps1      # release packaging (+ optional code signing)
```

## Credits

Created by **EvilJackk**.

MIT licensed — see [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Inspired by
[Wispr Flow](https://wisprflow.ai); independent and unaffiliated.
