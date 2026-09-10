# CraneCAN — Project Package Verify без распаковки

## Назначение

Кнопка `Проверить ZIP…` проверяет переносимый CraneCAN Project Package напрямую внутри ZIP. Файлы проекта не распаковываются, не публикуются и не перезаписываются.

Результат проверки всегда отображается как `VALID` или `INVALID` вместе с диагностикой.

## Что проверяется

CraneCAN проверяет:

- ZIP открывается и имеет допустимую структуру;
- пути entries безопасны для Windows;
- нет `../`, абсолютных путей, ADS, reserved device names, symbolic links и case-insensitive дублей;
- присутствует ровно один корневой `*.canproject`;
- присутствует ровно один `CraneCAN.package-manifest.json`;
- `.canproject` читается и проходит schema/traceBindings validation;
- Project ID и имя project file во внутреннем SHA-256 manifest согласованы с `.canproject`;
- фактический набор payload равен `.canproject` + зарегистрированные resources;
- набор fingerprints во внутреннем manifest точно соответствует этому payload;
- каждый payload entry читается прямо из ZIP, его фактический размер и SHA-256 совпадают с записанными при экспорте значениями;
- вычисляется SHA-256 самого ZIP и SHA-256 внутреннего manifest.

## Что означает VALID

`VALID` означает, что структура package согласована и все payload bytes совпадают с fingerprints во внутреннем SHA-256 manifest.

Это позволяет обнаружить случайное повреждение или изменение payload без обновления manifest.

`VALID` не является доказательством авторства. Если злоумышленник изменит payload и одновременно пересоздаст внутренний manifest, внутренней проверки недостаточно. Для подтверждения происхождения нужен заранее известный SHA-256 всего ZIP по доверенному каналу либо цифровая подпись.

## Что означает INVALID

`INVALID` выдаётся, например, если:

- отсутствует internal SHA-256 manifest;
- legacy package создан старой версией CraneCAN;
- изменён хотя бы один byte TRC/profile/incident/project resource;
- размер payload не совпадает;
- Project ID в manifest не совпадает с `.canproject`;
- есть лишний или отсутствующий resource;
- путь ZIP entry небезопасен;
- ZIP содержит symbolic link или duplicate path;
- `.canproject` или internal manifest повреждены.

Для `INVALID` импортировать package как доверенную контрольную копию не следует.

## Отличие от Импорт ZIP…

`Проверить ZIP…`:

- ничего не распаковывает;
- не создаёт project directory;
- не меняет текущий открытый project;
- подходит для быстрой проверки архива перед импортом или передачей.

`Импорт ZIP…` после проверки дополнительно безопасно распаковывает package во временный каталог, выполняет Project Integrity и только затем публикует новую project directory.

## Производительность

SHA-256 считается потоково. Большие TRC не загружаются целиком в память. Metadata (`*.canproject` и internal hash manifest) ограничены безопасным размером 16 MiB, общий package — теми же высокими пределами, что и безопасный importer.

## Безопасность CAN

Verify работает только с файлом ZIP. CAN channel не открывается, PCAN write/transmit не вызывается, никакие CAN frames не передаются.
