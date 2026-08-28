namespace CraneCAN.Core.Profiles;

public static class BuiltInProfiles
{
    public static IReadOnlyList<CanSystemProfile> All { get; } =
    [
        CreateOnk160S02()
    ];

    private static CanSystemProfile CreateOnk160S02() => new(
        "onk160s-02-ks55727",
        "ОНК-160С-02 / КС-55727",
        "Собственный UART 38400, 8E1, LSB first через TJA1050 (не CAN 2.0)",
        38_400,
        [38_400],
        OnkNodes(),
        OnkDiagnostics(),
        [
            new(701, "Включение рабочих движений"),
            new(702, "Поворот расторможен"),
            new(703, "Ограничение подъема крюка"),
            new(704, "Ограничение сматывания каната"),
            new(705, "Ускоренная работа лебедки"),
            new(706, "Вторая секция стрелы втянута"),
            new(707, "Дополнительный противовес установлен"),
            new(708, "Начало выдвижения третьей и четвертой секций"),
            new(801, "Подъем груза"),
            new(802, "Опускание груза"),
            new(803, "Поворот влево"),
            new(804, "Поворот вправо"),
            new(805, "Опускание стрелы"),
            new(806, "Подъем стрелы"),
            new(807, "Выдвижение секций стрелы"),
            new(808, "Втягивание секций стрелы")
        ],
        "Подтверждено осциллограммами стенда 25.08.2026. CAN-H: контакт 6; CAN-L: контакт 7; GND: контакт 4. Линия: 60±5 Ом. Для приема нужен TJA1050 в Silent mode и USB-UART; PCAN-USB этот формат не декодирует.");


    private static IReadOnlyList<CanNodeDefinition> OnkNodes() =>
    [
        new(30, "Датчик давления поршневой полости", "Documented logical address"),
        new(31, "Датчик давления штоковой полости", "Documented logical address"),
        new(32, "Датчик давления напорной магистрали P1", "Documented logical address"),
        new(33, "Датчик давления напорной магистрали P2", "Documented logical address")
    ];

    private static IReadOnlyList<DiagnosticCodeDefinition> OnkDiagnostics() =>
    [
        new("E10", "Датчик вылета/длины или его цепь"),
        new("E30-E33", "Цифровые датчики давления или совпадение адресов"),
        new("E40/E41", "Датчик азимута"),
        new("E53/E55", "Контроллер оголовка стрелы"),
        new("E57", "Контроллер неповоротной части"),
        new("E59", "Отказ реле контроллера неповоротной части"),
        new("E63", "Неисправность линии CAN-H/CAN-L"),
        new("E78", "Длина стрелы"),
        new("E79", "Угол стрелы"),
        new("E80", "Азимут"),
        new("E81/E82", "Продольный/поперечный крен"),
        new("E83", "Концевой выключатель подъема крюка")
    ];
}
