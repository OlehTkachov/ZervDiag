using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;

namespace CraneCAN.Core.Storage;

public enum DbcSignalValueType
{
    Integer,
    Float32,
    Float64
}

public sealed record DbcSignalDefinition
{
    public string Name { get; init; } = string.Empty;
    public int StartBit { get; init; }
    public int BitLength { get; init; }
    public SignalByteOrder ByteOrder { get; init; }
    public bool IsSigned { get; init; }
    public double Factor { get; init; } = 1;
    public double Offset { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; }
    public string Unit { get; init; } = string.Empty;
    public string Multiplexing { get; init; } = string.Empty;
    public IReadOnlyList<string> Receivers { get; init; } = [];
    public string Comment { get; init; } = string.Empty;
    public int? Spn { get; init; }
    public DbcSignalValueType ValueType { get; init; } = DbcSignalValueType.Integer;
    public IReadOnlyList<SignalEnumState> EnumStates { get; init; } = [];
}

public sealed record DbcMessageDefinition
{
    public uint RawDbcId { get; init; }
    public uint CanId { get; init; }
    public bool IsExtended { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Dlc { get; init; }
    public string Sender { get; init; } = string.Empty;
    public bool IsJ1939 { get; init; }
    public J1939Identifier? J1939 { get; init; }
    public IReadOnlyList<DbcSignalDefinition> Signals { get; init; } = [];
}

public sealed record DbcDatabase(
    string SourceName,
    IReadOnlyList<DbcMessageDefinition> Messages,
    IReadOnlyList<string> Warnings)
{
    public int SignalCount => Messages.Sum(message => message.Signals.Count);
    public int J1939MessageCount => Messages.Count(message => message.IsJ1939);
}

public static class DbcCodec
{
    private const string NumberPattern =
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";

    private static readonly Regex MessageRegex = new(
        @"^\s*BO_\s+(?<id>\d+)\s+(?<name>[^\s:]+)\s*:\s*(?<dlc>\d+)\s*(?<sender>\S*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SignalRegex = new(
        @"^\s*SG_\s+(?<name>[^\s:]+)(?:\s+(?<mux>M|m\d+(?:M)?))?\s*:\s*" +
        @"(?<start>\d+)\|(?<length>\d+)@(?<order>[01])(?<sign>[+-])\s*" +
        @"\(\s*(?<factor>" + NumberPattern + @")\s*,\s*(?<offset>" + NumberPattern + @")\s*\)\s*" +
        @"\[\s*(?<min>" + NumberPattern + @")\s*\|\s*(?<max>" + NumberPattern + @")\s*\]\s*" +
        @"""(?<unit>(?:[^""\\]|\\.)*)""\s*(?<receivers>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SignalCommentRegex = new(
        @"^\s*CM_\s+SG_\s+(?<id>\d+)\s+(?<signal>[^\s]+)\s+""(?<text>(?:[^""\\]|\\.)*)""\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ValueTableRegex = new(
        @"^\s*VAL_\s+(?<id>\d+)\s+(?<signal>[^\s]+)\s+(?<pairs>.+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ValuePairRegex = new(
        @"(?<value>-?\d+)\s+""(?<name>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SpnAttributeRegex = new(
        @"^\s*BA_\s+""SPN""\s+SG_\s+(?<id>\d+)\s+(?<signal>[^\s]+)\s+(?<spn>-?\d+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SignalValueTypeRegex = new(
        @"^\s*SIG_VALTYPE_\s+(?<id>\d+)\s+(?<signal>[^\s]+)\s*:\s*(?<type>[012])\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VFrameFormatDefinitionRegex = new(
        @"^\s*BA_DEF_\s+BO_\s+""VFrameFormat""\s+ENUM\s+(?<values>.+)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex QuotedValueRegex = new(
        @"""(?<value>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VFrameFormatAttributeRegex = new(
        @"^\s*BA_\s+""VFrameFormat""\s+BO_\s+(?<id>\d+)\s+(?<value>.+?)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProtocolTypeRegex = new(
        @"^\s*BA_\s+""ProtocolType""\s+""(?<value>(?:[^""\\]|\\.)*)""\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<DbcDatabase> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken)
            .ConfigureAwait(false);
        var sourceName = PortableFileName(path);

        string text;
        string? encodingWarning = null;
        try
        {
            text = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
            encodingWarning =
                "DBC не является корректным UTF-8; применён однобайтный Latin-1 fallback. " +
                "Проверьте не-ASCII имена/комментарии перед инженерным использованием.";
        }

        var database = Parse(text, sourceName);
        if (encodingWarning is null)
            return database;

        return database with
        {
            Warnings = new[] { encodingWarning }
                .Concat(database.Warnings)
                .ToArray()
        };
    }

    public static DbcDatabase Parse(
        string text,
        string sourceName = "memory.dbc")
    {
        ArgumentNullException.ThrowIfNull(text);
        sourceName = PortableFileName(sourceName);

        var warnings = new List<string>();
        var messages = new List<MessageBuilder>();
        var byRawId = new Dictionary<uint, MessageBuilder>();
        MessageBuilder? current = null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];

            var messageMatch = MessageRegex.Match(line);
            if (messageMatch.Success)
            {
                if (!uint.TryParse(
                        messageMatch.Groups["id"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var rawId))
                {
                    warnings.Add($"L{lineNumber}: BO_ message ID не помещается в UInt32.");
                    current = null;
                    continue;
                }

                if (!TryDecodeDbcId(
                        rawId,
                        out var canId,
                        out var isExtended,
                        out var idWarning))
                {
                    warnings.Add($"L{lineNumber}: {idWarning}");
                    current = null;
                    continue;
                }

                if (idWarning is not null)
                    warnings.Add($"L{lineNumber}: {idWarning}");

                if (!int.TryParse(
                        messageMatch.Groups["dlc"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var dlc) ||
                    dlc < 0)
                {
                    warnings.Add($"L{lineNumber}: BO_ DLC некорректен.");
                    current = null;
                    continue;
                }

                if (byRawId.ContainsKey(rawId))
                {
                    warnings.Add(
                        $"L{lineNumber}: повторный BO_ ID {rawId}; второе определение пропущено.");
                    current = null;
                    continue;
                }

                current = new MessageBuilder
                {
                    RawDbcId = rawId,
                    CanId = canId,
                    IsExtended = isExtended,
                    Name = messageMatch.Groups["name"].Value,
                    Dlc = dlc,
                    Sender = messageMatch.Groups["sender"].Value
                };
                messages.Add(current);
                byRawId.Add(rawId, current);
                continue;
            }

            var signalMatch = SignalRegex.Match(line);
            if (signalMatch.Success)
            {
                if (current is null)
                {
                    warnings.Add($"L{lineNumber}: SG_ находится вне BO_ и пропущен.");
                    continue;
                }

                try
                {
                    var startBit = ParseInt(
                        signalMatch.Groups["start"].Value,
                        "StartBit");
                    var bitLength = ParseInt(
                        signalMatch.Groups["length"].Value,
                        "BitLength");
                    var factor = ParseDouble(
                        signalMatch.Groups["factor"].Value,
                        "Factor");
                    var offset = ParseDouble(
                        signalMatch.Groups["offset"].Value,
                        "Offset");
                    var minimum = ParseDouble(
                        signalMatch.Groups["min"].Value,
                        "Minimum");
                    var maximum = ParseDouble(
                        signalMatch.Groups["max"].Value,
                        "Maximum");

                    var receivers = signalMatch.Groups["receivers"].Value
                        .Split(
                            ',',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .Where(value =>
                            !string.Equals(
                                value,
                                "Vector__XXX",
                                StringComparison.Ordinal))
                        .ToArray();

                    current.Signals.Add(new SignalBuilder
                    {
                        Name = signalMatch.Groups["name"].Value,
                        StartBit = startBit,
                        BitLength = bitLength,
                        ByteOrder =
                            signalMatch.Groups["order"].Value == "1"
                                ? SignalByteOrder.LittleEndian
                                : SignalByteOrder.BigEndian,
                        IsSigned =
                            signalMatch.Groups["sign"].Value == "-",
                        Factor = factor,
                        Offset = offset,
                        Minimum = minimum,
                        Maximum = maximum,
                        Unit = UnescapeQuoted(
                            signalMatch.Groups["unit"].Value),
                        Multiplexing =
                            signalMatch.Groups["mux"].Success
                                ? signalMatch.Groups["mux"].Value
                                : string.Empty,
                        Receivers = receivers
                    });
                }
                catch (Exception exception)
                    when (exception is FormatException or OverflowException)
                {
                    warnings.Add(
                        $"L{lineNumber}: SG_ {signalMatch.Groups["name"].Value} пропущен: {exception.Message}");
                }

                continue;
            }

            if (line.TrimStart().StartsWith("SG_", StringComparison.Ordinal))
            {
                warnings.Add(
                    $"L{lineNumber}: SG_ не соответствует поддерживаемому DBC numeric syntax и пропущен.");
            }
        }

        var vFrameFormatValues = ParseVFrameFormatValues(lines);

        for (var index = 0; index < lines.Length; index++)
        {
            var lineNumber = index + 1;
            var line = lines[index];

            ApplySignalComment(
                line,
                lineNumber,
                byRawId,
                warnings);
            ApplyValueTable(
                line,
                lineNumber,
                byRawId,
                warnings);
            ApplySpn(
                line,
                lineNumber,
                byRawId,
                warnings);
            ApplySignalValueType(
                line,
                lineNumber,
                byRawId,
                warnings);
            ApplyVFrameFormat(
                line,
                lineNumber,
                byRawId,
                vFrameFormatValues,
                warnings);
        }

        var globalJ1939 = lines
            .Select(line => ProtocolTypeRegex.Match(line))
            .Where(match => match.Success)
            .Select(match => UnescapeQuoted(match.Groups["value"].Value))
            .Any(value =>
                value.Contains(
                    "J1939",
                    StringComparison.OrdinalIgnoreCase));

        var resultMessages = messages
            .Select(message =>
            {
                var isJ1939 =
                    message.IsJ1939 ||
                    message.Signals.Any(signal => signal.Spn.HasValue) ||
                    (globalJ1939 && message.IsExtended);

                J1939Identifier? j1939 = null;
                if (isJ1939)
                {
                    if (!message.IsExtended)
                    {
                        warnings.Add(
                            $"BO_ {message.Name}: J1939 отмечен для Standard CAN ID; J1939 metadata не создана.");
                        isJ1939 = false;
                    }
                    else
                    {
                        j1939 = J1939IdentifierCodec.Decode(message.CanId);
                    }
                }

                return new DbcMessageDefinition
                {
                    RawDbcId = message.RawDbcId,
                    CanId = message.CanId,
                    IsExtended = message.IsExtended,
                    Name = message.Name,
                    Dlc = message.Dlc,
                    Sender = message.Sender,
                    IsJ1939 = isJ1939,
                    J1939 = j1939,
                    Signals = message.Signals
                        .Select(signal => new DbcSignalDefinition
                        {
                            Name = signal.Name,
                            StartBit = signal.StartBit,
                            BitLength = signal.BitLength,
                            ByteOrder = signal.ByteOrder,
                            IsSigned = signal.IsSigned,
                            Factor = signal.Factor,
                            Offset = signal.Offset,
                            Minimum = signal.Minimum,
                            Maximum = signal.Maximum,
                            Unit = signal.Unit,
                            Multiplexing = signal.Multiplexing,
                            Receivers = signal.Receivers,
                            Comment = signal.Comment,
                            Spn = signal.Spn,
                            ValueType = signal.ValueType,
                            EnumStates = signal.EnumStates
                                .OrderBy(state => state.Value)
                                .ToArray()
                        })
                        .ToArray()
                };
            })
            .ToArray();

        return new DbcDatabase(
            sourceName,
            resultMessages,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static string PortableFileName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "unknown.dbc";

        var normalized = path.Trim().Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index >= 0
            ? normalized[(index + 1)..]
            : normalized;
    }

    private static bool TryDecodeDbcId(
        uint rawId,
        out uint canId,
        out bool isExtended,
        out string? warning)
    {
        warning = null;

        if ((rawId & 0x80000000u) != 0)
        {
            if ((rawId & 0x60000000u) != 0)
            {
                canId = 0;
                isExtended = false;
                warning =
                    $"DBC extended ID 0x{rawId:X8} содержит недопустимые bits 29/30.";
                return false;
            }

            canId = rawId & 0x1FFFFFFFu;
            isExtended = true;
            return true;
        }

        if (rawId <= 0x7FFu)
        {
            canId = rawId;
            isExtended = false;
            return true;
        }

        if (rawId <= 0x1FFFFFFFu)
        {
            canId = rawId;
            isExtended = true;
            warning =
                $"BO_ ID 0x{rawId:X8} трактуется как bare 29-bit Extended ID; " +
                "Vector DBC обычно устанавливает marker bit 31.";
            return true;
        }

        canId = 0;
        isExtended = false;
        warning =
            $"DBC message ID 0x{rawId:X8} не является допустимым Standard/Extended ID.";
        return false;
    }

    private static IReadOnlyList<string> ParseVFrameFormatValues(
        IReadOnlyList<string> lines)
    {
        foreach (var line in lines)
        {
            var match = VFrameFormatDefinitionRegex.Match(line);
            if (!match.Success)
                continue;

            return QuotedValueRegex
                .Matches(match.Groups["values"].Value)
                .Cast<Match>()
                .Select(value =>
                    UnescapeQuoted(
                        value.Groups["value"].Value))
                .ToArray();
        }

        return [];
    }

    private static void ApplySignalComment(
        string line,
        int lineNumber,
        IReadOnlyDictionary<uint, MessageBuilder> byRawId,
        ICollection<string> warnings)
    {
        var match = SignalCommentRegex.Match(line);
        if (!match.Success)
            return;

        if (!TryFindSignal(
                match,
                lineNumber,
                byRawId,
                warnings,
                out var signal))
        {
            return;
        }

        signal!.Comment =
            UnescapeQuoted(match.Groups["text"].Value);
    }

    private static void ApplyValueTable(
        string line,
        int lineNumber,
        IReadOnlyDictionary<uint, MessageBuilder> byRawId,
        ICollection<string> warnings)
    {
        var match = ValueTableRegex.Match(line);
        if (!match.Success)
            return;

        if (!TryFindSignal(
                match,
                lineNumber,
                byRawId,
                warnings,
                out var signal))
        {
            return;
        }

        foreach (Match pair in
                 ValuePairRegex.Matches(
                     match.Groups["pairs"].Value))
        {
            if (!long.TryParse(
                    pair.Groups["value"].Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
            {
                warnings.Add(
                    $"L{lineNumber}: VAL_ значение не помещается в Int64 и пропущено.");
                continue;
            }

            signal!.EnumStates.Add(new SignalEnumState
            {
                Value = value,
                Name = UnescapeQuoted(
                    pair.Groups["name"].Value)
            });
        }
    }

    private static void ApplySpn(
        string line,
        int lineNumber,
        IReadOnlyDictionary<uint, MessageBuilder> byRawId,
        ICollection<string> warnings)
    {
        var match = SpnAttributeRegex.Match(line);
        if (!match.Success)
            return;

        if (!TryFindSignal(
                match,
                lineNumber,
                byRawId,
                warnings,
                out var signal))
        {
            return;
        }

        if (!int.TryParse(
                match.Groups["spn"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var spn) ||
            spn is < 0 or > 524287)
        {
            warnings.Add(
                $"L{lineNumber}: SPN вне диапазона 0…524287 и пропущен.");
            return;
        }

        signal!.Spn = spn;
    }

    private static void ApplySignalValueType(
        string line,
        int lineNumber,
        IReadOnlyDictionary<uint, MessageBuilder> byRawId,
        ICollection<string> warnings)
    {
        var match = SignalValueTypeRegex.Match(line);
        if (!match.Success)
            return;

        if (!TryFindSignal(
                match,
                lineNumber,
                byRawId,
                warnings,
                out var signal))
        {
            return;
        }

        signal!.ValueType =
            match.Groups["type"].Value switch
            {
                "1" => DbcSignalValueType.Float32,
                "2" => DbcSignalValueType.Float64,
                _ => DbcSignalValueType.Integer
            };
    }

    private static void ApplyVFrameFormat(
        string line,
        int lineNumber,
        IReadOnlyDictionary<uint, MessageBuilder> byRawId,
        IReadOnlyList<string> enumValues,
        ICollection<string> warnings)
    {
        var match = VFrameFormatAttributeRegex.Match(line);
        if (!match.Success)
            return;

        if (!uint.TryParse(
                match.Groups["id"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rawId) ||
            !byRawId.TryGetValue(rawId, out var message))
        {
            warnings.Add(
                $"L{lineNumber}: VFrameFormat ссылается на неизвестный BO_ ID.");
            return;
        }

        var rawValue = match.Groups["value"].Value.Trim();
        string label;

        if (rawValue.Length >= 2 &&
            rawValue[0] == '"' &&
            rawValue[^1] == '"')
        {
            label = UnescapeQuoted(rawValue[1..^1]);
        }
        else if (int.TryParse(
                     rawValue,
                     NumberStyles.Integer,
                     CultureInfo.InvariantCulture,
                     out var index))
        {
            if (index >= 0 && index < enumValues.Count)
            {
                label = enumValues[index];
            }
            else if (index == 3 && enumValues.Count == 0)
            {
                label = "J1939PG";
                warnings.Add(
                    $"L{lineNumber}: VFrameFormat=3 трактуется как типичный Vector J1939PG, " +
                    "поскольку BA_DEF_ enum не найден.");
            }
            else
            {
                return;
            }
        }
        else
        {
            return;
        }

        if (label.Contains(
                "J1939",
                StringComparison.OrdinalIgnoreCase))
        {
            message.IsJ1939 = true;
        }
    }

    private static bool TryFindSignal(
        Match match,
        int lineNumber,
        IReadOnlyDictionary<uint, MessageBuilder> byRawId,
        ICollection<string> warnings,
        out SignalBuilder? signal)
    {
        signal = null;

        if (!uint.TryParse(
                match.Groups["id"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var rawId) ||
            !byRawId.TryGetValue(rawId, out var message))
        {
            warnings.Add(
                $"L{lineNumber}: атрибут SG_ ссылается на неизвестный BO_ ID.");
            return false;
        }

        var signalName = match.Groups["signal"].Value;
        signal = message.Signals.FirstOrDefault(item =>
            string.Equals(
                item.Name,
                signalName,
                StringComparison.Ordinal));
        if (signal is not null)
            return true;

        warnings.Add(
            $"L{lineNumber}: атрибут ссылается на неизвестный SG_ {signalName}.");
        return false;
    }

    private static int ParseInt(
        string value,
        string fieldName)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result))
        {
            throw new FormatException(
                $"{fieldName} не является Int32.");
        }

        return result;
    }

    private static double ParseDouble(
        string value,
        string fieldName)
    {
        if (!double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var result) ||
            !double.IsFinite(result))
        {
            throw new FormatException(
                $"{fieldName} не является конечным числом.");
        }

        return result;
    }

    private static string UnescapeQuoted(string value) =>
        value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);

    private sealed class MessageBuilder
    {
        public uint RawDbcId { get; init; }
        public uint CanId { get; init; }
        public bool IsExtended { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Dlc { get; init; }
        public string Sender { get; init; } = string.Empty;
        public bool IsJ1939 { get; set; }
        public List<SignalBuilder> Signals { get; } = [];
    }

    private sealed class SignalBuilder
    {
        public string Name { get; init; } = string.Empty;
        public int StartBit { get; init; }
        public int BitLength { get; init; }
        public SignalByteOrder ByteOrder { get; init; }
        public bool IsSigned { get; init; }
        public double Factor { get; init; } = 1;
        public double Offset { get; init; }
        public double Minimum { get; init; }
        public double Maximum { get; init; }
        public string Unit { get; init; } = string.Empty;
        public string Multiplexing { get; init; } = string.Empty;
        public IReadOnlyList<string> Receivers { get; init; } = [];
        public string Comment { get; set; } = string.Empty;
        public int? Spn { get; set; }
        public DbcSignalValueType ValueType { get; set; } =
            DbcSignalValueType.Integer;
        public List<SignalEnumState> EnumStates { get; } = [];
    }
}
