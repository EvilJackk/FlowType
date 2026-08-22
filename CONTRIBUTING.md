# Contributing to FlowType

Thanks for taking a look. FlowType is a small, focused Windows app — it stays
that way on purpose.

## Getting set up

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/EvilJackk/FlowType.git
cd FlowType
dotnet run --project src/FlowType -c Release
```

Runtime data (settings, models, notes) lives in `%APPDATA%\FlowType` and is
never part of the repo.

## Verifying a change

There is no unit-test project; the app carries its own diagnostics instead.

```bash
# Full pipeline: model load, 2s capture, transcription, formatter + variant
# test vectors. Writes %APPDATA%\FlowType\selftest.txt
FlowType.exe --selftest

# 20 seconds of raw keyboard events with virtual-key codes and injected flags.
# Writes %APPDATA%\FlowType\keylog.txt
FlowType.exe --keylog

# Accuracy + latency: every .wav (16 kHz mono) with a sibling .txt reference in
# the folder, through every downloaded model in both decoding modes. Word error
# rate per model/mode/condition, cold-start and per-clip timings.
# Writes %APPDATA%\FlowType\bench.txt
FlowType.exe --bench C:\path\to\clips

# Every window, page and flow-bar state rendered to PNG off-screen — no
# screenshots, no input, works while a game owns the display.
FlowType.exe --snapshot C:\path\to\out
```

If you touch `TextFormatter` or `EnglishVariant`, add a vector to the arrays in
`App.RunSelfTest` and make sure the run still reports `SELFTEST OK`. If you
touch `Transcriber` or `AudioConditioner`, run `--bench` before and after on
the same clips. The point is the relative change, not the absolute number —
synthetic clips (Windows TTS, degraded) are easier than real mumbling. For
reference, the 1.3.0 baseline on an 80-clip heavily degraded TTS set
(1.1 kHz low-pass "mumble", room reverb, 0 dB noise), RTX 4070 via Vulkan,
beam search on — word error rate on the "mumble" clips: tiny 57 %, small.en
9.5 % (16 % greedy), large-v3-turbo 8.7 %, large-v3-q5_0 3.2 %; average
latency per 4–8 s clip: 0.34 s / 0.50 s / 0.36 s / 0.74 s respectively.

## Things worth knowing before you change input handling

These were each discovered the hard way; the comments in the code say so too.

- **Never filter the keyboard hook on `LLKHF_INJECTED`.** Keyboard vendor
  software and remappers re-inject the user's real keystrokes with that flag.
  FlowType tags its own synthetic input with an `InjectionGuard` signature in
  `dwExtraInfo` and filters on that.
- **The hook callback must return immediately.** Windows silently uninstalls a
  low-level hook that exceeds `LowLevelHooksTimeout` (~300 ms), and every
  keystroke on the system flows through it. The hook runs on a dedicated
  message-pump thread and only posts to the dispatcher.
- **The flow bar is `WS_EX_NOACTIVATE`** so it never steals focus mid-dictation.
  Anything focus-related on that window (menus, popups) needs the temporary
  activation dance in `FlowBarWindow`.

## Style

Match the surrounding code: standard .NET naming, four-space indent, and
comments that explain *why* rather than restate *what*. Prefer a comment on the
non-obvious constraint over a comment on the obvious line.

## Pull requests

Keep them focused, describe what you verified (ideally the `--selftest`
output), and mention any Windows version or keyboard hardware specifics if the
change touches input.

## Releasing

Packaging and signing steps live in [build/RELEASING.md](build/RELEASING.md).
