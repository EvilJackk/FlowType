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

### The one thing that actually removes the warnings

**Authenticode code signing.** An OV certificate (~$200–400/yr from Sectigo,
DigiCert, SSL.com) builds publisher reputation over time; an **EV certificate**
gets SmartScreen trust essentially immediately. Once you have one:

```powershell
pwsh build\publish.ps1 -Sign -Thumbprint <your-cert-thumbprint>
```

Self-signed certificates do **not** help — Windows doesn't trust them, and they
can look worse than no signature at all.

### If a scanner flags it anyway

Submit it as a false positive; vendors turn these around quickly and it fixes
it for everyone:

- Microsoft Defender — <https://www.microsoft.com/en-us/wdsi/filesubmission>
- Malwarebytes — <https://www.malwarebytes.com/support> (False Positive form)
- VirusTotal — note that a handful of red flags among 70+ engines is normal for
  unsigned indie software; look at *which* engines and whether the label is a
  generic heuristic like `Trojan.Generic` / `ML.Attribute.HighConfidence`.

Please don't ask for detection *evasion* techniques (obfuscation, entropy
padding, anti-analysis). They don't make software safer, they're what actual
malware does, and they make reputation problems worse. Signing and transparency
are the real fix.

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
