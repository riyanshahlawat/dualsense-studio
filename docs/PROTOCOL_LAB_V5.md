# V5 technical foundation

V4 sent a 334-byte `0x35` report with one Opus packet and did not perform the
complete audio subsystem initialization.

V5 adds a second strategy:

- `0x32`, 142 bytes, for SetState and volume;
- `0x39`, 547 bytes, for aggregated audio;
- two 200-byte Opus packets;
- speaker `0x13` or headset `0x16` selection;
- a default interval of 21.333 ms;
- comparison with the previous format.

The implementation is an independent rewrite based on publicly documented
behavior. It does not modify controller firmware.
