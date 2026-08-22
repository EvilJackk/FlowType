# Changelog

All notable changes to FlowType. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow
[SemVer](https://semver.org/).

## [1.3.0] — 2026-08-22

### Changed — recognition
- **Beam-search decoding** (5 hypotheses, temperature fallback ladder) replaces
  greedy decoding. Settings → AI model → *Accurate decoding*, on by default.
- **Audio conditioning** before the model: 80 Hz high-pass, gain lift for
  quiet takes (capped at +24 dB), lead-in/tail padding. Sub-second utterances
  were silently dropped before (whisper.cpp refuses anything under 1 s); they
  are now padded and transcribed. Near-silent takes are rejected instead of
  being hallucinated into "Thank you."
- **Capture tail**: the mic stays open 280 ms after the hotkey is released, so
  a word released on its last syllable is no longer clipped.
- **Hallucination guard**: on audio it cannot make out, Whisper sometimes
  echoes its own prompt ("The following is American English, and the
  following is…") or loops a phrase. Both are now detected and dropped before
  anything is typed; genuine speech is untouched (covered by selftest vectors).
- **Warm-up after model load**: a short embedded clip runs through the model
  as soon as it loads, so the first real dictation no longer pays the 1–2 s
  GPU kernel/buffer setup.
- Dictionary rewrite targets (e.g. "Arma Reforger") now also bias recognition,
  not only vocabulary-only entries.
- **Model catalog**: *Turbo · Full* is now recommended automatically on
  machines with a dedicated GPU; *Large · Maximum* (large-v3, 1.1 GB) added.
- Flash attention enabled on the GPU path; whisper thread count follows
  physical cores on the CPU path (was capped at 4).
- Whisper.net 1.8.1 → 1.9.1 (newer whisper.cpp / ggml Vulkan kernels).
- New hidden diagnostic: `FlowType.exe --bench <folder>` measures word error
  rate and latency per model and decoding mode; `--selftest` now reports
  which GPU whisper picked and checks the audio conditioner.

### Changed — look
- **Monochrome theme** everywhere: near-black surfaces, white accent, greys —
  the purple is gone from the windows, flow bar, tray menu, chips, and the
  app icon. Check boxes, radio buttons, combo boxes, progress bars and scroll
  bars are now styled to match instead of using the stock Windows look.
- **Slimmer flow bar**: roughly 40 % shorter in every state (≤ 28 px), narrower,
  and moved down to 4 px above the taskbar so it stops covering message boxes
  at the bottom of chat apps.

## [1.2.0] — 2026-07-28

First public release.

### Changed
- Simplified the About panel in Settings.
- Documentation: split third-party attributions into
  [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), added contributor and
  release guides.

## [1.1.1] — 2026-07-28

### Fixed
- **Flow bar menu wouldn't close.** The bar is a non-activating window so it
  never steals focus while you dictate — but that also meant its right-click
  menu never received focus, so clicking elsewhere couldn't dismiss it. The bar
  now activates for exactly as long as the menu is open and hands focus back to
  your previous window on close.

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
