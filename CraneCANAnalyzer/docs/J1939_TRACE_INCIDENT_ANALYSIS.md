# J1939 TRC / Incident Analysis — CraneCAN 0.7

## Назначение

Этот этап переносит DBC/J1939 metadata из `Machine Profile` непосредственно в анализ уже записанных CAN-данных.

CraneCAN теперь умеет:

- декодировать J1939 Profile signals во всём открытом TRC;
- сопоставлять J1939 signal не только по одному 29-bit CAN ID, а по PGN;
- учитывать изменение Source Address (SA);
- сохранять Destination Address (DA) как часть сопоставления для PDU1;
- показывать PGN / SPN / SA / DA в Incident Event Chain;
- показывать engineering transition для подходящего ByteChanged шага;
- строить Profile Signal Timeline по всем кадрам подходящего PGN, даже если SA меняется.

Функция полностью пассивная: анализируется только уже полученный Rx Classical CAN. CAN Tx отсутствует.

## Почему одного точного 29-bit ID недостаточно

Для J1939 Source Address занимает младший байт 29-bit CAN ID. Один и тот же PGN может наблюдаться с разными SA.

Например:

```text
0x18F004A1
0x18F004B2
```

оба кадра имеют PGN `0x0F004`, но разные Source Address (`0xA1` и `0xB2`).

Если Machine Profile был создан из DBC с canonical ID `0x18F00400`, CraneCAN не требует, чтобы фактический кадр заканчивался на `00`. Для J1939 используется PGN-aware matching.

Canonical ID в Machine Profile остаётся частью определения/provenance сигнала и не переписывается фактическим SA из трассы.

## PDU1 и PDU2

Для PDU2 (`PF >= 240`) PS входит в PGN. При одинаковом PGN разрешается изменение SA.

Для PDU1 (`PF < 240`) PS является Destination Address и не входит в PGN. Поэтому CraneCAN требует одновременно:

- одинаковый PGN;
- одинаковый Destination Address;
- Source Address может меняться.

Это предотвращает ошибочное объединение двух PDU1 сообщений, адресованных разным узлам.

## Кнопка `J1939 TRC…`

Кнопка добавлена в блок Machine Profile.

Перед использованием:

1. открыть TRC;
2. открыть/сформировать Machine Profile;
3. импортировать DBC или иметь J1939 signals с заполненными `Protocol=J1939` и `J1939Pgn`;
4. нажать `J1939 TRC…`.

Для каждого J1939 signal отображаются:

- PGN;
- SPN, если известен;
- имя signal;
- confidence;
- все наблюдавшиеся Source Address;
- количество совпавших кадров;
- количество успешно декодированных кадров;
- количество кадров с недостаточным DATA/DLC;
- minimum / maximum engineering value;
- последнее engineering value;
- время первого и последнего декодированного кадра.

Engineering value вычисляется тем же `MachineSignalDecoder`, что используется Profile Signal Timeline:

```text
engineering = raw * Scale + Offset
```

Поддерживаются LittleEndian и зафиксированная DBC/Motorola BigEndian sawtooth convention.

## Incident Event Chain

Базовый Incident First Changes по-прежнему остаётся raw-анализом наблюдаемых CAN ID/байтов.

После построения Event Chain J1939 enrichment выполняет дополнительное сопоставление с Machine Profile. Для подходящего шага таблица показывает:

- observed 29-bit ID;
- PGN;
- SPN или список SPN, если один DATA byte пересекает несколько Profile signals;
- observed SA;
- DA для PDU1;
- имя Profile signal;
- confidence;
- engineering transition, если его можно декодировать.

Пример:

```text
Observed ID: 18F004B2
PGN:        0x0F004
SPN:        190
SA:         0xB2
Profile:    EngineSpeed
Engineering: 800 → 1000 rpm
```

Raw transition (`0xXX -> 0xYY`) остаётся видимым отдельно. Engineering interpretation его не заменяет.

## Profile Signal Timeline для J1939

При выборе J1939 Profile signal график собирает все Rx кадры incident, которые соответствуют:

- PGN сигнала;
- PDU1 Destination Address, если применимо;
- геометрии поля и достаточному DATA length.

Source Address не обязан совпадать с canonical ID Profile signal.

Перед передачей кадров существующему timeline-decoder создаются только временные копии с нормализованным ID. Оригинальный `PreFaultIncident` и его CAN frames не изменяются.

Если один PGN наблюдался от нескольких SA, это явно показывается в предупреждениях и в заголовке графика.

## Короткий DLC / DATA

Кадр может совпадать по PGN, но быть слишком коротким для полного поля. Такой кадр:

- учитывается как совпавший;
- не используется для engineering decode;
- увеличивает счётчик `short`;
- вызывает предупреждение.

CraneCAN не дополняет отсутствующие байты нулями и не угадывает значение.

## Confidence и Evidence

DBC/J1939 metadata является документированным независимым evidence, но не доказывает, что конкретный физический вход/выход машины соответствует предполагаемому назначению на конкретной конфигурации.

Этот анализ:

- не меняет `CANDIDATE / PROBABLE / CONFIRMED` автоматически;
- не создаёт `CONFIRMED` только по совпадению PGN/SPN;
- не объявляет первый изменившийся SPN причиной неисправности;
- сохраняет raw CAN как отдельное evidence.

## Текущее ограничение First Changes

На этом этапе исходный `IncidentTransitionAnalyzer` всё ещё формирует raw candidates по точному CAN ID.

Следствие: если между baseline и search изменился только Source Address при том же PGN, raw First Changes может сначала показать старый ID как исчезнувший и новый ID как появившийся. J1939 enrichment корректно распознаёт PGN metadata для этих шагов, но сам raw candidate generation ещё не нормализован по PGN.

Следующий J1939-этап должен добавить отдельное PGN-aware First Changes представление, не уничтожая исходный exact-ID анализ.

## Безопасность

Все операции этого раздела работают с receive-only данными:

- открытый TRC;
- сохранённый incident;
- Machine Profile;
- DBC/J1939 metadata.

Передача CAN сообщений, Request PGN, Address Claim, диагностические команды и любые ECU write/reset операции здесь отсутствуют.
