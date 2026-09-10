using System.Runtime.CompilerServices;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;
using CraneCAN.Core.Storage;

internal static class DbcJ1939Tests
{
    [ModuleInitializer]
    internal static void Run()
    {
        J1939IdentifierPdu2();
        J1939IdentifierPdu1();
        DbcParsingAndImport();
        ExistingCandidateKeepsStatus();
        ConflictingEngineeringDefinitionIsNotOverwritten();
        J1939SpnDecodeAllowsSourceAddressVariation();
        J1939Pdu1KeepsDestinationAddressFixed();
    }

    private static void J1939IdentifierPdu2()
    {
        var id = J1939IdentifierCodec.Decode(0x18F004AB);

        Check(
            id.Priority == 6 &&
            id.Pgn == 0x0F004 &&
            id.PduFormat == 0xF0 &&
            id.PduSpecific == 0x04 &&
            id.SourceAddress == 0xAB &&
            !id.IsPdu1 &&
            !id.DestinationAddress.HasValue,
            "J1939 PDU2 identifier decode is incorrect.");
    }

    private static void J1939IdentifierPdu1()
    {
        var id = J1939IdentifierCodec.Decode(0x18EAFF3D);

        Check(
            id.Priority == 6 &&
            id.Pgn == 0x0EA00 &&
            id.PduFormat == 0xEA &&
            id.PduSpecific == 0xFF &&
            id.SourceAddress == 0x3D &&
            id.IsPdu1 &&
            id.DestinationAddress == 0xFF,
            "J1939 PDU1 PGN/destination decode is incorrect.");
    }

    private static void DbcParsingAndImport()
    {
        var database = DbcCodec.Parse(
            SampleDbc(),
            @"C:\private\oem\EEC1.dbc");

        Check(
            database.SourceName == "EEC1.dbc",
            "DBC source name leaked or was not normalized.");
        Check(
            database.Messages.Count == 1 &&
            database.SignalCount == 5 &&
            database.J1939MessageCount == 1,
            "DBC message/signal/J1939 counts are incorrect.");

        var message = database.Messages.Single();
        Check(
            message.RawDbcId == 2565866496u &&
            message.CanId == 0x18F00400u &&
            message.IsExtended &&
            message.IsJ1939 &&
            message.J1939?.Pgn == 0x0F004,
            "DBC extended ID or J1939 VFrameFormat decode is incorrect.");

        var engineSpeed = message.Signals.Single(signal =>
            signal.Name == "EngineSpeed");
        Check(
            engineSpeed.StartBit == 24 &&
            engineSpeed.BitLength == 16 &&
            engineSpeed.ByteOrder == SignalByteOrder.LittleEndian &&
            !engineSpeed.IsSigned &&
            Math.Abs(engineSpeed.Factor - 0.125) < 0.0000001 &&
            engineSpeed.Spn == 190 &&
            engineSpeed.Comment == "Engine speed from OEM DBC",
            "DBC SG_/SPN/comment parsing is incorrect.");

        var state = message.Signals.Single(signal =>
            signal.Name == "State");
        Check(
            state.EnumStates.Count == 2 &&
            state.EnumStates[0].Value == 0 &&
            state.EnumStates[0].Name == "Off" &&
            state.EnumStates[1].Value == 1 &&
            state.EnumStates[1].Name == "On",
            "DBC VAL_ parsing is incorrect.");

        var timestamp =
            new DateTimeOffset(
                2026, 9, 10, 9, 0, 0,
                TimeSpan.Zero);
        var result = DbcMachineProfileImporter.Import(
            new MachineProfile
            {
                MachineName = "DBC test"
            },
            database,
            @"D:\secret\customer\EEC1.dbc",
            timestamp);

        Check(
            result.ImportedSignals == 3 &&
            result.SkippedMultiplexedSignals == 1 &&
            result.SkippedFloatingPointSignals == 1 &&
            result.ConflictSignals == 0,
            "DBC importer skip/import counts are incorrect.");

        var imported = result.Profile.ExperimentalSignals
            .Single(signal => signal.Name == "EngineSpeed");
        Check(
            imported.Confidence == SignalKnowledgeState.Probable &&
            imported.CanId == 0x18F00400u &&
            imported.IsExtended &&
            imported.StartByte == 3 &&
            imported.StartBit == 0 &&
            imported.BitLength == 16 &&
            imported.ByteOrder == SignalByteOrder.LittleEndian &&
            Math.Abs(imported.Scale - 0.125) < 0.0000001 &&
            imported.Unit == "rpm" &&
            imported.Protocol == "J1939" &&
            imported.J1939Pgn == 0x0F004 &&
            imported.J1939Spn == 190,
            "Imported MachineSignal engineering/J1939 metadata is incorrect.");

        var evidence = imported.Evidence.Single();
        Check(
            evidence.Kind == EvidenceKind.J1939Database &&
            evidence.SourceReference ==
                "DBC:EEC1.dbc#BO=EEC1;SG=EngineSpeed" &&
            evidence.Description.Contains(
                "PGN 0x0F004",
                StringComparison.Ordinal) &&
            evidence.Description.Contains(
                "SPN 190",
                StringComparison.Ordinal) &&
            !evidence.Description.Contains(
                @"D:\secret",
                StringComparison.OrdinalIgnoreCase) &&
            !imported.Source.Contains(
                @"D:\secret",
                StringComparison.OrdinalIgnoreCase),
            "DBC/J1939 evidence is not portable or lacks PGN/SPN provenance.");
    }

    private static void ExistingCandidateKeepsStatus()
    {
        var database = DbcCodec.Parse(
            SampleDbc(),
            "EEC1.dbc");
        var existing = new MachineSignal
        {
            Name = "Observed engine speed",
            CanId = 0x18F00400u,
            IsExtended = true,
            StartByte = 3,
            StartBit = 0,
            BitLength = 16,
            ByteOrder = SignalByteOrder.LittleEndian,
            IsSigned = false,
            Scale = 0.125,
            Offset = 0,
            Unit = "rpm",
            Confidence = SignalKnowledgeState.Candidate,
            Evidence =
            [
                new SignalEvidence
                {
                    Kind = EvidenceKind.IncidentCapture,
                    SourceReference = "incident:test"
                }
            ]
        };
        var profile = new MachineProfile
        {
            ExperimentalSignals = [existing]
        };

        var result = DbcMachineProfileImporter.Import(
            profile,
            database,
            "EEC1.dbc",
            DateTimeOffset.UnixEpoch);

        var updated = result.Profile.ExperimentalSignals
            .Single(signal =>
                signal.SignalId == existing.SignalId);
        Check(
            updated.Confidence == SignalKnowledgeState.Candidate &&
            updated.Evidence.Any(evidence =>
                evidence.Kind == EvidenceKind.J1939Database) &&
            updated.J1939Pgn == 0x0F004 &&
            updated.J1939Spn == 190,
            "DBC evidence changed existing confidence or failed to enrich J1939 metadata.");

        var second = DbcMachineProfileImporter.Import(
            result.Profile,
            database,
            "EEC1.dbc",
            DateTimeOffset.UnixEpoch.AddSeconds(1));
        var secondUpdated = second.Profile.ExperimentalSignals
            .Single(signal =>
                signal.SignalId == existing.SignalId);
        Check(
            secondUpdated.Evidence.Count(evidence =>
                evidence.Kind == EvidenceKind.J1939Database &&
                evidence.SourceReference ==
                    "DBC:EEC1.dbc#BO=EEC1;SG=EngineSpeed") == 1 &&
            second.DuplicateEvidenceSkipped >= 1,
            "Repeated DBC import duplicated identical evidence.");
    }

    private static void ConflictingEngineeringDefinitionIsNotOverwritten()
    {
        var database = DbcCodec.Parse(
            SampleDbc(),
            "EEC1.dbc");
        var existing = new MachineSignal
        {
            Name = "Raw candidate",
            CanId = 0x18F00400u,
            IsExtended = true,
            StartByte = 3,
            StartBit = 0,
            BitLength = 16,
            ByteOrder = SignalByteOrder.LittleEndian,
            IsSigned = false,
            Scale = 1,
            Offset = 0,
            Unit = "rpm",
            Confidence = SignalKnowledgeState.Candidate
        };
        var profile = new MachineProfile
        {
            ExperimentalSignals = [existing]
        };

        var result = DbcMachineProfileImporter.Import(
            profile,
            database,
            "EEC1.dbc",
            DateTimeOffset.UnixEpoch);
        var preserved = result.Profile.ExperimentalSignals
            .Single(signal =>
                signal.SignalId == existing.SignalId);

        Check(
            Math.Abs(preserved.Scale - 1) < 0.0000001 &&
            preserved.Evidence.All(evidence =>
                evidence.Kind != EvidenceKind.J1939Database) &&
            result.ConflictSignals >= 1 &&
            result.Warnings.Any(warning =>
                warning.Contains(
                    "engineering semantics",
                    StringComparison.OrdinalIgnoreCase)),
            "DBC conflict overwrote existing engineering semantics.");
    }

    private static void J1939SpnDecodeAllowsSourceAddressVariation()
    {
        var database = DbcCodec.Parse(
            SampleDbc(),
            "EEC1.dbc");
        var profile = DbcMachineProfileImporter.Import(
            new MachineProfile(),
            database,
            "EEC1.dbc",
            DateTimeOffset.UnixEpoch).Profile;
        var engineSpeed = profile.ExperimentalSignals
            .Single(signal => signal.J1939Spn == 190);

        var frame = new CanFrame
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Channel = 0,
            Id = 0x18F004ABu,
            IsExtended = true,
            Data =
            [
                0x00, 0x00, 0x00, 0x40,
                0x1F, 0x00, 0x00, 0x00
            ],
            Protocol = BusProtocol.ClassicalCan,
            Direction = CanDirection.Rx
        };

        var decoded = J1939SignalDecoder.Decode(
            engineSpeed,
            frame);

        Check(
            decoded.Identifier.Pgn == 0x0F004 &&
            decoded.Identifier.SourceAddress == 0xAB &&
            decoded.Spn == 190 &&
            decoded.RawUnsigned == 8000 &&
            Math.Abs(decoded.EngineeringValue - 1000) < 0.0000001,
            "J1939 SPN decode or PGN-level source-address matching is incorrect.");
    }

    private static void J1939Pdu1KeepsDestinationAddressFixed()
    {
        var signal = new MachineSignal
        {
            Name = "PDU1 test",
            CanId = 0x18EAFF00u,
            IsExtended = true,
            StartByte = 0,
            StartBit = 0,
            BitLength = 8,
            Protocol = "J1939",
            J1939Pgn = 0x0EA00,
            J1939Spn = 1
        };

        Check(
            J1939SignalDecoder.MatchesIdentifier(
                signal,
                0x18EAFF45u,
                isExtended: true),
            "J1939 PDU1 source-address variation should preserve matching destination.");

        Check(
            !J1939SignalDecoder.MatchesIdentifier(
                signal,
                0x18EA8045u,
                isExtended: true),
            "J1939 PDU1 matcher incorrectly ignored destination address.");
    }

    private static string SampleDbc() =>
        """
        VERSION "CraneCAN test"

        NS_ :
            CM_
            BA_DEF_
            BA_
            VAL_
            SIG_VALTYPE_

        BS_:

        BU_: Engine Display

        BO_ 2565866496 EEC1: 8 Engine
         SG_ EngineSpeed : 24|16@1+ (0.125,0) [0|8031.875] "rpm" Display
         SG_ State : 0|8@1+ (1,0) [0|255] "" Display
         SG_ Mux M : 16|8@1+ (1,0) [0|255] "" Display
         SG_ Conditional m1 : 40|8@1+ (1,0) [0|255] "" Display
         SG_ FloatSignal : 48|32@1+ (1,0) [0|100] "x" Display

        BA_DEF_ BO_ "VFrameFormat" ENUM "StandardCAN","ExtendedCAN","reserved","J1939PG";
        BA_DEF_ SG_ "SPN" INT 0 524287;
        BA_ "VFrameFormat" BO_ 2565866496 3;
        BA_ "SPN" SG_ 2565866496 EngineSpeed 190;
        CM_ SG_ 2565866496 EngineSpeed "Engine speed from OEM DBC";
        VAL_ 2565866496 State 0 "Off" 1 "On";
        SIG_VALTYPE_ 2565866496 FloatSignal : 1;
        """;

    private static void Check(
        bool condition,
        string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
