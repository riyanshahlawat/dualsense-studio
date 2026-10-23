# Changelog

All notable changes to this project will be documented in this file.

## [1.0.14] - unreleased

- Updater hardened: only offers releases that include the installer **and** its
  `.sha256` checksum, only downloads from this repository's GitHub release URL over
  HTTPS, verifies the SHA-256 and the executable header before running, and fails
  closed (no more falling back to a web page). Separate long timeout for the download.
- Audio and mic now target Player 1's exact controller (`--hid-path`).
- Deterministic shutdown of the mic and game-controller workers.
- Removed settings that did nothing (sound source dropdown, protocol and period);
  the UI now states that audio follows the Windows output and uses the smooth mode.
- Live view reuses the game controller's HID reports (one reader per controller).
- One version source (`VERSION.txt`) for build scripts and CI.

## [1.0.13] - 2026-10-23

- Fixed stutter: always the smooth single-packet mode. Mic: mute stays in sync,
  no blip after unmute, 80 Hz low-cut, transparent limiter.

## [1.0.5 – 1.0.12] - 2026-10-23

- Echo cancellation (SpeexDSP) with measured Bluetooth echo delay, voice processing,
  packet-loss concealment, clock-drift correction, touchpad speed, MacBook-style
  touchpad, better Opus quality, Pro Audio threads.

## [1.0.0 – 1.0.4] - 2026-10-23

- DualSense Studio: Bluetooth headset audio + mic, Xbox/PlayStation emulation,
  multi-controller, PS5-style UI, installer and in-app updates.

## [0.0.1] - 2026-08-01

### Added

- DualSense Audio Studio WPF interface.
- Bluetooth audio streaming to the DualSense speaker and headset jack.
- WASAPI loopback capture, Opus encoding, ClockFix, and SendQueue.
- Audio device selection, real-time volume control, and integrated diagnostics.
- Real DualSense image and vector XAML icons.
- RGB lighting controller clearly identified as BETA.
- DSX and HidHide compatibility notices.
- Self-contained builds for Windows x64 and x86.
- Automated GitHub Actions builds.
- English interface, engine output, scripts, and public documentation.
