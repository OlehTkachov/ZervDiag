# CraneCAN Development Handoff — 2026-09-16

**Repository:** `OlehTkachov/ZervDiag`  
**Project:** `CraneCANAnalyzer/`  
**Current main CraneCAN line:** `feature/live-guided-can` @ `5ee1114a7376eebbc481ae51e0fdb8d921ce0d93`

## Evolution

1. `feature/onk160-only-test` — 0.4.1, passive ONK-160 UART/8E1 research.
2. `feature/generic-can-analysis` — 0.5.0, stable offline PCAN TRC field analyzer.
3. `feature/generic-guided-diagnostics` — 0.6, Guided offline experiments, repeatability, scoring, Machine Profile.
4. `feature/live-guided-can` — 0.7, PCAN listen-only + Replay + Incident/Network/Project architecture.

## CraneCAN branch map

| Branch | HEAD | Purpose |
|---|---|---|
| `master` | `6252e83c75b6` | Основная линия ZervDiag; не использовать как базу для CraneCAN-разработки. |
| `feature/onk160-only-test` | `29ee54cd0f5a` | CraneCAN ONK-160 0.4.1; замороженная специализированная ветка. |
| `feature/generic-can-analysis` | `2ba8872d5923` | CraneCAN Field 0.5.0; проверенный offline TRC-анализ, полевая контрольная точка. |
| `feature/generic-guided-diagnostics` | `b0a2da3d7ee6` | CraneCAN 0.6; Guided Diagnostics offline. |
| `feature/live-guided-can` | `5ee1114a7376` | Текущая основная линия CraneCAN 0.7; Live/Replay, Incident, Network, Project Package. |
| `feature/assistant-live-guided-next` | `6c272a60903c` | Экспериментальное продолжение; проверять diff перед переносом изменений. |
| `feature/assistant-live-guided-next-2` | `ddd2c9a84264` | Экспериментальное продолжение / WIP. |
| `feature/assistant-project-package-wip` | `ddd2c9a84264` | WIP по Project Package. |
| `feature/restore-project-package-ui` | `0169f4e821a3` | Отдельная ветка восстановления UI Project Package. |

## Architecture entry points

- `CraneCANAnalyzer/src/CraneCAN.Core/Analysis/` — generic comparisons, incident/first-divergence.
- `.../Guided/` — guided experiment pipeline and quality.
- `.../Live/` — live receiver/session/buffer/pre-fault/node health.
- `.../Network/` — passive network discovery, J1939/CANopen.
- `.../Storage/` — TRC, experiment/profile, project package and integrity.
- `CraneCANAnalyzer/src/CraneCAN.Driver.PcanBasic/` — real PCAN driver.
- `CraneCANAnalyzer/tests/CraneCAN.SmokeTests/` — regression suite and fixtures.

## Development invariants

- Diagnostic path is receive-only/listen-only; do not add CAN Tx.
- One experiment = one physical action; REFERENCE → ACTION → POST; prefer 3 repeats.
- Candidate != confirmed signal. Preserve evidence, repeatability and quality.
- Standard and Extended IDs remain distinct.
- RAW TRC is immutable evidence.
- Keep frozen branches unchanged; continue new work from an explicit development branch.

## Related field dataset

SOOSAN JK1200A material is intentionally isolated in `handoff/soosan-jk1200a-2026-09-16`. It is a regression/evidence dataset, not machine-specific Core logic.
