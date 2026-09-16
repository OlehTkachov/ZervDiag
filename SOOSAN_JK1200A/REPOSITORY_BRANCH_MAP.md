# ZervDiag / CraneCAN — карта веток на 2026-09-16

## Ветки, которые важны для CAN-диагностики

| Ветка | HEAD | Назначение |
|---|---|---|
| `master` | `6252e83c75b622d8850f742f8a4f80a2a2c6e39b` | Основная линия ZervDiag. Не считать актуальной линией CraneCAN. |
| `feature/onk160-only-test` | `29ee54cd0f5abe05a65714dbea337b6712881bfb` | CraneCAN ONK-160 0.4.1. Замороженная специализированная линия. |
| `feature/generic-can-analysis` | `2ba8872d592330b097c0c1a1def4611ef741a49b` | CraneCAN Field 0.5.0. Проверенный offline TRC-анализ; полевая fallback-версия. |
| `feature/generic-guided-diagnostics` | `b0a2da3d7ee6d079ff1c9c76606b041a5804d0fb` | CraneCAN 0.6. Guided offline diagnostics. |
| `feature/live-guided-can` | `5ee1114a7376eebbc481ae51e0fdb8d921ce0d93` | Основная развиваемая линия CraneCAN 0.7: PCAN Live/Replay, Guided, Incident, Network, Project Package. |
| `feature/assistant-live-guided-next` | `6c272a60903c003a6f3b6c6575d9455b45e35ee8` | Экспериментальное продолжение. Перед переносом изменений сверять diff с `feature/live-guided-can`. |
| `feature/assistant-live-guided-next-2` | `ddd2c9a842640d37375bd9effe9b0de70bf22a80` | Экспериментальная/WIP-линия. |
| `feature/assistant-project-package-wip` | `ddd2c9a842640d37375bd9effe9b0de70bf22a80` | WIP по Project Package. |
| `feature/restore-project-package-ui` | `0169f4e821a30332a4032322580c5d080ad75274` | Отдельная линия восстановления Project Package UI. |
| `handoff/cranecan-development-2026-09-16` | `0020d117840af4f054a2e3d2dfa950cd41124e31` на момент создания | Передача разработки CraneCAN следующему программисту. |
| `handoff/soosan-jk1200a-2026-09-16` | текущая ветка | Полное диагностическое досье SOOSAN JK1200A, trace manifest и контрольные суммы. |

## Другие ветки репозитория

Они относятся к более ранним/параллельным этапам ZervDiag и перечислены здесь для навигации: `library-auditor`; `stable-v13-2026-08-21`; `stable-v14-ai-base-2026-08-21`; `stable-v14-auto-index-2026-08-21`; `stable-v14-black-box-2026-08-21`; `stable-v14-final-2026-08-21`; `stable-v14-localization-2026-08-21`; `stable-v14-pre-ai-2026-08-21`; `stable-v14-ui-2026-08-21`; `stable-v14-windows-scheduler-2026-08-21`; `stable-v15-beta-approved-branding-2026-08-27`; `stable-v15-beta-commercial-2026-08-27`; `stable-v15-beta-db-transfer-2026-08-24`; `stable-v15-beta-final-2026-08-24`; `stable-v15-beta-installer-build-2026-08-21`; `stable-v15-beta-pre-final-2026-08-24`; `stable-v15-crash-recovery-2026-08-24`; `stable-v15-doc-repair-2026-08-22`; `stable-v15-endday-2026-08-21`; `stable-v15-post-ocr-crash-2026-08-24`; `stable-v15-provider-config-2026-08-21`; `stable-v15-retrieval-2026-08-21`; `v12-database-status`; `v13-heavy-ocr-queue`; `v14-auto-index-settings`; `v14-black-box-logging`; `v14-localization`; `v14-ui-assistant-foundation`; `v14-windows-scheduler`; `v15-ai-assistant`; `v15-ai-retrieval-ui`; `v15-beta-installer`; `v15-cloud-ai`.

## Правило продолжения разработки

Для новой универсальной CAN-функциональности использовать явную новую ветку от проверенного состояния `feature/live-guided-can` либо от последующего подтверждённого handoff commit. Не вносить экспериментальные изменения напрямую в `master`, `feature/onk160-only-test` или `feature/generic-can-analysis`.
