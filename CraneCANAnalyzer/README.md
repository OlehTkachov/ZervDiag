# CraneCAN 0.7 — Live Guided Diagnostics

CraneCAN выполняет управляемые диагностические эксперименты в неизвестной
Classical CAN-сети: записывает спокойное состояние `REFERENCE`, показывает
оператору инструкцию, записывает `ACTION` и `POST`, затем передаёт эти окна
существующему Generic Analyzer. Результат — ранжированные проверяемые
кандидаты, а не автоматическое утверждение о назначении сигнала.

> **Безопасность:** Live-драйвер работает только в аппаратно подтверждённом
> `LISTEN ONLY`. В сборке нет вызова PCAN Write/Send и нет пользовательских
> функций CAN Tx. CraneCAN не управляет машиной и не предлагает обход защит.

## Сначала проверить без машины

1. Запустить `CraneCAN.Live.exe`.
2. Открыть вкладку **LIVE GUIDED**.
3. Источник: **TRC Replay — без машины**.
4. Нажать **TRC для Replay…** и выбрать
   `samples\live_guided_demo.trc`.
5. Нажать **ПОДКЛЮЧИТЬ**, затем **START GUIDED EXPERIMENT**.
6. Дождаться автоматического прохождения `REFERENCE → ожидание → ACTION →
   POST → ANALYZING → COMPLETED`.
7. Убедиться, что найден кандидат Standard `0x18F`, включая переход
   `DATA[1] 00→02`. Сохранить `.canexperiment` и открыть его на вкладке
   **Диагностика**.

Replay использует тот же `ICanDriver`, потоковый буфер, state machine,
сохранение и Generic Analyzer, что и реальный PCAN.

## Реальный PCAN-USB

На Windows должен быть установлен официальный x64-драйвер PEAK с
`PCANBasic.dll`.

1. Физически подключить PCAN параллельно к проверенной CAN-линии. Штатное
   соединение машины сохранить.
2. Не включать дополнительную терминацию адаптера, если шина уже имеет
   штатные два терминатора.
3. На вкладке **LIVE GUIDED** выбрать **PEAK PCAN-USB**.
4. Выбрать bitrate; для SOOSAN JK1200A — `250000`.
5. Нажать **Найти каналы**, выбрать PCAN-USB и нажать **ПОДКЛЮЧИТЬ**.
6. Продолжать только если постоянно показаны `CONNECTED` и `LISTEN ONLY`.
   Если пассивный режим не подтверждён, CraneCAN сам закроет канал.
7. Указать одно действие, инструкцию, номер повтора и длительности. Нажать
   **START GUIDED EXPERIMENT**.
8. Выполнять только крупную текущую инструкцию на экране. При любой
   неопределённости нажать **ABORT / STOP**.
9. Для repeatability повторить один и тот же опыт три раза, не меняя условий.
10. Сохранить `.canexperiment`. Полный сырой TRC сохраняется независимо в
    `Документы\CraneCAN\LiveCaptures`.

Live PCAN не является обязательным для ближайшего полигона: PCAN-View остаётся
главным регистратором, а стабильный offline-анализ `.trc` сохранён целиком.

## Что реализовано

- `ReplayCanDriver`: real-time, accelerated и step;
- `PcanBasicCanDriver`: PCAN-USB, Classical CAN, 11/29-bit, timestamp, DLC,
  DATA, отмена, безопасное закрытие и диагностика потерь/ошибок;
- аппаратная установка и обратная проверка `LISTEN ONLY`;
- отсутствие transmit API в PCAN-сборке;
- потоковый `LiveCanBuffer` на 120 секунд с ограничением памяти;
- непрерывное сохранение сырого PCAN-View-совместимого TRC;
- состояния `Idle/Baseline/WaitingForAction/Action/PostAction/Analyzing/
  Completed/Aborted` и точные timestamps переходов;
- конфигурируемые длительности и Generic-названия действий;
- автоматическое подавление кандидатов у `INVALID/ABORTED` опыта;
- несколько повторов, repeatability, temporal analysis и confidence score;
- сохранение raw capture, границ, канала, bitrate, инструкции, счётчиков,
  quality warnings и кандидатов в открытом `.canexperiment`;
- повторное открытие Live-эксперимента через общий offline pipeline;
- прежний импорт PCAN-View TRC 1.0–3.0, Generic CAN экран, сравнение окон,
  Machine Profile, SOOSAN и ONK regression tests.

Подробный безопасный регламент: [`docs/LIVE_GUIDED_DIAGNOSTICS.md`](docs/LIVE_GUIDED_DIAGNOSTICS.md).
Архитектура аналитики: [`docs/GENERIC_GUIDED_DIAGNOSTICS.md`](docs/GENERIC_GUIDED_DIAGNOSTICS.md).

## Сборка на Windows

Нужны Windows 10/11 x64, PowerShell и .NET 8 SDK:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build-windows.ps1
```

Скрипт сначала собирает и запускает все ONK/Generic/Live smoke tests, а только
затем создаёт автономную папку:

```text
publish\live-guided-win-x64
```

Запуск: `CraneCAN.Live.exe`. На целевом ноутбуке .NET Runtime и Python не
нужны; переносить следует всю папку. Для реального Live нужен установленный
официальный PEAK Device Driver x64. Для Replay он не нужен.

## Известные ограничения 0.7

- физический PCAN-USB нельзя аппаратно проверить в Linux-среде разработки;
  первый реальный приём следует проверять в цеху, не на полигоне;
- PCAN-View и CraneCAN могут конфликтовать за канал либо за режим/bitrate;
  при конфликте главным регистратором остаётся PCAN-View;
- J1939/DBC-декодирование, AI/GPT, голос, автоматический выбор следующего
  эксперимента и CAN Tx не входят в этот milestone;
- назначение сигнала остаётся `CANDIDATE/PROBABLE`, пока пользователь не
  добавил независимое evidence и явно не подтвердил его.
