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
```

If you touch `TextFormatter` or `EnglishVariant`, add a vector to the arrays in
`App.RunSelfTest` and make sure the run still reports `SELFTEST OK`.

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
