# CraneCAN — история разработки и инженерные решения

Состояние: 2026-09-16. Этот файл предназначен следующему программисту и объясняет не только что есть в коде, но и почему архитектура стала именно такой.

## 1. Исходная задача: ОНК-160 — версия 0.4.1

Первая задача была узкой: пассивно наблюдать нестандартный поток ОНК-160С-02 на стенде и находить, какой байт/бит меняется при одном известном физическом изменении. Выяснилось, что этот поток нельзя корректно трактовать как обычный Classical CAN через PCAN-USB, поэтому использовался отдельный тракт CAN-transceiver в Silent mode → USB-UART, 38400 8E1.

В `feature/onk160-only-test` были реализованы сборка пакетов CA/E5/E6/E8/F7, checksum, live-decode известных состояний, сравнение нормы и изменённого состояния, XOR/bit transitions, стабильность выборок и CSV. Методический принцип родился здесь: **один эксперимент = одно физическое изменение**, записать устойчивую норму, изменить один сигнал, записать новое состояние, вернуть в норму и проверить обратимость.

Главный результат 0.4.1 — доказательство самой методики controlled before/after reverse engineering. Эта ветка заморожена как regression/reference.

## 2. Переход к универсальному CAN — версия 0.5.0

SOOSAN JK1200A потребовал работы с нормальным Classical CAN, Standard и Extended 29-bit, файлами PCAN-View `.trc`. Поэтому специализированный ONK pipeline был отделён от Generic CAN Core.

В `feature/generic-can-analysis` реализованы:

- универсальная модель кадра с timestamp, ID, format, DLC, DATA;
- импорт PCAN TRC практических версий 1.x/2.x/3.x;
- строгая раздельность Standard/Extended ID;
- статистика по ID и периодам;
- фильтры по ID/формату;
- REFERENCE/ACTION по двум окнам одного файла или двум файлам;
- modal/stability compare по ID/byte;
- XOR и изменившиеся биты;
- CSV export;
- smoke/regression fixture `soosan_mixed.trc`.

Полевой SOOSAN JCH4 показал фундаментальное ограничение простой моды: реальный сигнал джойстика 0x18F менялся как ramp `00 → 0F → 11 → … → C8`. В широком ACTION-окне промежуточные значения могут встретиться по одному разу, поэтому modal consensus может пропустить смысл перехода. Это стало непосредственной причиной 0.6.

0.5.0 остаётся наиболее консервативной offline полевой версией: RAW пишет PCAN-View, CraneCAN анализирует файл, Tx отсутствует.

## 3. Guided Diagnostics — версия 0.6

`feature/generic-guided-diagnostics` создана от замороженной 0.5.0. Цель — чтобы сервисный инженер описывал действие человеческим языком («нажал кнопку», «джойстик EXTEND», «концевик сработал»), а программа сама искала CAN-кандидатов.

Трёхуровневая модель:

1. **Generic CAN Core** — марконезависимые кадры, статистика, compare, temporal analysis.
2. **Guided Diagnostics** — REFERENCE/ACTION, повторы, quality gate, temporal transitions, ranking.
3. **Machine Profile** — накопление знаний конкретной машины в открытом `.craneprofile`.

Temporal/Transition Analyzer сохраняет baseline, первое изменение и timestamp, последовательность значений, min/max, последнее устойчивое значение, время стабилизации, возврат к baseline, направление/скорость, монотонность и число переходов. Благодаря этому ramp больше не сворачивается в один modal byte.

Repeatability и score 0–100 сделаны прозрачными. Типовые положительные признаки: повторяемость во всех опытах, стабильный bit transition, появление/исчезновение ID, изменение после action, возврат к baseline, directed analog ramp. Штрафы: изменение до действия, analog noise, плохая повторяемость. Критическое правило: **даже score 100 не равен CONFIRMED**. Состояния знания: UNKNOWN → CANDIDATE → PROBABLE → CONFIRMED либо REJECTED. CONFIRMED требует внешнего/повторного evidence и осознанного подтверждения.

Machine Profile хранит bus, bitrate, ID/format, byte/bit/length, endian, signed, scale, offset, unit, enums, known/experimental/rejected signals, notes и evidence. Повторное обнаружение не создаёт дубль, а накапливает evidence.

## 4. Live Guided Diagnostics — версия 0.7

Текущая основная линия: `feature/live-guided-can`, HEAD `5ee1114a7376eebbc481ae51e0fdb8d921ce0d93` на 2026-09-16.

0.7 переносит тот же Generic/Guided pipeline в live, но без создания возможности CAN Tx.

### Драйверы

- `ReplayCanDriver` — real-time / accelerated / step replay TRC без машины.
- `PcanBasicCanDriver` — PCAN-USB, Classical CAN, 11/29-bit, timestamp, DLC/data, cancellation, diagnostics.
- `LiveCanBuffer` — ограниченный потоковый буфер последних ~60–120 секунд.

### Live state machine

`Idle → Baseline → WaitingForAction → Action → PostAction → Analyzing → Completed`, при проблеме `Aborted/Invalid`.

В `.canexperiment` сохраняются границы стадий, channel/bitrate, действие/инструкция оператору, номер повтора, quality warnings и результаты. Неполный/ошибочный эксперимент не должен порождать сильные candidates.

### Safety invariants

- пользовательского CAN Tx нет;
- PCAN live разрешается только при подтверждённом Listen-only;
- если passive mode нельзя подтвердить, канал закрывается;
- STOP/ABORT немедленно завершает эксперимент и помечает capture;
- RAW сохраняется независимо от результата анализа;
- при конфликте инструментов PCAN-View остаётся первичным RAW recorder до полной аппаратной валидации CraneCAN Live.

## 5. Единый pipeline

Любой источник (`PCAN-View TRC`, `Replay`, `PCAN-USB Live`) нормализуется в один `CanFrame stream`. Далее идут experiment/session boundaries → Generic + Temporal analysis → quality/scoring → candidate list → profile/report/project evidence. Это специально исключает ситуацию, когда replay и live используют разные алгоритмы и дают разные выводы.

## 6. Regression datasets

- ONK-160 packets/tests — checksum, packet assembly, bit compare, отсутствие регрессии старого специализированного протокола.
- `samples/soosan_mixed.trc` — parser Standard + Extended, Standard 0x18F transitions и Extended HYDAC.
- реальный `JK1200A_JCH4_joystick.trc` — event-driven ramp 0x18F.
- реальный `JK1200A_HCH2_HYDAC.trc` — большой Extended 29-bit HYDAC/J1939 dataset.
- `samples/live_guided_demo.trc` — Replay/state-machine/Guided pipeline без машины.

SOOSAN — **dataset, а не special-case logic**. Core не должен содержать `if machine == SOOSAN`.

## 7. Почему сохраняются старые ветки

0.4.1 и 0.5.0 являются известными контрольными состояниями. Новые функции развиваются в новых ветках, а не переписывают эти линии. Это даёт возможность в поле вернуться к проверенной сборке и позволяет искать regression через diff.

## 8. Следующее развитие

Приоритеты после 0.7: реальная Windows/PCAN validation (listen-only, hot unplug, reconnect, bus-off, timestamps, lost/errors, raw capture), GOOD TRACE vs FAULT TRACE с First Divergence, Event/Incident Chain (`input → intermediate → enable → command → output`), полностью streaming import больших TRC, project/session package с hashes и trace bindings, Signal Builder, DBC/J1939/CANopen как дополнительные источники evidence, не как обязательное знание Unknown CAN.
