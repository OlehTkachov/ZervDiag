# CraneCAN `*.canproject` — рабочий проект машины

## Назначение

`.canproject` объединяет материалы диагностики одной машины в единое рабочее
пространство без копирования и изменения исходных файлов.

Проект может ссылаться на:

- Machine Profile (`*.craneprofile`);
- Guided Diagnostics experiment (`*.canexperiment`);
- incident (`*.canincident`);
- CAN snapshot (`*.cansnapshot`);
- TRC/CSV trace;
- Markdown/TXT report;
- PDF/DOCX/XLSX документацию;
- другие вспомогательные файлы.

## Принцип хранения

`.canproject` — JSON manifest schema version 1.

В manifest хранятся только **относительные пути внутри папки проекта**.
Абсолютные пути и `..` traversal запрещены.

Это сделано по двум причинам:

1. проект можно переносить целой папкой на другой ПК;
2. открытие полученного от другого инженера `.canproject` не должно автоматически
   читать произвольные файлы вне папки проекта.

Исходные файлы не копируются, не переименовываются и не удаляются. Удаление
resource из проекта удаляет только ссылку из manifest.

## Основные поля

- Project ID;
- schema/program version;
- имя проекта;
- машина, производитель, модель;
- CAN bus / bitrate / CAN type;
- active Machine Profile;
- active Guided Experiment;
- список resources;
- notes;
- timestamps.

Каждый resource имеет собственный Resource ID, тип, относительный путь,
понятную подпись и необязательную роль.

## UI

В верхней панели CraneCAN появляется кнопка `Проект…`.

Project Manager позволяет:

- создать новый проект;
- открыть существующий `.canproject`;
- сохранить manifest;
- добавить текущий профиль / эксперимент / TRC;
- добавить существующие файлы из папки проекта;
- убрать ссылку, не удаляя исходный файл;
- открыть из проекта Machine Profile, Guided Experiment, incident или TRC;
- увидеть отсутствующие resource.

При открытии проекта active Machine Profile загружается автоматически только
если файл существует внутри папки проекта. Другие resources не читаются
автоматически.

## Переносимость

Рекомендуемая структура:

```text
SOOSAN_JK1200A/
  SOOSAN_JK1200A.canproject
  machine.craneprofile
  experiments/
    TELESCOPE_OUT.canexperiment
  incident_good/
    incident.canincident
    capture.trc
  incident_fault/
    incident.canincident
    capture.trc
  reports/
    comparison.md
  docs/
    wiring.pdf
```

Если текущий профиль, эксперимент или TRC расположен вне папки `.canproject`,
CraneCAN не добавляет его автоматически и показывает предупреждение. Сначала
поместите/сохраните материал внутри папки проекта.

## Безопасность

Project Manager работает с локальными файлами и анализом уже сохранённых данных.
Он не добавляет CAN Tx и не меняет receive-only архитектуру Live Guided
Diagnostics.

`.canproject` не является доказательством причинности, конфигурацией ECU или
сервисным файлом машины. Это manifest инженерных материалов и provenance.
