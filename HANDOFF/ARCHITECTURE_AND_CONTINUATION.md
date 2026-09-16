# CraneCAN — architecture and continuation guide

## Repository location

Main solution: `CraneCANAnalyzer/`.

Key areas in current `feature/live-guided-can` line:

- `src/CraneCAN.Core/Analysis/` — generic comparison, first divergence / incident analysis.
- `src/CraneCAN.Core/Guided/` — guided experiment model, temporal analysis, repeatability, scoring, quality.
- `src/CraneCAN.Core/Live/` — live session, receiver/buffer, pre-fault/health logic.
- `src/CraneCAN.Core/Network/` — passive network discovery/protocol-neutral extensions (including J1939/CANopen-oriented work).
- `src/CraneCAN.Core/Storage/` — TRC, experiment/profile/project persistence and integrity.
- `src/CraneCAN.Driver.PcanBasic/` — physical PEAK PCAN driver.
- `tests/CraneCAN.SmokeTests/` — regression suite/fixtures.
- `samples/` — compact replay/regression traces.

## Data contracts that must remain stable

### CanFrame

A frame must preserve at minimum timestamp, bus/source, direction, numeric CAN ID, Standard/Extended format, DLC and 0–8 DATA bytes. Same numeric ID in Standard and Extended format is not the same message key.

### Raw trace

`.trc` is evidence. Never overwrite the source during analysis. Any generated result must retain a path/hash/binding to the exact source trace where possible.

### Experiment

REFERENCE/ACTION/POST boundaries, requested physical action, repeat number, channel/bitrate, quality flags and candidates must be persisted so the analysis can be reproduced later.

### Machine Profile

Open human-readable profile. Separate confirmed knowledge from experimental candidates and rejected hypotheses. Preserve evidence provenance.

## Build/test discipline

Before producing a field build:

1. Build the full solution for the intended Windows x64 target.
2. Run smoke/regression tests, including ONK and Generic fixtures.
3. Replay `live_guided_demo.trc` and verify the expected 0x18F transition is still found.
4. Exercise parser on Standard and Extended TRC.
5. Verify no diagnostic UI/path exposes CAN transmit.
6. Verify incomplete/aborted experiments do not emit strong candidates.
7. If PcanBasic changed, validate real hardware separately: passive mode proof, disconnect/reconnect, adapter removal, timestamps, frame/lost/error counters and safe close.

## Rules for new features

- Put protocol-neutral analysis in Core.
- Keep device-specific code in drivers/adapters.
- Do not hard-code SOOSAN/ONK semantics into generic algorithms.
- Prefer streaming processing for large traces; avoid loading million-frame files wholesale where unnecessary.
- New ranking heuristics must expose their evidence/contributions; avoid opaque confidence.
- Any new persistent schema should be versioned or migrated explicitly.
- A UI convenience feature must not weaken evidence or safety invariants.

## Recommended branch strategy

Frozen/checkpoint branches: `feature/onk160-only-test`, `feature/generic-can-analysis`. Keep them untouched.

Current development reference: `feature/live-guided-can` @ `5ee1114a7376eebbc481ae51e0fdb8d921ce0d93` as of this handoff.

Assistant/WIP branches (`feature/assistant-live-guided-next*`, `feature/assistant-project-package-wip`, `feature/restore-project-package-ui`) must be treated as sources for selective diff/review, not automatically as canonical successors.

For the next substantial feature create a new named feature branch from the explicitly chosen verified base and record that base SHA in the task/README.

## Immediate backlog

1. Validate PCAN-USB hardware on Windows and record objective pass/fail evidence.
2. Capture a GOOD baseline from the now manually trained SOOSAN JK1200A and bind it by SHA-256.
3. Add GOOD vs FAULT First Divergence using the 2026-09-04 SOOSAN reset/fault trace.
4. Complete Incident/Event Chain presentation and evidence export.
5. Strengthen project/package integrity and trace bindings.
6. Add scalable streaming tests using the real large SOOSAN traces.
7. Keep PCAN-View compatible raw capture as independent evidence.
