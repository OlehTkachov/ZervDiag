# Hirschmann HC4900 profile — CraneCAN 0.7

## Назначение

Профиль `profiles/hirschmann_hc4900.craneprofile` — отдельный стартовый Machine Profile для LMI Hirschmann HC4900 / IC4600. Он предназначен для пассивной диагностики через Generic CAN, Guided Diagnostics, Live/Replay, Node Health, Incident и Machine Profile evidence.

Профиль не содержит CAN Tx и не предназначен для управления HC4900.

## Что подтверждено сервисным руководством

- система HC4900 использует CANopen 2.0B;
- исходная скорость CAN в руководстве указана как 125 kbit/s;
- CAN Bus State консоли показывает следующие Node-ID:
  - `1` — IC4600 display;
  - `3` — HC4900 central unit / Mentor;
  - `15` — length/angle sensor (cable reel);
  - `60` (`0x3C`) — piston oil pressure sensor;
  - `61` (`0x3D`) — rod oil pressure sensor;
- ошибки CAN:
  - `E61` — ошибка передачи для всех CAN units;
  - `E62` — ошибка CAN data transfer pressure transducer unit;
  - `E63` — internal error pressure transducer unit;
  - `E64` — ошибка CAN data transfer length/angle sensor unit;
  - `E94` — ошибка CAN data transfer HC4900 CU ↔ IC4600 console.

Важно: CANopen `Node-ID` — это адрес узла, а не готовый raw CAN identifier / COB-ID. Руководство не предоставляет полного списка прикладных COB-ID и раскладки DATA, поэтому CraneCAN не должен их выдумывать.

## Датчики и топология

Основной length/angle sensor находится в cable reel и связан с CAN через conversion board. A2B также входит в эту цепь. Дополнительный length sensor LG105 использует 4–20 mA и при включении в общую систему преобразуется в CAN внутри большого cable reel. Wind speed также проходит через converter board.

Поэтому в стартовом `.craneprofile` списки `knownSignals` и `experimentalSignals` пусты. Реальные сигналы следует добавлять только после пассивного наблюдения и повторяемого evidence.

## Физическое подключение CAN

Для X1 / системного 5-проводного кабеля руководство указывает:

| Pin | Назначение |
|---:|---|
| 1 | CAN_SHLD / shield |
| 2 | CAN +UB / CAN_V+ |
| 3 | CAN GND |
| 4 | CAN_H |
| 5 | CAN_L |

Цвета системного кабеля в руководстве: brown = shield, white = CAN_V+, blue = CAN_GND, black = CAN_H, gray = CAN_L.

CraneCAN/PCAN подключать параллельно штатной сети и только в listen-only. Не разрывать штатную связь и не добавлять терминатор без проверки существующей терминации.

## Как пользоваться профилем

1. Запустить `CraneCAN.Live.exe`.
2. Открыть вкладку `Диагностика`.
3. В блоке `Профиль машины` нажать `Открыть…`.
4. Выбрать `profiles\hirschmann_hc4900.craneprofile` из publish-папки.
5. Для Live начать с `125000` bit/s и подтвердить наличие штатного трафика, `CONNECTED`, `LISTEN ONLY`, `Lost = 0`, `Errors = 0`.
6. Сначала сохранить спокойный baseline / Configuration Snapshot.
7. Для неизвестных функций применять Guided `REFERENCE → ACTION → POST`, минимум 3 повтора.
8. Найденные DATA/bit поля добавлять в Machine Profile только как `CANDIDATE`, пока назначение не подтверждено повтором, схемой, измерением или сервисной документацией.
9. При плавающей неисправности использовать Node Health + Incident Recorder и затем `Incident First Changes` / `Event Chain`.

## Что намеренно не включено

В профиль не внесена таблица I/O из раздела руководства для конкретного применения XCMG как универсальная характеристика HC4900. Это application-specific конфигурация крана и не должна автоматически переноситься на другую машину с HC4900.

Также не созданы искусственные `MachineSignal` для длины, угла, давления или A2B, поскольку сервисное руководство не задаёт их CAN COB-ID, byte/bit location, endian и scale. Эти поля должны появиться только из доказательного анализа реального трафика.
