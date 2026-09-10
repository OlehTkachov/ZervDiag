# Portable Cross-Incident Signature Report — CraneCAN 0.7

## Назначение

Окно `Cross-Incident Repeatability / Signature` может сохранить результат анализа
3+ incident в переносимый Markdown-отчёт.

Отчёт предназначен для:

- передачи другому инженеру;
- разбора в другом сеансе ChatGPT/ИИ;
- архива вместе с исходными incident;
- анализа результата без запуска CraneCAN.

Режим полностью offline / read-only. CAN Tx отсутствует.

## Что сохраняется

### Серия incident

Для каждого выбранного `.canincident`:

- только имя `.canincident`, без абсолютного каталога;
- Incident ID;
- capture origin;
- driver;
- CAN channel;
- bitrate metadata;
- LISTEN ONLY confirmed;
- полнота;
- quality codes;
- количество CAN-кадров;
- подпись самого раннего marker;
- длительность incident-окна.

`capture.trc`, `RawTracePath`, `ReplaySourcePath`, `ContinuousRawPath` и абсолютные
локальные пути намеренно не попадают в отчёт.

### Machine Profile

Если профиль загружен:

- Profile ID;
- машина;
- производитель / модель;
- CAN bus;
- bitrate профиля;
- число Known / Experimental signals.

### Итог

- число incident;
- HIGH / MEDIUM / INFO;
- самый ранний HIGH-кандидат.

### Полная таблица кандидатов

Для каждого Cross-Incident кандидата:

- priority;
- CAN ID;
- Standard / Extended;
- DATA[n] или ID/DLC;
- тип события;
- сопоставленные Profile signals;
- repeatability N/M и процент;
- transition agreement N/M и процент;
- median / min / max времени относительно marker;
- timing spread;
- breakpoint count;
- модальный baseline → observed;
- текст наблюдения.

## Переносимость

Все числа времени и процентов форматируются через invariant culture. Поэтому
на Windows с `uk-UA`, `ru-RU` и другими локалями десятичный разделитель в
Markdown остаётся точкой.

Markdown-ячейки экранируют:

- `\`;
- `|`;
- CR/LF.

Файл сохраняется UTF-8 без BOM.

## Проверка целостности

Codec отклоняет отчёт, если:

- число `LoadedIncidentPackage` не совпадает с `IncidentSignatureAnalysisResult`;
- серия содержит повторный Incident ID;
- incident не содержит marker;
- кандидат рассчитан для другого числа incident.

Копия одного `.canincident` не считается независимым повторением.

## Ограничение интерпретации

HIGH означает устойчиво повторяющийся наблюдаемый CAN-шаг по текущим критериям
CraneCAN. Это не доказательство физической причинности и не идентификация
неисправного ECU, датчика, клапана, реле или механического узла.

Для инженерного вывода используйте вместе:

- GOOD/FAULT Incident Comparison;
- Machine Profile evidence;
- электрические/гидравлические схемы;
- документацию;
- физические и электрические измерения;
- повторяемую реакцию машины.
