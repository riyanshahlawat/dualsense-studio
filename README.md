# DualSense Studio

Use a PS5 **DualSense over Bluetooth** on Windows like a real headset and game
controller: headphone audio and the **microphone through the controller's 3.5 mm
jack**, games, haptics, lighting, and a PS5-style app you can drive with the
controller.

## Features

- **Headset audio over Bluetooth**: Windows shows one device, *DualSense Wireless
  Controller*, for both playback and microphone, selected automatically.
- **Microphone** from a headset plugged into the controller, with noise removal,
  *hear myself* (sidetone) and the controller's **mute button** (with LED and an
  on-screen pop-up).
- **Games**: every DualSense becomes a virtual **Xbox** or **PlayStation**
  controller (Player 1–4, multi-controller), game rumble goes back to the
  controller, and the touchpad works as a mouse. The real controller is hidden
  from games to avoid double input.
- **Haptics from game sound** (beta) and **Low latency mode**.
- **PS5-style home screen**: big tiles, live controller view, the whole app usable
  with the controller (L1/R1, D-pad, ✕, ○). PS button shows the desktop.
- Lighting, battery alerts, battery-colored light bar, tray, start with Windows,
  Start/Stop, and **automatic updates** from GitHub Releases.

## Install

Download **DualSenseStudio-Setup.exe** from
[Releases](https://github.com/riyanshahlawat/dualsense-studio/releases) and run it.
It installs these free, signed drivers if they're missing:

| Driver | Why |
|---|---|
| [VB-CABLE](https://vb-audio.com/Cable/) | headphone audio path |
| [VB-Audio Hi-Fi Cable](https://vb-audio.com/Cable/) | microphone path |
| [ViGEmBus](https://github.com/nefarius/ViGEmBus) | virtual Xbox / PlayStation controllers |
| [HidHide](https://github.com/nefarius/HidHide) | hides the real controller from games |

Then pair the DualSense in Windows Bluetooth settings (hold **PS + Create**).

## Build

Requires the .NET 9 SDK. `BUILD_X64.bat` builds the app; `BUILD_INSTALLER.bat`
also builds `release\DualSenseStudio-Setup.exe` (needs Inno Setup 6).

## Credits

- Built on [Kodzinho/DualSense-Bluetooth-Audio](https://github.com/Kodzinho/DualSense-Bluetooth-Audio) (MIT).
- Bluetooth microphone and haptics packet formats follow
  [hurryman2212/vds](https://github.com/hurryman2212/vds) (MIT); audio timing ideas from
  [DS5Dongle](https://github.com/loteran/DS5Dongle).
- Uses NAudio, OpusSharp, HidApi.Net and Nefarius.ViGEm.Client.

Unofficial community project, not affiliated with Sony Interactive Entertainment.
DualSense and PlayStation are trademarks of their respective owners.

License: [MIT](LICENSE).
