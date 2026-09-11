namespace CraneCAN.Core.Profiles;

/// <summary>
/// Documented starting profile for the Hirschmann HC4900 / IC4600 LMI system.
///
/// The service manual documents CANopen 2.0B, a 125 kbit/s default bus rate,
/// CANopen Node-IDs and diagnostic error meanings, but it does not document
/// application COB-IDs or DATA byte/bit layouts. Therefore this profile does
/// not invent MachineSignal definitions. Signal mappings must be learned from
/// passive captures and stored as evidence in a MachineProfile.
/// </summary>
public static class Hc4900Profile
{
    public const string ProfileId = "hirschmann-hc4900";
    public const int DefaultBitrate = 125_000;

    // These collections must be initialized before SystemProfile. Static field/property
    // initializers execute in source order; keeping them above SystemProfile prevents
    // null values from being passed to CanSystemProfile during type initialization.
    public static IReadOnlyList<CanNodeDefinition> Nodes { get; } =
    [
        new(1, "IC4600 display", "Documented CAN Bus State Node-ID"),
        new(3, "HC4900 central unit / Mentor", "Documented CAN Bus State Node-ID"),
        new(15, "Length / angle sensor (cable reel CAN converter)", "Documented CAN Bus State Node-ID"),
        new(60, "Piston oil pressure sensor (0x3C)", "Documented Node-ID"),
        new(61, "Rod oil pressure sensor (0x3D)", "Documented Node-ID")
    ];

    public static IReadOnlyList<DiagnosticCodeDefinition> Diagnostics { get; } =
    [
        new("E61", "Ошибка передачи данных CAN для всех CAN-устройств"),
        new("E62", "Ошибка передачи данных CAN узла датчика давления"),
        new("E63", "Внутренняя ошибка CAN-узла датчика давления"),
        new("E64", "Ошибка передачи данных CAN узла датчика длины/угла (cable reel)"),
        new("E94", "Ошибка CAN-связи между HC4900 CU и консолью IC4600")
    ];

    public static CanSystemProfile SystemProfile { get; } = new(
        ProfileId,
        "Hirschmann HC4900 / IC4600",
        "Classical CAN / CANopen 2.0B",
        DefaultBitrate,
        [DefaultBitrate],
        Nodes,
        Diagnostics,
        [],
        "Источник: HC4900 System Service Manual. Документированные CANopen Node-ID: " +
        "1 = IC4600 display, 3 = HC4900 central unit (Mentor), 15 = length/angle sensor, " +
        "60 (0x3C) = piston oil pressure, 61 (0x3D) = rod oil pressure. " +
        "Node-ID не является raw CAN identifier/COB-ID. Руководство указывает default baudrate 125K. " +
        "Перед реальным подключением скорость следует подтвердить на конкретной машине. " +
        "Диагностика CraneCAN для этого профиля остаётся listen-only; CAN Tx не требуется.");

    public static MachineProfile CreateMachineProfile() => new()
    {
        Manufacturer = "Hirschmann",
        Model = "HC4900",
        MachineName = "Hirschmann HC4900",
        Subsystem = "LMI / Load Moment Indicator",
        CanBusName = "HC4900 CAN",
        Bitrate = DefaultBitrate,
        CanType = "Classical CAN / CANopen 2.0B",
        Notes =
            "Шаблон HC4900 по HC4900 System Service Manual. " +
            "CANopen Node-ID: 1 IC4600 display; 3 HC4900 CU/Mentor; 15 length/angle sensor; " +
            "60 (0x3C) piston pressure; 61 (0x3D) rod pressure. " +
            "A2B, основной length/angle, дополнительный 4–20 mA length sensor и wind speed " +
            "поступают в CAN через converter board кабельного барабана; отдельные Node-ID для них " +
            "в использованном руководстве не задокументированы. " +
            "Ошибки связи: E61 all CAN units; E62 pressure transfer; E63 pressure internal; " +
            "E64 length/angle transfer; E94 CU-console. " +
            "KnownSignals намеренно пуст: руководство не задаёт COB-ID/байты прикладных данных. " +
            "Заполняйте сигналы только по повторяемым capture/evidence. Listen-only."
    };
}
