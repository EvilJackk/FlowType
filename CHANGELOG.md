# Changelog

All notable changes to FlowType. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow
[SemVer](https://semver.org/).

## [1.1.1] — 2026-07-28

### Fixed
- **Flow bar menu wouldn't close.** The bar is a non-activating window so it
  never steals focus while you dictate — but that also meant its right-click
  menu never received focus, so clicking elsewhere couldn't dismiss it. The bar
  now activates for exactly as long as the menu is open and hands focus back to
  your previous window on close.
- Removed a stale executable left in the pre-`RuntimeIdentifier` output path
  that could be launched by mistake.

### Added
- Tray menu restyled to match the app: dark palette, rounded hover, Windows 11
  rounded corners, no legacy icon gutter.
- Tray menu now shows live status (`Ready · hold <key>`, `Listening…`,
  `Loading model…`, `Paused`) and offers Start/Stop and Pause/Resume.
- Flow bar menu gained **Start/Stop dictating** and **Hide this bar**.

## [1.1.0] — 2026-07-28

### Added
- **British / American English.** Converts spelling in both directions
  (colour↔color, realise↔realize, centre↔center, travelled↔traveled) across the
  regular suffix families plus ~40 irregulars, preserving capitalisation. Also
  biases recognition toward the chosen variant via the model's initial prompt.
- Notes export to a text file.
- Privacy toggle: turn off transcript saving entirely.
- Pause dictation without quitting.
- Flow bar position: bottom, top, or hidden.
- Configurable maximum recording length.
- English variant and Detect-key added to the onboarding wizard.

### Fixed
- **Modifier hotkeys no longer hijack shortcuts.** With a modifier bound (e.g.
  Left Ctrl), every Ctrl+C used to chime and start recording. Modifier hotkeys
  now wait a short grace period, and any other keypress in that window claims
  the gesture as a shortcut.
- Cancelling during transcription could race with disposal of the cancellation
  token.
- Spelling map no longer mis-maps `-ous`/`-ary`/`-ific` derivatives
  (humorous, honorary) that drop the *u* in both dialects.

### Security / hardening
- Settings, notes, dictionary, and stats are written atomically (temp file →
  flush → replace, with a `.bak`), so a crash can't corrupt them; unreadable
  files fall back to the backup automatically.
- Downloaded models are validated against the ggml magic header before being
  loaded, so an error page or truncated transfer can never reach the engine.
- Full version/publisher metadata, `asInvoker` manifest, and a release pipeline
  that avoids single-file packing and compression — see [SECURITY.md](SECURITY.md).

## [1.0.0] — 2026-07-28

Initial release: hold-to-talk and hands-free dictation, the flow bar, local
Whisper transcription with Vulkan GPU acceleration and CPU fallback, in-app
model downloads, dictionary with recognition biasing, notes and stats, and the
first-run onboarding wizard.

### Fixed during 1.0.x
- **Hotkey detected nothing on machines with keyboard software.** The keyboard
  hook discarded any event flagged as *injected* — but Logitech G HUB, Razer
  Synapse, PowerToys, AutoHotkey, RDP and KVMs all re-inject real keystrokes
  that way. FlowType now identifies only its own synthetic input, by a private
  `dwExtraInfo` signature.
- Added a polling watchdog alongside the hook so detection survives a hook
  being dropped by Windows, and works for mouse buttons.
- Added **Detect key…** — binds to whatever virtual-key your hardware actually
  sends — plus a live "hotkey held / last key seen" indicator.
- Non-speech markers such as `[BLANK_AUDIO]` are stripped instead of typed.
