# Changelog

All notable changes to FlowType. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/); versions follow
[SemVer](https://semver.org/).

## [1.5.0] — 2026-09-10

FlowType learns your words from the way you fix them.

### Added
- **Learn from corrections you type** (Settings → Formatting, on by default).
  When FlowType gets a word wrong, backspace over it and type the right one —
  the correction becomes a dictionary entry and the bar says *"Added to
  dictionary: Armor Forger → Arma Reforger"*. It is the same gesture you were
  making anyway, and a real example of how the model mishears you is worth more
  than any rule you could think to write in advance.
  Entries created this way are labelled *(learned from your correction)* in the
  Dictionary, so a wrong one is obvious and one click from gone. Keystrokes are
  only watched for a few seconds after FlowType itself inserts text, in the same
  window, and are never written anywhere — only the dictionary entry is saved.
  Anything that moves the cursor somewhere FlowType cannot follow (an arrow key,
  a mouse click, changing window) cancels the watch rather than guessing.

### Fixed
- **One dictionary entry now covers the whole family of ways a name gets
  misheard.** A rule you wrote is also a *sample of how the model mishears you*,
  and a far better anchor for the next mangling than the correct spelling is —
  so FlowType now matches near-misses of the mis-hearing too. With a single
  "Armor Forger → Arma Reforger" entry, *Armour Forger*, *Arm of Forger*,
  *Armored Forger* and *Armor Forge her* are all now caught, where before only
  the two exact phrases were. This is the reason adding a word to the dictionary
  so often looked like it did nothing.

### Diagnostics
- `FlowType.exe --dictcheck ["phrase" …]` — shows exactly what your dictionary
  is telling the recogniser, the prompt it builds, and what happens to each
  phrase at every stage. With no arguments it invents plausible mis-hearings of
  your own terms and reports how many are caught.

## [1.4.0] — 2026-09-09

Theme of the release: stop the small daily failures — a name spelled wrong, a
paste that lost the race, room noise typed as words, a transcript that never
arrived.

### Fixed
- **Esc now cancels while FlowType is transcribing.** It only ever worked while
  the microphone was still open; the moment processing started, the key did
  nothing. Everything downstream of it — the cancellation the app already had —
  was unreachable code.
- **A stuck transcription can no longer wedge the app.** Decoding now has a
  generous time budget (four times the length of what you said, at least three
  minutes); past that the attempt is abandoned and FlowType goes back to idle
  instead of sitting in "processing" with a dead hotkey until restarted.
- **A formatting error no longer throws your words away.** If anything in the
  formatting chain fails — including a rule you wrote yourself — FlowType now
  inserts what the model heard rather than showing an error. Dictionary rules
  also have a time limit, so no entry can hang a dictation.
- **The microphone stopping mid-sentence keeps what it captured.** Unplugging a
  USB or Bluetooth mic during dictation used to delete the recording; the
  captured audio is now transcribed, with a note that the mic disconnected.
- **Fixed a hang** when the microphone test in Settings was opened in the
  moment a dictation was finishing: the dictation never completed.
- **Bracketed text you actually dictated survives.** "See note [1] and [2]"
  came back as "See note and", and "[sic]" disappeared from quotations —
  FlowType was stripping *any* bracketed text rather than only Whisper's own
  markers.
- **Dictionary rules no longer feed each other.** With "react → React" and
  "React → React.js", "i use react daily" became "i use React.js daily", and
  adding one entry could silently change what another produced. Every rule now
  matches what you actually said; where two overlap, the longer one wins.
- **A rule can now replace something with a space** — "-" → " " to undo
  hyphenation is the obvious one, and it was impossible before.
- Filler removal is no longer applied to languages where those are real words
  ("um" is an article in Portuguese). It follows what the model actually
  decoded rather than the language setting.

### Added
- **Fix near-misses of your dictionary words automatically** (Settings →
  Formatting, on by default). Dictionary entries now correct themselves: FlowType
  compares every one- to three-word run against your terms by spelling *and* by
  sound, so a single "Arma Reforger" entry catches "Armour Forger", "armor
  forger" and "Arma Reforge" without you writing a rule for each. Capitalisation
  and punctuation around the phrase are preserved; email addresses and links are
  never touched.
- **Speech detection before the model.** Every take is measured in 20 ms windows
  before it reaches Whisper. A muted microphone now says so ("No sound from the
  microphone — check it's not muted"), and room tone is reported as "Didn't hear
  any speech" instead of being handed to a model that invents a sentence for it.
- **Quiet speech gets more help.** When a take's loudness swings the way speech
  does, FlowType lifts it up to +30 dB instead of +24. Steady hiss and fan noise
  keep the lower ceiling, so a noisy room is never amplified into a wall of sound
  the model tries to read.
- **Long silences at the end of a take are trimmed** back to a third of a second
  before transcription — mostly a hands-free-session fix, where several seconds
  of quiet used to be fed to the model.
- More filler words removed: `hmm`, `mmm`, `ehm`, `ahm`, `er`, `ah`, `eh` join
  `um`/`uh`/`erm`. Units survive ("5 mm" stays), hyphenated words survive
  ("Ah-ha" stays), and an utterance that is *only* a filler is typed as spoken
  rather than swallowed. The extra words are only removed when you are dictating
  in English.

- **A space after a finished sentence** (Settings → Output, on by default), so
  dictating three sentences as three separate takes no longer gives you
  "One.Two.Three." Skipped automatically after links, addresses and anything
  that looks like code.
- **Dictated code and aligned text keep their spacing.** "let x  =  1" and
  "foo :: bar" used to be tidied into "let x = 1" and "foo:: bar".
- **The dictionary now tells the model both halves.** A rule "armor forger →
  Arma Reforger" told the recogniser the correct spelling but never the
  mis-hearing it is about to produce; both now go into the prompt, labelled.
- **A diagnostic log** at `%APPDATA%\FlowType\attempts.log` — one line per
  dictation with timings, audio measurements and the outcome. **No transcript
  text ever**, so it is safe to attach to a bug report. Off with Settings →
  Privacy if you would rather not have it.

### Changed
- **Large · Maximum is now the recommended model on a machine with a dedicated
  GPU**, replacing Turbo · Full. FlowType's own benchmark has said for a release
  that Large makes about a third of Turbo's errors on mumbled speech; the
  recommendation now matches the measurement. Turbo remains one click away for
  anyone who prefers the speed.
- Fast decoding is genuinely fast now: it was still generating five candidate
  readings per attempt, which is most of the cost of the accurate mode.
- **Pasting waits for the app to actually take the text.** FlowType used to set
  the clipboard, press Ctrl+V, wait half a second and put your clipboard back —
  a race that a busy app or a remote desktop can win, pasting your *old*
  clipboard instead of what you said. It now hands the target a promise and puts
  your clipboard back only once the target has genuinely read it (and never if
  you copied something else in the meantime). Pasting is also about 120 ms
  faster, since there is no longer anything to wait for before the keystroke.
- Dictionary rules no longer rewrite text inside an email address, link or path.
- The recognition prompt is now labelled ("Preferred spellings: …") and capped so
  it always fits Whisper's window — a long dictionary used to silently push the
  spelling convention out of it.
- FlowType now says "CPU" when it is running on the CPU. On a machine whose
  graphics driver cannot provide Vulkan, the engine quietly fell back to CPU
  while the app still reported "GPU (Vulkan)".
- **The Visual C++ runtime now ships with FlowType.** The speech engine needs
  it, FlowType has no installer, and on a machine without it the engine simply
  failed to load. Packaging refuses to produce a release without it.

### Diagnostics
- `FlowType.exe --pastetest` — exercises the whole clipboard transaction against
  a real clipboard read, without sending keystrokes anywhere.
- `FlowType.exe --conditioncheck <folder>` — runs the audio conditioner over a
  folder of recordings and reports what it decided for each (verdict, gain,
  trimmed tail) in seconds, without transcribing.
- `--bench` now reports how many clips the pre-model speech check rejected.

## [1.3.0] — 2026-08-22

Theme of the release: hear unclear speech better, stay out of the way, go
monochrome.

### Added
- **Accurate decoding** (Settings → AI model, on by default): beam search with
  five candidates and the reference temperature-fallback ladder instead of
  taking the first guess. In testing on heavily degraded speech it roughly
  halves the word errors of Small and Large.
- **Large · Maximum** model (large-v3, 1.1 GB) in the catalog — the most
  accurate on mumbled speech, about twice Turbo's wait on a strong GPU.
- **GPU-aware recommendation**: machines with a dedicated GPU are steered to
  *Turbo · Full* automatically; CPU-only machines keep the Small/Base pick.
- **Hallucination guard**: on audio it cannot make out, Whisper sometimes
  echoes its own prompt ("The following is American English, and the
  following is…") or loops a phrase; both are now dropped before anything is
  typed. Genuine speech is untouched (covered by self-test vectors).
- **Warm-up after model load**: a short embedded clip runs through the model
  as soon as it loads, so the first real dictation no longer pays the 1–2 s
  GPU setup cost.
- Diagnostics: `FlowType.exe --bench <folder>` (word error rate and latency per
  model, decoding mode and clip condition), `--snapshot <folder>` (renders
  every window, page and flow-bar state to PNG off-screen), `--open
  [--settings]` (launch straight into the main window). `--selftest` now
  reports which GPU Whisper picked and checks the audio conditioner and the
  hallucination guard.

### Changed
- **Audio conditioning** before recognition: 80 Hz high-pass, gain lift for
  quiet takes (capped at +24 dB), lead-in and tail padding, and a silence gate
  so a near-silent take says "didn't catch anything" instead of producing a
  hallucinated "Thank you."
- **Capture tail**: the microphone stays open 280 ms after the hotkey is
  released, so a word released on its last syllable is no longer clipped.
- Dictionary rewrite targets (e.g. "Arma Reforger") now also bias
  recognition, not only vocabulary-only entries.
- Flash attention on the GPU path; thread count follows physical cores on the
  CPU path (was capped at 4).
- Whisper.net 1.8.1 → 1.9.1 (newer whisper.cpp / ggml Vulkan kernels).
- **Monochrome theme** everywhere: near-black surfaces, white accent, greys —
  the purple is gone from the windows, flow bar, tray menu, chips, README
  badges and the app icon. Check boxes, radio buttons, drop-downs, progress
  bars and scroll bars are now styled to match instead of using the stock
  Windows look; search and entry boxes show placeholder text.
- **Slimmer flow bar**: no taller than a line of text in any state (≤ 28 px,
  was up to 46), narrower, and moved down to 4 px above the taskbar so it
  stops covering message boxes at the bottom of chat apps.

### Fixed
- Utterances under one second were silently dropped (whisper.cpp refuses
  input shorter than 1 s); they are now padded and transcribed.
- Room noise that decoded to a lone "." or "-" no longer counts as text.

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
