using System.Globalization;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Protocols;
using CraneCAN.Core.Storage;

namespace CraneCAN.Core.Profiles;

public sealed record DbcMachineProfileImportResult(
    MachineProfile Profile,
    int ImportedSignals,
    int EvidenceAdded,
    int DuplicateEvidenceSkipped,
    int SkippedMultiplexedSignals,
    int SkippedFloatingPointSignals,
    int SkippedCanFdSignals,
    int ConflictSignals,
    IReadOnlyList<string> Warnings)
{
    public int ChangedSignals => ImportedSignals + EvidenceAdded;
}

public static class DbcMachineProfileImporter
{
    public static DbcMachineProfileImportResult Import(
        MachineProfile profile,
        DbcDatabase database,
        string? sourceReference = null,
        DateTimeOffset? recordedAt = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(database);

        var sourceName = DbcCodec.PortableFileName(
            string.IsNullOrWhiteSpace(sourceReference)
                ? database.SourceName
                : sourceReference!);
        var timestamp = recordedAt ?? DateTimeOffset.UtcNow;

        var known = profile.KnownSignals.ToList();
        var experimental = profile.ExperimentalSignals.ToList();
        var warnings = new List<string>(database.Warnings);

        var imported = 0;
        var evidenceAdded = 0;
        var duplicateEvidenceSkipped = 0;
        var skippedMultiplexed = 0;
        var skippedFloatingPoint = 0;
        var skippedCanFd = 0;
        var conflicts = 0;

        foreach (var message in database.Messages)
        {
            if (message.Dlc is < 0 or > 8)
            {
                skippedCanFd += message.Signals.Count;
                warnings.Add(
                    $"BO_ {message.Name}: DLC {message.Dlc} не поддерживается текущим Classical CAN decoder; " +
                    $"{message.Signals.Count} signal(s) не импортированы.");
                continue;
            }

            foreach (var dbcSignal in message.Signals)
            {
                if (!string.IsNullOrWhiteSpace(dbcSignal.Multiplexing) &&
                    !string.Equals(
                        dbcSignal.Multiplexing,
                        "M",
                        StringComparison.Ordinal))
                {
                    skippedMultiplexed++;
                    warnings.Add(
                        $"BO_ {message.Name} / SG_ {dbcSignal.Name}: conditional multiplexing " +
                        $"«{dbcSignal.Multiplexing}» пока не поддерживается Machine Profile и сигнал пропущен.");
                    continue;
                }

                if (dbcSignal.ValueType != DbcSignalValueType.Integer)
                {
                    skippedFloatingPoint++;
                    warnings.Add(
                        $"BO_ {message.Name} / SG_ {dbcSignal.Name}: SIG_VALTYPE_={dbcSignal.ValueType}; " +
                        "float/double пока не импортируется в integer MachineSignal decoder.");
                    continue;
                }

                var signal = BuildSignal(
                    message,
                    dbcSignal,
                    sourceName,
                    timestamp);

                try
                {
                    MachineSignalDecoder.ValidateDefinition(signal);
                    var requiredLength =
                        MachineSignalDecoder.RequiredDataLength(signal);
                    if (requiredLength > message.Dlc)
                    {
                        throw new InvalidDataException(
                            $"поле требует DATA length {requiredLength}, BO_ DLC={message.Dlc}");
                    }
                }
                catch (Exception exception)
                    when (exception is
                        ArgumentException or
                        InvalidDataException or
                        NotSupportedException or
                        OverflowException)
                {
                    conflicts++;
                    warnings.Add(
                        $"BO_ {message.Name} / SG_ {dbcSignal.Name}: определение не импортировано: {exception.Message}");
                    continue;
                }

                var all = known.Concat(experimental).ToArray();
                var sameGeometry = all.FirstOrDefault(existing =>
                    existing.CanId == signal.CanId &&
                    existing.IsExtended == signal.IsExtended &&
                    existing.StartByte == signal.StartByte &&
                    existing.StartBit == signal.StartBit &&
                    existing.BitLength == signal.BitLength);

                if (sameGeometry is not null)
                {
                    if (!SemanticsAgree(sameGeometry, signal))
                    {
                        conflicts++;
                        warnings.Add(
                            BuildConflictWarning(
                                message,
                                dbcSignal,
                                sameGeometry,
                                signal));
                        continue;
                    }

                    var evidence = signal.Evidence.Single();
                    var duplicate = sameGeometry.Evidence.Any(existing =>
                        existing.Kind == evidence.Kind &&
                        string.Equals(
                            existing.SourceReference,
                            evidence.SourceReference,
                            StringComparison.Ordinal));

                    if (duplicate)
                    {
                        duplicateEvidenceSkipped++;
                        continue;
                    }

                    var updated = sameGeometry with
                    {
                        Protocol = ResolveProtocol(
                            sameGeometry.Protocol,
                            signal.Protocol),
                        J1939Pgn =
                            sameGeometry.J1939Pgn ??
                            signal.J1939Pgn,
                        J1939Spn =
                            sameGeometry.J1939Spn ??
                            signal.J1939Spn,
                        Evidence = sameGeometry.Evidence
                            .Append(evidence)
                            .ToList(),
                        UpdatedAt = timestamp
                    };

                    ReplaceSignal(
                        known,
                        experimental,
                        sameGeometry.SignalId,
                        updated);
                    evidenceAdded++;
                    continue;
                }

                experimental.Add(signal);
                imported++;
            }
        }

        var updatedProfile = profile with
        {
            KnownSignals = known,
            ExperimentalSignals = experimental,
            UpdatedAt = timestamp,
            ProgramVersion = "0.7.0"
        };

        return new DbcMachineProfileImportResult(
            updatedProfile,
            imported,
            evidenceAdded,
            duplicateEvidenceSkipped,
            skippedMultiplexed,
            skippedFloatingPoint,
            skippedCanFd,
            conflicts,
            warnings.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static MachineSignal BuildSignal(
        DbcMessageDefinition message,
        DbcSignalDefinition dbcSignal,
        string sourceName,
        DateTimeOffset timestamp)
    {
        var startByte = dbcSignal.StartBit / 8;
        var startBit = dbcSignal.StartBit % 8;
        var isJ1939 =
            message.IsJ1939 &&
            message.J1939 is not null;
        var evidenceKind = isJ1939
            ? EvidenceKind.J1939Database
            : EvidenceKind.Dbc;

        var sourceReference =
            $"DBC:{sourceName}#BO={message.Name};SG={dbcSignal.Name}";
        var j1939Description = isJ1939
            ? $" PGN {message.J1939!.PgnHex}" +
              (dbcSignal.Spn.HasValue
                  ? $"; SPN {dbcSignal.Spn.Value.ToString(CultureInfo.InvariantCulture)}"
                  : string.Empty) +
              $"; priority {message.J1939.Priority}; SA {message.J1939.SourceAddressHex}" +
              (message.J1939.DestinationAddress.HasValue
                  ? $"; DA {message.J1939.DestinationAddressHex}"
                  : string.Empty) +
              "."
            : string.Empty;

        var evidence = new SignalEvidence
        {
            Kind = evidenceKind,
            CaptureOrigin = "document",
            Description =
                $"Импортировано из DBC {sourceName}: BO_ {message.Name}, SG_ {dbcSignal.Name}." +
                j1939Description +
                " DBC/J1939 evidence не переводит сигнал в CONFIRMED автоматически.",
            SourceReference = sourceReference,
            RecordedAt = timestamp
        };

        var notes = new List<string>
        {
            $"DBC message={message.Name}",
            $"DLC={message.Dlc.ToString(CultureInfo.InvariantCulture)}"
        };
        if (!string.IsNullOrWhiteSpace(message.Sender))
            notes.Add($"sender={message.Sender}");
        if (dbcSignal.Receivers.Count > 0)
            notes.Add("receivers=" + string.Join(",", dbcSignal.Receivers));
        notes.Add(
            $"range=[{FormatNumber(dbcSignal.Minimum)}|{FormatNumber(dbcSignal.Maximum)}]");
        if (string.Equals(
                dbcSignal.Multiplexing,
                "M",
                StringComparison.Ordinal))
        {
            notes.Add("multiplexer");
        }

        return new MachineSignal
        {
            Name = dbcSignal.Name,
            Description = string.IsNullOrWhiteSpace(dbcSignal.Comment)
                ? $"DBC signal {message.Name}.{dbcSignal.Name}"
                : dbcSignal.Comment,
            CanId = message.CanId,
            IsExtended = message.IsExtended,
            StartByte = startByte,
            StartBit = startBit,
            BitLength = dbcSignal.BitLength,
            ByteOrder = dbcSignal.ByteOrder,
            IsSigned = dbcSignal.IsSigned,
            Scale = dbcSignal.Factor,
            Offset = dbcSignal.Offset,
            Unit = dbcSignal.Unit,
            EnumStates = dbcSignal.EnumStates.ToList(),
            Confidence = SignalKnowledgeState.Probable,
            Evidence = [evidence],
            Source = $"DBC import: {sourceName}",
            Notes = string.Join("; ", notes),
            Protocol = isJ1939 ? "J1939" : "CAN",
            J1939Pgn = isJ1939
                ? message.J1939!.Pgn
                : null,
            J1939Spn = isJ1939
                ? dbcSignal.Spn
                : null,
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
    }

    private static bool SemanticsAgree(
        MachineSignal existing,
        MachineSignal imported)
    {
        return existing.ByteOrder == imported.ByteOrder &&
               existing.IsSigned == imported.IsSigned &&
               NearlyEqual(existing.Scale, imported.Scale) &&
               NearlyEqual(existing.Offset, imported.Offset) &&
               UnitAgree(existing.Unit, imported.Unit) &&
               NullableAgree(existing.J1939Pgn, imported.J1939Pgn) &&
               NullableAgree(existing.J1939Spn, imported.J1939Spn);
    }

    private static string BuildConflictWarning(
        DbcMessageDefinition message,
        DbcSignalDefinition dbcSignal,
        MachineSignal existing,
        MachineSignal imported) =>
        $"BO_ {message.Name} / SG_ {dbcSignal.Name}: в Machine Profile уже есть поле " +
        $"ID 0x{existing.CanId:X} DATA[{existing.StartByte}] bit {existing.StartBit}, " +
        $"{existing.BitLength} bit, но engineering semantics отличаются. " +
        $"Profile сохранён без автоматической замены: existing {existing.ByteOrder}, " +
        $"{(existing.IsSigned ? "signed" : "unsigned")}, scale {FormatNumber(existing.Scale)}, " +
        $"offset {FormatNumber(existing.Offset)}, unit «{existing.Unit}»; DBC {imported.ByteOrder}, " +
        $"{(imported.IsSigned ? "signed" : "unsigned")}, scale {FormatNumber(imported.Scale)}, " +
        $"offset {FormatNumber(imported.Offset)}, unit «{imported.Unit}».";

    private static void ReplaceSignal(
        IList<MachineSignal> known,
        IList<MachineSignal> experimental,
        Guid signalId,
        MachineSignal replacement)
    {
        for (var index = 0; index < known.Count; index++)
        {
            if (known[index].SignalId != signalId)
                continue;

            known[index] = replacement;
            return;
        }

        for (var index = 0; index < experimental.Count; index++)
        {
            if (experimental[index].SignalId != signalId)
                continue;

            experimental[index] = replacement;
            return;
        }

        throw new InvalidOperationException(
            "Не удалось обновить существующий Machine Profile signal.");
    }

    private static string ResolveProtocol(
        string existing,
        string imported)
    {
        if (string.Equals(
                imported,
                "J1939",
                StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(existing) ||
             string.Equals(
                 existing,
                 "CAN",
                 StringComparison.OrdinalIgnoreCase)))
        {
            return "J1939";
        }

        return existing;
    }

    private static bool NullableAgree(
        int? left,
        int? right) =>
        !left.HasValue ||
        !right.HasValue ||
        left.Value == right.Value;

    private static bool UnitAgree(
        string left,
        string right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
        {
            return true;
        }

        return string.Equals(
            left.Trim(),
            right.Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool NearlyEqual(
        double left,
        double right)
    {
        var scale = Math.Max(
            1d,
            Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= 1e-12 * scale;
    }

    private static string FormatNumber(double value) =>
        value.ToString("0.###############", CultureInfo.InvariantCulture);
}
