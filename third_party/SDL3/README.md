# SDL3 (vendored)

Official Windows x64 build of SDL, used for gamepad input (stage 3).
Shipped next to the exe; loaded only from the app folder.

- Version: 3.4.18
- Source: https://github.com/libsdl-org/SDL/releases/download/release-3.4.18/SDL3-3.4.18-win32-x64.zip
- SHA-256 of SDL3.dll: 2b4b128d8d510cc2d442693cf848c050d700d43dc667afe9e5fd82b0570d667e
- License: zlib (LICENSE.txt)

To update: download the new `SDL3-<version>-win32-x64.zip` from the same
releases page, replace `SDL3.dll` and `LICENSE.txt`, update this file, and
check the gamepad event layouts in `include/SDL3/SDL_events.h` against
`src/OverlayUtils.Core/Input/Sdl.cs`.
