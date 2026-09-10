# CraneCAN — persistent TRC bindings in `*.canproject`

## Зачем это нужно

Автоматический project-aware rebinding умеет находить старый TRC по пути или по
уникальному имени. Но у реальной машины часто есть несколько файлов с одинаковым
именем, например `capture.trc` в разных incident/экспериментах.

В такой ситуации CraneCAN не должен угадывать. Persistent binding позволяет
инженеру один раз явно указать:

`GuidedExperiment resource + Repeat + REFERENCE/ACTION/RETURN -> Trace resource`.

После этого выбор хранится в `*.canproject` и используется первым при каждой
последующей загрузке эксперимента.

## Формат

В schema 1 `.canproject` добавлено необязательное поле `traceBindings`. Старые
проекты без этого поля продолжают открываться: список привязок по умолчанию пуст.

Каждая привязка содержит только:

- `bindingId`;
- `experimentResourceId`;
- `repeatNumber`;
- `role`: `reference`, `action` или `return`;
- `traceResourceId`;
- `updatedAt`.

Абсолютный legacy-путь TRC в binding **не сохраняется**. Поэтому привязка остаётся
переносимой вместе со всей папкой проекта.

## Приоритет разрешения

При загрузке повторов Guided Experiment порядок такой:

1. если для конкретного `experiment + repeat + role` есть persistent binding —
   использовать только указанный Trace resource;
2. если binding отсутствует — применить существующий project-aware resolver:
   точное совпадение пути, затем единственное совпадение имени;
3. затем сохранить fallback для эксперимента вне проекта: существующий абсолютный
   путь или путь относительно каталога `*.canexperiment`.

Persistent binding является авторитетным. Если связанный Trace resource
зарегистрирован, но файл исчез, CraneCAN **не переключается молча** на другой
`capture.trc` с тем же именем. Загрузка останавливается и показывает проблему
привязки.

## UI

В верхней панели есть кнопка `TRC привязки…`.

Окно позволяет:

- выбрать Guided Experiment из текущего проекта;
- увидеть все REFERENCE / ACTION / RETURN зависимости по повторам;
- увидеть, где работает автоматическое разрешение и где оно неоднозначно;
- выбрать любой Trace resource проекта;
- сохранить явную привязку;
- очистить привязку и снова вернуться к автоматическому разрешению.

Изменение binding сразу сохраняет `*.canproject`. Исходный `*.canexperiment` и
`*.trc` не переписываются.

## Перенос на другой ПК

Пример:

```text
SOOSAN_JK1200A/
  SOOSAN_JK1200A.canproject
  experiments/
    TELESCOPE_OUT.canexperiment
  traces/
    good/capture.trc
    fault/capture.trc
```

Если старый experiment всё ещё содержит путь вроде
`D:\old_machine\captures\capture.trc`, инженер может назначить, например,
`Repeat 2 / ACTION -> traces/fault/capture.trc`.

В manifest сохраняются Resource ID, а сами resources уже имеют относительные
пути внутри проекта. После переноса всей папки на другой диск привязка остаётся
однозначной.

## Контроль качества

Smoke tests проверяют:

- JSON round-trip binding;
- перенос всей папки проекта;
- два одинаковых `capture.trc`;
- независимые привязки REFERENCE/ACTION/RETURN;
- запрет target resource не типа Trace;
- дедупликацию повторной установки того же binding;
- отсутствие fallback на другой файл, если явно связанный Trace исчез;
- обнаружение stale binding после удаления resource.

## Безопасность

Механизм работает только с локальными сохранёнными файлами. Он не добавляет CAN
Tx, не отправляет кадры на машину и не меняет LISTEN ONLY архитектуру CraneCAN.
