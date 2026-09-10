using System.Globalization;
using System.Runtime.CompilerServices;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Live;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;

internal static class IncidentSignatureReportTests
{
    [ModuleInitializer]
    public static void Run()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("uk-UA");

            var start = new DateTimeOffset(2026, 9, 10, 6, 0, 0, TimeSpan.Zero);
            var ids = new[]
            {
                Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Guid.Parse("33333333-3333-3333-3333-333333333333")
            };

            var privateRoot = Path.Combine(Path.GetTempPath(), "CraneCAN-private-signature");
            var packages = new[]
            {
                Package(
                    Path.Combine(privateRoot, "good-1.canincident"),
                    Path.Combine(privateRoot, "raw-private-1.trc"),
                    ids[0],
                    start,
                    "boom|extend\nhold",
                    new IncidentSource(
                        "peak-pcan-basic",
                        "pcan-usb:0051",
                        250_000,
                        Path.Combine(privateRoot, "replay-private-1.trc"),
                        Path.Combine(privateRoot, "continuous-private-1.trc"),
                        ListenOnlyConfirmed: true)),
                Package(
                    Path.Combine(privateRoot, "good-2.canincident"),
                    Path.Combine(privateRoot, "raw-private-2.trc"),
                    ids[1],
                    start.AddMinutes(1),
                    "boom extend",
                    new IncidentSource(
                        "peak-pcan-basic",
                        "pcan-usb:0051",
                        250_000,
                        null,
                        Path.Combine(privateRoot, "continuous-private-2.trc"),
                        ListenOnlyConfirmed: true)),
                Package(
                    Path.Combine(privateRoot, "good-3.canincident"),
                    Path.Combine(privateRoot, "raw-private-3.trc"),
                    ids[2],
                    start.AddMinutes(2),
                    "boom extend",
                    new IncidentSource(
                        "peak-pcan-basic",
                        "pcan-usb:0051",
                        250_000,
                        null,
                        null,
                        ListenOnlyConfirmed: true),
                    ["SHORT_PREHISTORY"])
            };

            var high = new IncidentSignatureCandidate(
                0x18F,
                false,
                1,
                IncidentTransitionKind.ByteChanged,
                IncidentSignaturePriority.High,
                3,
                3,
                100,
                120,
                100,
                130.5,
                30.5,
                "0x00",
                "0x02",
                3,
                100,
                1,
                ["Joystick|EXTEND", "Valve\nEnable"],
                "repeatable|step\nsecond");

            var medium = new IncidentSignatureCandidate(
                0x0CFF5321,
                true,
                2,
                IncidentTransitionKind.ByteChanged,
                IncidentSignaturePriority.Medium,
                2,
                3,
                200.0 / 3.0,
                -75.25,
                -100,
                -50.5,
                49.5,
                "0x10",
                "0x20",
                2,
                100,
                0,
                [],
                "extended candidate");

            var result = new IncidentSignatureAnalysisResult(
                3,
                [high, medium],
                ["warning|one\nnext"]);

            var profile = new MachineProfile
            {
                ProfileId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                MachineName = "SOOSAN|JK1200A",
                Manufacturer = "SOOSAN",
                Model = "JK1200A",
                CanBusName = "HCH2",
                Bitrate = 250_000,
                KnownSignals =
                [
                    new MachineSignal
                    {
                        Name = "Known",
                        CanId = 0x18F,
                        StartByte = 1,
                        BitLength = 8,
                        Confidence = CraneCAN.Core.Guided.SignalKnowledgeState.Confirmed
                    }
                ],
                ExperimentalSignals =
                [
                    new MachineSignal
                    {
                        Name = "Experimental",
                        CanId = 0x0CFF5321,
                        IsExtended = true,
                        StartByte = 2,
                        BitLength = 8
                    }
                ]
            };

            var generated = start.AddMilliseconds(123);
            var markdown = IncidentSignatureReportCodec.BuildMarkdown(
                packages,
                result,
                profile,
                generated);

            Check(markdown.Contains(
                      "Сформировано UTC: 2026-09-10 06:00:00.123",
                      StringComparison.Ordinal) &&
                  markdown.Contains("+0.120 s", StringComparison.Ordinal) &&
                  markdown.Contains("+0.100 s", StringComparison.Ordinal) &&
                  markdown.Contains("30.5 ms", StringComparison.Ordinal) &&
                  markdown.Contains("-0.075 s", StringComparison.Ordinal) &&
                  !markdown.Contains("+0,120 s", StringComparison.Ordinal) &&
                  !markdown.Contains("30,5 ms", StringComparison.Ordinal),
                "Portable signature report formatting is culture-sensitive.");

            Check(markdown.Contains("good-1.canincident", StringComparison.Ordinal) &&
                  markdown.Contains("good-2.canincident", StringComparison.Ordinal) &&
                  markdown.Contains("good-3.canincident", StringComparison.Ordinal) &&
                  !markdown.Contains(privateRoot, StringComparison.OrdinalIgnoreCase) &&
                  !markdown.Contains("raw-private", StringComparison.OrdinalIgnoreCase) &&
                  !markdown.Contains("replay-private", StringComparison.OrdinalIgnoreCase) &&
                  !markdown.Contains("continuous-private", StringComparison.OrdinalIgnoreCase),
                "Portable signature report leaked an absolute/raw/replay/continuous path.");

            Check(markdown.Contains("boom\\|extend<br>hold", StringComparison.Ordinal) &&
                  markdown.Contains("warning\\|one<br>next", StringComparison.Ordinal) &&
                  markdown.Contains("Joystick\\|EXTEND; Valve<br>Enable", StringComparison.Ordinal) &&
                  markdown.Contains("repeatable\\|step<br>second", StringComparison.Ordinal),
                "Markdown escaping is incorrect.");

            Check(markdown.Contains("0x18F", StringComparison.Ordinal) &&
                  markdown.Contains("Standard", StringComparison.Ordinal) &&
                  markdown.Contains("0x0CFF5321", StringComparison.Ordinal) &&
                  markdown.Contains("Extended", StringComparison.Ordinal) &&
                  markdown.Contains("3/3 (100%)", StringComparison.Ordinal) &&
                  markdown.Contains("2/3 (66.7%)", StringComparison.Ordinal) &&
                  markdown.Contains("LISTEN ONLY", StringComparison.Ordinal) &&
                  markdown.Contains("SOOSAN\\|JK1200A", StringComparison.Ordinal) &&
                  markdown.Contains("Known signals: 1", StringComparison.Ordinal) &&
                  markdown.Contains("Experimental signals: 1", StringComparison.Ordinal),
                "Signature report lost core series/candidate/profile metadata.");

            Check(markdown.Contains("CAN Tx отсутствует", StringComparison.Ordinal) &&
                  markdown.Contains("не доказывает физическую причинность", StringComparison.Ordinal) &&
                  markdown.Contains("не идентифицирует автоматически неисправный ECU", StringComparison.Ordinal),
                "Signature report lost read-only/causality limitations.");

            var output = Path.Combine(
                Path.GetTempPath(),
                $"cranecan-signature-report-{Guid.NewGuid():N}.md");
            try
            {
                IncidentSignatureReportCodec.SaveAsync(
                        output,
                        packages,
                        result,
                        profile,
                        generated)
                    .GetAwaiter()
                    .GetResult();

                var saved = File.ReadAllText(output);
                Check(saved == markdown,
                    "Saved signature Markdown differs from BuildMarkdown output.");
                var bytes = File.ReadAllBytes(output);
                Check(bytes.Length < 3 ||
                      !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
                    "Portable signature report unexpectedly contains UTF-8 BOM.");
            }
            finally
            {
                if (File.Exists(output))
                    File.Delete(output);
            }

            var duplicateRejected = false;
            try
            {
                IncidentSignatureReportCodec.BuildMarkdown(
                    [packages[0], packages[1], packages[1]],
                    result,
                    profile,
                    generated);
            }
            catch (ArgumentException)
            {
                duplicateRejected = true;
            }

            Check(duplicateRejected,
                "Signature report accepted duplicate Incident IDs.");

            var countMismatchRejected = false;
            try
            {
                IncidentSignatureReportCodec.BuildMarkdown(
                    packages.Take(2).ToArray(),
                    result,
                    profile,
                    generated);
            }
            catch (ArgumentException)
            {
                countMismatchRejected = true;
            }

            Check(countMismatchRejected,
                "Signature report accepted package/result incident-count mismatch.");

            var candidateCountMismatchRejected = false;
            try
            {
                IncidentSignatureReportCodec.BuildMarkdown(
                    packages,
                    result with
                    {
                        Candidates =
                        [
                            high with { IncidentCount = 4 }
                        ]
                    },
                    profile,
                    generated);
            }
            catch (ArgumentException)
            {
                candidateCountMismatchRejected = true;
            }

            Check(candidateCountMismatchRejected,
                "Signature report accepted a candidate calculated for another incident count.");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    private static LoadedIncidentPackage Package(
        string metadataPath,
        string rawTracePath,
        Guid incidentId,
        DateTimeOffset start,
        string markerLabel,
        IncidentSource source,
        IReadOnlyList<string>? qualityCodes = null)
    {
        var quality = qualityCodes ?? [];
        var incident = new PreFaultIncident(
            incidentId,
            start,
            start.AddSeconds(15),
            start.AddSeconds(15),
            [new IncidentMarker(start.AddSeconds(10), markerLabel)],
            [],
            quality);

        return new LoadedIncidentPackage(
            metadataPath,
            rawTracePath,
            incident,
            source,
            source.DriverId == "pcan-trc-replay" ? "replay" : "liveOrUnknown",
            start.AddSeconds(16),
            "test");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
