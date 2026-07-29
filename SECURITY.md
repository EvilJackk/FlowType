# Security, privacy, and antivirus notes

## What FlowType does on your machine

| Capability | Why it exists | Scope |
| ---------- | ------------- | ----- |
| Global keyboard hook (`WH_KEYBOARD_LL`) | Detect the push-to-talk hotkey anywhere | Compares each event against your one configured hotkey; **key events are never stored, logged, or transmitted** |
| Synthetic input (`SendInput`) | Paste/type the transcript into the focused app | Only fires right after a dictation you started |
| Microphone capture | The actual dictation | Only between hotkey press and release; written to a temp WAV that is deleted immediately after transcription |
| Clipboard read/write | Paste insertion + restoring what you had | Reads only to restore your previous contents |
| Foreground process name | The "top apps" stat | Process name only — never window titles or contents |
| Network | Downloading AI models | HTTPS to `huggingface.co` only, on your explicit click. **Nothing else — no telemetry, no analytics, no accounts** |

Everything else stays on disk in `%APPDATA%\FlowType`. Speech recognition runs
locally via whisper.cpp. Turn off **Settings → Privacy → Save transcripts** and
nothing you dictate is written to disk at all.

## Why antivirus tools may flag it (and what we do about it)

A dictation app is, structurally, doing the same three things a keylogger does:
install a global keyboard hook, synthesize keystrokes, and record audio. No
amount of engineering changes that shape — so heuristic scanners and
SmartScreen may warn, especially for a new, low-reputation binary.

The legitimate answers are transparency and provenance, and FlowType applies
the ones it can:

- **No packing, compression, or trimming.** Published as an ordinary folder of
  ordinary assemblies (`PublishSingleFile=false`, `EnableCompressionInSingleFile=false`,
  `PublishTrimmed=false`). Self-extracting single-file bundles are the same
  shape as packed malware and are a leading cause of false positives.
- **Complete, honest metadata.** Real product name, version, description,
  company, and copyright are compiled into the binary. Blank metadata is itself
  a heuristic signal.
- **`asInvoker` manifest.** FlowType never requests elevation, and never needs it.
- **Full source, reproducible build.** Anyone can read every line and rebuild
  with `dotnet publish`.
- **Narrow behavior.** No persistence beyond an optional Run-key entry you
  control in Settings, no process injection, no network beyond model downloads.

### Why Windows still shows a warning

Releases are not yet Authenticode code-signed, so SmartScreen displays an
"unknown publisher" prompt the first time you run FlowType. Code signing is the
only thing that removes that prompt; until then, the full source, the build
script, and this document are the transparency offered in its place. You can
also build your own copy from source in one command and skip the download
entirely.

What FlowType will never do to look more trustworthy: obfuscation, entropy
padding, or anything else designed to slip past scanners. Those are what actual
malware does, and they make the situation worse rather than better.

### If a scanner flags it

Reporting it as a false positive helps everyone — vendors usually turn these
around quickly:

- Microsoft Defender — <https://www.microsoft.com/en-us/wdsi/filesubmission>
- Malwarebytes — <https://www.malwarebytes.com/support> (False Positive form)

On VirusTotal, a handful of hits among 70+ engines is normal for unsigned indie
software. It's worth checking *which* engines flagged it and whether the label
is a generic heuristic such as `Trojan.Generic` or
`ML.Attribute.HighConfidence` rather than a specific, named threat.

## Reporting a vulnerability

Open an issue describing the problem and how to reproduce it. There is no
server component, so the realistic attack surface is: the model download path,
the JSON files in `%APPDATA%\FlowType`, and anything that could cause the
transcript to be inserted somewhere unintended.

### Hardening already in place

- Model downloads are HTTPS-only, size-checked, and validated for the ggml
  magic header before being loaded, so a captive-portal page or truncated
  transfer can't be handed to the inference engine.
- Downloads land in a `.partial` file and are only swapped in after validation.
- Settings/notes/dictionary/stats are written atomically (temp file → flush →
  `File.Replace` with a `.bak`), so a crash or power loss can't corrupt them; a
  damaged file automatically falls back to the backup.
- Model ids come from a fixed catalog and are never used to build paths from
  user input.
- FlowType's own synthetic keystrokes are tagged with a private
  `dwExtraInfo` signature so they can't feed back into hotkey detection.
