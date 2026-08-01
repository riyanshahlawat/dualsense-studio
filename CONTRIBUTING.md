# Contributing

Thank you for your interest in DualSense Bluetooth Audio.

## Before you start

1. Check whether an issue already covers the topic.
2. Open an issue before working on a large change.
3. Preserve the working audio engine and avoid unrelated refactoring.

## Development environment

- Windows 10 or Windows 11;
- .NET 9 SDK;
- a DualSense connected over Bluetooth for hardware tests.

## Submitting a change

1. Create a branch from the default branch.
2. Keep the change small and focused.
3. Build x64 and x86 with `BUILD_APP.bat`.
4. Confirm that audio, HID queue shutdown, and diagnostics still work.
5. Update documentation and the changelog when needed.
6. Open a pull request that explains the problem and solution.

Do not commit binaries, build folders, personal files, or diagnostic data that
could identify your computer.
