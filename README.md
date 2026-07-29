<div align="center">

# FlowType

**Speak anywhere. Local AI voice typing for Windows.**

[![Download](https://img.shields.io/badge/Download-Windows%20x64-7C6CFF)](../../releases/latest)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/)
[![Offline](https://img.shields.io/badge/100%25-Offline-success)](SECURITY.md)

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
- **The flow bar** — quiet pill when idle, live waveform + timer while
  listening, "✓ 42 words" when done. Position it bottom, top, or hidden.
- **British or American English** — converts spelling both ways (colour/color,
  realise/realize, centre/center, travelled/traveled) *and* biases recognition
  toward that vocabulary. Great for mixed UK/US teams.
- **Local AI formatting** — filler removal, "scratch that" corrections, line
  commands, optional spoken punctuation, no stray period after emails/URLs.
- **GPU accelerated** — Vulkan runtime with automatic CPU fallback.
- **In-app model downloads** — Tiny (75 MB) → Turbo (1.6 GB), from Hugging Face.
- **Dictionary** — teach names/terms (biases recognition) or create
  rewrites and snippets ("my email" → your address).
- **Notes & stats** — searchable history with text export, words dictated,
  speaking pace, day streak, top apps.
- **Privacy controls** — turn off transcript saving entirely; pause dictation
  with one checkbox for games or screen shares.

## Strong accents

Accent handling is the acoustic model's job, so if a speaker's accent is being
misheard, **move up a model size** — that helps far more than any setting.
`Small` handles most regional British, Irish, Scottish, Australian, and Indian
English well; `Turbo · Quantized` (574 MB) is better still and stays fast on a
GPU. Add recurring proper nouns to the Dictionary — vocabulary entries are fed
to the recognizer and noticeably improve names it keeps mangling.

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
  On CPU, prefer Base or Small.
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

MIT licensed — see [LICENSE](LICENSE). Inspired by
[Wispr Flow](https://wisprflow.ai); independent and unaffiliated.
