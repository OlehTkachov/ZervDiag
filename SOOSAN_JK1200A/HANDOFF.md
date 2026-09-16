# SOOSAN JK1200A Complete Diagnostic Handoff — 2026-09-16

## Current status

As of **2026-09-16**, the crane was started and the boom configuration was successfully restored by **manual training of every telescopic section**. The actual field procedure was a sequential pass through the sections, performing the required **PIN / UNPIN and LOCK / UNLOCK operations for each section** so that the controller could learn/reconstruct the section states. After this per-section manual training the crane became operational.

Therefore the previous automatic `Reset HYDAC length sensor` / `boResetBoomConf` blocker is **historical diagnostic context**, not the current operating blocker. Any future analysis must preserve this distinction.

## Machine / CAN

- Controller: HYDAC / TTControl HY-TTC 60-CD-594K-768K-0000-000; HW 7.01B; SW 623.
- ECU serial: `05320211009990`.
- MST: 0.8.81.31, project SoosannMSt.
- Primary bus: HCH2/HCL2, 250 kbit/s Classical CAN, Extended 29-bit.
- PCAN must be parallel + listen-only; do not break the CSM link and do not inject reset frames.

## Historical root chain

Before manual training, boom/SVE NVM configuration was UNDEF/0xFFFFFFFF. CSM could request Reset mode, pressure and joystick were valid, but `boResetBoomConf` bit 24 in `0x0CFF1328` did not assert; `GE_MOT_AUTO_IDLE` persisted and Y060/Y061 stayed zero.

Key corrections/findings: `i16pAccuMin` raw 70 = 7.0 bar; raw 700 = 70.0 bar. Successful LPU charge to ~85.3 bar was captured. `i16CylLengthLimitLower=-32000` was already present and was not the cause. Screen 45% after manual zeroing was not physical boom movement.

### What finally worked

The recovery did **not** require obtaining the four-digit Reset PIN. The working field solution was manual teaching of the telescopic mechanism: go through the boom sections one after another and execute/confirm the corresponding PIN/UNPIN and LOCK/UNLOCK states for every section. This restored a coherent learned configuration and allowed normal crane startup/operation.

For a future engineer this is the most important final-state correction to the earlier reports: do not treat the automatic Reset access problem as still unresolved if diagnosing the now-trained machine.

## swing_err.trc

- 155,485 frames; ~322.645 s; 104 IDs.
- DM1 SA 0x20 initially contained SPN 3195/FMI12/OC9 plus a second DTC. The second DTC changes from SPN 183047/FMI31/OC83 to SPN 165928/FMI26/OC74 at ~10.48 s. From ~22.53 s only SPN 3195/FMI12 remains in direct `18FECA20`.
- Semantic names for these SPNs are not confirmed in the indexed SOOSAN material. Do not label them without MPF/DBC/repeat evidence.

## Trace archive

The companion trace archive contains all 18 raw TRC files. `SHA256SUMS.txt` and `trace_manifest.json` in this branch are the integrity/index layer.

| File | Bytes | SHA-256 | Purpose |
|---|---:|---|---|
| `69f87dcc-21d2-427b-958d-700901782245.trc` | 817066 | `fb84aea45618cdecaf73a19f00a2732e6216e8658abbeefffb5d384f36a5c110` | 03.09.2026, состояния/ошибки SVE и конфигурации. |
| `9a63f3f1-61ec-4c61-81c8-f014f8f133ba.trc` | 2741147 | `52cb24395546f0bea2066e0bbbcd2b040f17149dc7c8fdaf0688d7e9efc987b5` | 04.09.2026, штатная попытка восстановления конфигурации; ключевой Reset/IDLE/Y061 evidence. |
| `JK1200A_ACH1_intercontroller.trc` | 583406 | `65cb048da4996d7b66205f170115e4b61ce5a2f774b7176ce1a9d4c5e118fc81` | Межконтроллерная линия. |
| `JK1200A_ACH3_LMB.trc` | 762606 | `db7f818a5ae1eba37d580469b39569a8d8986eeb7ba0f90d04c3afd25c971840` | Отдельный канал LMB/AML. |
| `JK1200A_HCH2_HYDAC.trc` | 2209867 | `bc0515926bbe387a524312e922f7772e256926b1f47464d619f3823edf1bdcdc` | Основная HYDAC/TTC60 шина. |
| `JK1200A_JCH4_joystick.trc` | 635468 | `85579293e7a39e564b9f240cc9ecfe1cda3f6c05c0a4c9124c5b86fa0d11cba3` | Джойстик; Standard ID 0x18F. |
| `JK1200A_auto_tele_2026-08-28(1).trc` | 316983 | `9a61f7d4fbfc3cb4e2a8ad7590d4cc5f8128cd8b5241245d52345c18cca9b2f7` | Вторая ранняя попытка auto telescoping. |
| `JK1200A_auto_tele_2026-08-28.trc` | 718420 | `9fcfdaa4e2efdf9c06b8063095502f355e06037aabfdc9960da9fb892140bc1b` | Ранняя попытка auto telescoping. |
| `capture(1).trc` | 1407 | `18df5cd3fe351e1f0145aa88bf46ae30e99339accd9eaf35bc3c7848c0811fa6` | Короткая поздняя контрольная запись. |
| `capture(2).trc` | 1603 | `209b5f25c8df34a78678e763a94fec34ae537473f5660e5963750a0ea43cc4bb` | Короткая поздняя контрольная запись. |
| `capture(3).trc` | 1707 | `b9b31b4d69d5e8814203ba56ef85ffe9ff5b5d824ec5c3fc23bc69f25717f978` | Короткая поздняя контрольная запись. |
| `capture.trc` | 1407 | `18df5cd3fe351e1f0145aa88bf46ae30e99339accd9eaf35bc3c7848c0811fa6` | Короткая поздняя контрольная запись. |
| `charge acc lpu.trc` | 3602251 | `dc07a9de2a0fdf696ee15a4d2465ceb89293c9de9f9a3c79fe25cf8eb27119c2` | Серия ранних опытов зарядки LPU. |
| `charge acc lpu1.trc` | 1906005 | `0e4e94dfa9472a82eaa1f4291e08b04dc7f7bc287e3a1406405db5a017a1ec70` | Серия ранних опытов зарядки LPU. |
| `charge acc lpu2.trc` | 2181797 | `4b531510219114ef8b65ec20e0d3c26fd89404b0b3c64a35924754b257348cb5` | Серия ранних опытов зарядки LPU. |
| `d0daf58e-0d98-4e66-bebe-9200f91d0f18.trc` | 1603 | `209b5f25c8df34a78678e763a94fec34ae537473f5660e5963750a0ea43cc4bb` | Дубликат по SHA с capture(2).trc. |
| `e819f832-014e-41da-abf9-e02a61b64c31.trc` | 1809911 | `82367b72027d82b99ff95ff861c55a289cf68c505c5a53ef2aa6ca5c1e977be9` | Успешная зарядка LPU: Y05, ~4.3→85.3 bar. |
| `swing_err.trc` | 10289108 | `61e5f4fa7d59eaa883b5c297c72f9d584390455632a87b28075fe834a8e29677` | 14.09.2026, поворот платформы / swing. |

## CraneCAN branches relevant to this dataset

| Branch | HEAD | Purpose |
|---|---|---|
| `master` | `6252e83c75b6` | Основная линия ZervDiag. |
| `feature/onk160-only-test` | `29ee54cd0f5a` | ONK-160 0.4.1, frozen. |
| `feature/generic-can-analysis` | `2ba8872d5923` | Field 0.5.0, stable offline TRC. |
| `feature/generic-guided-diagnostics` | `b0a2da3d7ee6` | 0.6 Guided offline. |
| `feature/live-guided-can` | `5ee1114a7376` | Current CraneCAN 0.7 line. |
| `feature/assistant-live-guided-next` | `6c272a60903c` | Experimental continuation. |
| `feature/assistant-live-guided-next-2` | `ddd2c9a84264` | Experimental/WIP. |
| `feature/assistant-project-package-wip` | `ddd2c9a84264` | Project Package WIP. |
| `feature/restore-project-package-ui` | `0169f4e821a3` | Project Package UI recovery. |

## Do not repeat

- Do not rewrite -32000 as a troubleshooting step.
- Do not treat 45% display as physical movement.
- Do not reproduce faults by disconnecting the monitor.
- Do not inject guessed reset frames or force Y060/Y061.
- Do not continue treating the old automatic Reset blocker as unresolved: manual per-section PIN/UNPIN + LOCK/UNLOCK training restored the configuration and the crane started on 2026-09-16.

## Best next evidence

Capture a new GOOD baseline of the manually trained working crane and bind it to the project with SHA-256. Compare it against the 2026-09-04 faulty/reset trace and use the pair as a regression case for First Divergence / Incident Chain.
