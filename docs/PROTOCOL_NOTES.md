# Experimental protocol notes

Parameters used by the prototype:

- USB Vendor ID: `0x054C`
- DualSense Product ID: `0x0CE6`
- Bluetooth report: `0x35`
- Report size: 334 bytes
- Audio codec: Opus
- Sample rate: 48,000 Hz
- Channels: 2
- Window: 10 ms / 480 frames
- Target payload: 200 bytes in CBR mode
- Controller speaker packet: `0x13`
- Headset jack packet: `0x16`
- CRC: variant used by Sony Bluetooth reports

These values are experimental and may vary by firmware.
