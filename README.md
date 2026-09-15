<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# SharpEmu ARM64 — Unofficial Proof of Concept

<p align="center">
  <img src="./assets/images/logo.png" width="30%" height="30%" />
</p>

<p align="center">
  <strong>Unofficial ARM64 fork of SharpEmu — initially targeting Android ARM64.</strong>
</p>

---

### About

This project is an **unofficial fork** of [SharpEmu](https://github.com/sharpemu/sharpemu), created only as a **proof of concept for ARM64 devices**, with initial support focused on **Android ARM64**.

This ARM64 version is still at a very early experimental stage. It requires **a lot of additional development, testing, and optimization** before it can become practical on ARM64 devices.

> [!IMPORTANT]
> The original **SharpEmu team has no affiliation, involvement, responsibility, or official connection with this ARM64 fork**. Please do not contact the original SharpEmu developers for support related to this version.

### Current Status

At the moment, the only tested game that reaches a boot state is **Dreaming Sarah**.

It is **not playable**.

This proof of concept currently uses an **adapted interpreter created mainly to make Dreaming Sarah boot** and validate the initial ARM64 approach. It should not be considered a complete or optimized CPU backend.

| Game | Status | Notes |
| :---: | :---: | :--- |
| **Dreaming Sarah** | **Booting** | Boots as a proof of concept, but is **not playable**. |
| ![Dreaming Sarah](./.github/images/dreaming-sarah.jpg) | | |

### CPU execution: basic-block cached interpreter

The guest CPU backend now executes hot code through a **basic-block decode cache**
("cached interpreter") — blocks are decoded once with Iced and validated as one contiguous
byte range per execution, instead of re-decoding every instruction every time it runs
(see `src/SharpEmu.Core/Cpu/Interpreter/X64BlockCache.cs` and
[`ARCHITECTURE_DECISION.md`](./ARCHITECTURE_DECISION.md) for the full rationale and the
measured 2.65x desktop speedup). Guest-visible semantics are unchanged — the legacy
per-instruction path remains as the universal fallback and is kept byte-identical by
parity tests (`X64InterpreterBlockCacheTests`), and it stays as the foundation for the
planned ARM64 JIT recompiler (phases 2-5 in the decision document).

This is also the long-term execution strategy decision for the Android/ARM64 port:
cached interpreter now, JIT recompiler later on top of the same block infrastructure.

### Android touch controls & session control

The Android touch overlay now drives the **same host input path as physical controllers**
(`VirtualPadInput` → `HostWindowInput` → `PadExports`), so overlay presses are
indistinguishable from SDL gamepad events to the guest, and backing out of a game requests
the same cooperative host shutdown the desktop CLI's Ctrl+C uses
(`GameSession.Stop()` → `VideoOutExports.NotifyHostInterrupt()`).

### CI

- `.github/workflows/build.yml` — builds the **debug APK** on every push to the default
  branch (and via manual dispatch); artifact: `SharpEmu-Android-<sha>-debug.apk`.
- `.github/workflows/android-release.yml` — unchanged: release APK on `android-v*` tags.

### Development Status

Development of this project will remain **paused** until my other port, **rpPS4**, based on [shadPS4](https://github.com/shadps4-emu/shadps4), becomes sufficiently solid and functional.

The ARM64 backend developed for **rpPS4** is planned to be reused as the foundation for the CPU/backend work in this unofficial SharpEmu ARM64 port.

For now, this repository should be treated strictly as an **early proof of concept**.

---

## Legal

This project is intended for research and educational purposes. It does not include copyrighted system firmware, game data, or proprietary PlayStation assets.

Este projeto é destinado a pesquisa e fins educacionais. Ele não inclui firmware de sistema protegido por direitos autorais, dados de jogos ou recursos proprietários do PlayStation.

Users are expected to use legally obtained game dumps.

Os usuários devem utilizar dumps de jogos obtidos legalmente.

## License

This fork follows the licensing terms of the upstream SharpEmu project.

Este fork segue os termos de licença do projeto SharpEmu original.

- [SharpEmu upstream](https://github.com/sharpemu/sharpemu)
- [GPL-2.0 license](https://github.com/sharpemu/sharpemu/blob/main/LICENSE)







