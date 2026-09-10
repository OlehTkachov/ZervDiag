# Profile-decoded Incident Signal Timeline — CraneCAN 0.7

## Назначение

Режим `График Profile signal…` строит временной график уже не raw `DATA[n]`,
а инженерного значения сигнала из текущего `Machine Profile`.

Он работает поверх сохранённого `*.canincident` и полностью offline.
CAN Tx отсутствует.

Исходный `График DATA-шаг…` остаётся без изменений и используется как
независимое raw evidence.

## Выбор сигнала

Кнопка доступна для выбранного `ByteChanged` шага Event Chain.

CraneCAN ищет в `KnownSignals` и `ExperimentalSignals` текущего Machine Profile:

- точный CAN ID;
- точный Standard/Extended формат;
- поле, пересекающее изменившийся `DATA[n]`;
- статус, отличный от `REJECTED`;
- корректную геометрию поля для выбранного ByteOrder.

Если подходящих сигналов несколько, инженер явно выбирает сигнал из списка.
`LittleEndian` / `BigEndian` отображается в выборе. Назначение сигнала не
угадывается автоматически.

## LittleEndian convention

Для `SignalByteOrder.LittleEndian` определена следующая точная конвенция:

- bit 0 = младший бит `DATA[0]`;
- абсолютный младший бит поля = `StartByte * 8 + StartBit`;
- поле занимает `BitLength` последовательных битов с возрастающими номерами;
- байты собираются как little-endian целое:
  `DATA[0] + DATA[1]<<8 + DATA[2]<<16 + ...`;
- после выделения поля выполняется sign extension, если `IsSigned = true`;
- инженерное значение: `raw * Scale + Offset`.

## BigEndian / DBC Motorola sawtooth convention

`SignalByteOrder.BigEndian` теперь поддерживается по **явно зафиксированной
DBC/Motorola sawtooth convention**. CraneCAN больше не угадывает одну из
нескольких несовместимых трактовок Motorola.

Правила:

- внутри каждого DATA-байта `bit 0 = LSB`, `bit 7 = MSB`;
- `StartByte / StartBit` указывает **старший значащий бит (MSB)** сигнала;
- следующие биты поля идут к меньшим номерам bit внутри текущего байта;
- после `bit 0` следующий бит — `bit 7` следующего DATA-байта;
- raw-значение собирается MSB-first;
- затем выполняется sign extension, если `IsSigned = true`;
- инженерное значение: `raw * Scale + Offset`.

Контрольные примеры:

```text
DATA = 12 34
StartByte=0 StartBit=7 BitLength=16 BigEndian
raw = 0x1234
```

```text
DATA = 00 0A BC
StartByte=1 StartBit=3 BitLength=12 BigEndian
bits = DATA[1].3..0 + DATA[2].7..0
raw = 0xABC
```

Таким образом, `StartBit` для BigEndian не является младшим битом поля: он
является его MSB согласно выбранной sawtooth convention.

## Диапазон поля

Для обоих ByteOrder поддерживаются поля длиной 1…64 бит.

Поле обязано полностью помещаться в Classical CAN `DATA[0…7]`. Для BigEndian
это проверяется по фактическому sawtooth-переходу между байтами. Например,
`StartByte=0, StartBit=7, BitLength=64` допустим, а
`StartByte=0, StartBit=0, BitLength=64` потребовал бы девятый байт и поэтому
отклоняется.

## Signed / Scale / Offset

После извлечения raw bits порядок байтов больше не влияет на математическую
интерпретацию:

- unsigned: `RawUnsigned`;
- signed: two's-complement sign extension по `BitLength`;
- engineering: `raw * Scale + Offset`.

Для 64-bit signed используется полный two's-complement диапазон `Int64`.

## Фильтрация кадров

Для графика используются только:

- Classical CAN;
- Rx;
- точный numeric CAN ID;
- точный Standard/Extended;
- не RTR;
- не error frame;
- DLC/длина DATA достаточна для полного битового поля с учётом ByteOrder.

Совпадающие кадры с коротким DATA пропускаются и учитываются в предупреждении.

## График

X — время относительно самого раннего marker (`0.000 s`).

Y — `raw * Scale + Offset` в единицах Machine Profile.

Отдельно показаны:

- `MARKER`;
- `CHANGE` из выбранного Event Chain шага;
- min/max engineering value;
- количество совпавших, декодированных и коротких кадров;
- ByteOrder и геометрия поля.

Для больших рядов применяется downsampling с сохранением:

- первого отсчёта;
- последнего отсчёта;
- локальных minimum/maximum каждого временного блока.

Исходные CAN-кадры не изменяются.

## Ограничение интерпретации

Machine Profile определяет способ декодирования, но не доказывает физическое
назначение сигнала.

Например, график с названием `Valve command` означает только, что текущий
профиль так интерпретирует эти биты. Для CONFIRMED назначения нужны независимые
evidence: схема, документация, повторяемый эксперимент, измерение или
физическая проверка.

Поддержка BigEndian также не означает автоматического определения endian.
ByteOrder должен быть задан в Machine Profile либо импортирован из источника
с известной конвенцией, например будущего DBC importer.

## Безопасность CAN

Декодирование LittleEndian/BigEndian работает только с уже полученными
CAN-кадрами. Оно не открывает transmit path и не меняет LISTEN ONLY архитектуру
CraneCAN.
