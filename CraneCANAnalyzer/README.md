# CraneCAN 0.6 — Generic Guided Diagnostics

Универсальный offline-инструмент для исследования неизвестных Classical CAN
сетей через понятные физические эксперименты. CraneCAN помогает найти, какие
CAN-изменения совпали с кнопкой, джойстиком, концевиком, реле, клапаном или
режимом машины, не требуя заранее знать ID, byte, bit, DBC, J1939 или scaling.

> **Безопасность:** в CraneCAN отсутствуют CAN Tx и live-драйвер. PCAN-View
> записывает исходный `.trc` в Listen-only, CraneCAN анализирует сохранённый
> файл. Программа не предлагает обход защит машины.

## Быстрый запуск диагностики

1. Запустить `CraneCAN.exe`.
2. На первой вкладке **Диагностика** нажать **Открыть PCAN-View TRC**.
3. Ответить на вопрос «Что вы хотите найти?» и назвать физическое действие,
   например `Joystick EXTEND`.
4. Указать спокойное окно `REFERENCE` и окно `ACTION` в миллисекундах от начала
   трассы. Примерное время действия можно оставить пустым или задать с `±`.
5. Нажать **Добавить повтор**. Для надёжного вывода добавить 3 опыта.
6. Нажать **АНАЛИЗИРОВАТЬ**.
7. Выбрать кандидата и прочитать объяснение score: переход, время реакции,
   agreement, repeatability, возврат и штрафы.
8. Добавить сигнал в профиль как `CANDIDATE`, `PROBABLE` или после
   пользовательского подтверждения — `CONFIRMED`.
9. Сохранить `.craneprofile` и `.canexperiment`.

Главный экран показывает человекочитаемый вывод до HEX. Полные кадры,
timestamp, Δt, DLC, DATA, XOR, bit transitions и статистика остаются доступны
на Expert-вкладках.

## Что работает в первом этапе 0.6

- PCAN-View TRC 1.0/1.1/1.2/1.3/2.x/3.0;
- Standard 11-bit и Extended 29-bit Classical CAN;
- однофайловые REFERENCE/ACTION окна и прежнее сравнение двух TRC;
- несколько Guided-повторов и repeatability `N/M`;
- поиск фронта около примерного времени с допуском `±`;
- stable bit 0→1/1→0, stable byte, different DLC, появление/исчезновение ID;
- temporal sequence, min/max, stabilization, return, rate и monotonicity;
- различение analog ramp и нестабильного noise;
- прозрачный score 0–100 с каждым вкладом;
- проверки пустых/коротких/пересекающихся окон и разных buses;
- статусы `UNKNOWN/CANDIDATE/PROBABLE/CONFIRMED/REJECTED`;
- evidence и открытый JSON machine profile;
- сохранение/открытие эксперимента со ссылками на внешние TRC;
- прежний полный Generic CSV export;
- прежние ONK-160 и SOOSAN regression smoke tests.

Архитектура и модель доказательств описаны в
[`docs/GENERIC_GUIDED_DIAGNOSTICS.md`](docs/GENERIC_GUIDED_DIAGNOSTICS.md).

## Expert CAN

Существующие вкладки сохраняют полный технический доступ: исходные кадры,
фильтры ID/format, frequency/period statistics, modal comparator, XOR, bit
transitions и полный CSV с шумовыми и неизменившимися строками.

## SOOSAN regression fixture

SOOSAN не является специальной логикой CraneCAN. Контрольный файл
`samples/soosan_mixed.trc` проверяет Generic Core на реальных данных:

```text
Standard 0x18F
08 00 00 00 00  →  08 02 C8 00 00
```

Для проверки задать REFERENCE `0–40 ms`, ACTION `200–240 ms`. Должны быть
найдены `DATA[1] 00→02` и `DATA[2] 00→C8`. В той же fixture сохраняются
Extended HYDAC/J1939 кадры, включая `0x0CFF5321`.

## Сборка на Windows

Нужны Windows 10/11 x64 и .NET 8 SDK:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build-windows.ps1
```

Скрипт собирает и запускает smoke tests, затем публикует автономную папку:

```text
publish\generic-guided-win-x64
```

Запуск: `CraneCAN.exe`. На полевом ноутбуке .NET Runtime не требуется;
переносить нужно всю папку публикации.

## Известные ограничения

- импорт пока ограничен 1 000 000 обычных CAN-кадров;
- parser возвращает список кадров; streaming/progress/cancellation — следующий этап;
- timeline хранится в Core, но отдельная кликабельная шкала ещё не выведена;
- графики, event-chain GOOD/FAULT, `.canproject`, Signal Builder, DBC/J1939
  decoding и live PCAN не входят в первый этап;
- приложение не выполняет CAN передачу.
