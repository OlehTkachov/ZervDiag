# Live Physical Signal Monitor

## Назначение

Live Physical Signal Monitor показывает выбранные Machine Profile signals в инженерных единицах во время пассивного приёма CAN. Он использует уже заданные в профиле `Scale`, `Offset` и `Unit`; физический смысл и единицы не выводятся автоматически из CAN.

Функция предназначена для практической проверки откалиброванных сигналов длины, угла, давления, нагрузки, положения и других непрерывных параметров на стенде или машине. CAN-передача отсутствует: монитор читает только уже принятые Rx Classical CAN frames из существующего Live/Replay buffer.

## Интерфейс

Кнопка `Физический монитор…` открывает отдельное немодальное окно. Основное окно CraneCAN остаётся доступным, поэтому монитор можно держать рядом с экраном машины и одновременно управлять обычным Live workflow.

Можно выбрать один или несколько сигналов. Для каждого показываются:

- текущее engineering value;
- Unit;
- текущее raw;
- minimum / maximum за последние 5 секунд данных этого sender;
- скорость изменения engineering value в единицу за секунду, рассчитанная least-squares slope;
- возраст последнего кадра;
- `FRESH`, `STALE`, `NO DATA`, `SOURCE STOPPED` или `REPLAY STOPPED`;
- sample count;
- наблюдаемый CAN ID;
- J1939 PGN / SA / DA, если применимо;
- Confidence;
- происхождение физической шкалы: `VALIDATED PASS`, `CALIBRATED`, `PHYSICAL EVIDENCE` или `UNIT ONLY`.

Обновление интерфейса выполняется примерно каждые 250 ms. Есть пауза отображения, которая не останавливает CAN receiver и не меняет данные.

## FRESH / STALE

Для Live PCAN reference time — текущее время. Сигнал считается `STALE`, если последний пригодный кадр старше 2 секунд.

Для Replay reference time — последняя временная отметка текущего replay buffer. Это позволяет корректно тестировать исторические TRC без сравнения их timestamps с wall clock.

Если transport остановлен, строка явно показывает `SOURCE STOPPED` или `REPLAY STOPPED`, даже если последнее сохранённое значение само по себе было свежим относительно replay timeline.

## Статистика и скорость

Min/Max и rate рассчитываются только по trailing window 5 секунд, заканчивающемуся последним кадром выбранного сигнала. Rate — наклон linear least-squares fit `physical(time)`, а не разница двух случайных соседних кадров. Для rate требуется временной span минимум 200 ms.

Tx, remote и error frames не участвуют. Frames с недостаточным DLC не декодируются и учитываются отдельно в detail.

## J1939

Если Machine Profile signal имеет `Protocol=J1939` и `J1939Pgn`, monitor использует существующее PGN-aware matching. Для PDU1 Destination Address остаётся фиксированным в соответствии с J1939 matching rules.

Если тот же PGN наблюдается от разных Source Address, текущее значение выбирается по sender последнего matching frame, а статистика 5 s не смешивает payload разных SA. Raw CAN ID и текущий SA остаются видимыми для инженерной проверки.

## Evidence

`VALIDATED PASS` означает наличие записанного результата независимого Calibration Validation Run. `CALIBRATED` означает наличие physical calibration evidence. `UNIT ONLY` означает, что Scale/Offset/Unit существуют, но monitor не нашёл physical validation/calibration evidence соответствующего типа.

Эти метки не меняют Confidence, не доказывают физическую причинность и не являются автоматическим подтверждением назначения сигнала.

## Безопасность

Live Physical Signal Monitor:

- не открывает CAN channel самостоятельно;
- не содержит CAN Tx API;
- использует существующий PCAN connection только в подтверждённом LISTEN ONLY режиме;
- не изменяет Machine Profile;
- не изменяет TRC, incident или project;
- не записывает evidence.

Для safety-critical техники показание CraneCAN является диагностическим наблюдением и не заменяет штатную систему безопасности крана.
