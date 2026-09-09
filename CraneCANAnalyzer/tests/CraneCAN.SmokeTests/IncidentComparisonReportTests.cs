using System.Globalization;
using CraneCAN.Core.Analysis;
using CraneCAN.Core.Live;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Storage;

internal static class IncidentComparisonReportTests
{
    public static async Task RunAsync()
    {
        var marker = DateTimeOffset.UnixEpoch.AddSeconds(10);
        var baseline = Package(
            @"C:\private\GOOD\good.canincident",
            @"C:\private\GOOD\capture.trc",
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            marker,
            "PCAN_USBBUS1",
            250_000,
            complete: true,
            qualityCodes: []);

        var current = Package(
            @"D:\secret\FAULT\fault.canincident",
            @"D:\secret\FAULT\capture.trc",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            marker.AddMinutes(1),
            "PCAN_USBBUS1",
            250_000,
            complete: false,
            qualityCodes: ["CAPTURE_UNCERTAIN"]);

        var difference = new IncidentEventChainDifference(
            0x123,
            false,
            2,
            IncidentTransitionKind.ByteChanged,
            IncidentEventChainDifferenceKind.TransitionChanged,
            IncidentEventChainDifferencePriority.High,
            -120,
            30,
            "0x00 → 0x01",
            "0x00 → 0x80",
            ["Joystick|command"],
            "Строка 1\nСтрока 2");

        var result = new IncidentEventChainComparisonResult(
            3,
            4,
            100,
            ["quality|warning"],
            [difference]);

        var profile = new MachineProfile
        {
            ProfileId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            MachineName = "Test | crane",
            Manufacturer = "Maker",
            Model = "Model",
            CanBusName = "CAN1",
            Bitrate = 250_000
        };

        var generatedAt = new DateTimeOffset(
            2026, 9, 9, 9, 30, 0, TimeSpan.Zero);
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        string markdown;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("uk-UA");
            markdown = IncidentComparisonReportCodec.BuildMarkdown(
                baseline,
                current,
                result,
                profile,
                generatedAt);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }

        Check(markdown.Contains("good.canincident", StringComparison.Ordinal) &&
              markdown.Contains("fault.canincident", StringComparison.Ordinal),
            "Portable incident report lost source file names.");

        Check(!markdown.Contains(@"C:\private", StringComparison.OrdinalIgnoreCase) &&
              !markdown.Contains(@"D:\secret", StringComparison.OrdinalIgnoreCase) &&
              !markdown.Contains("capture.trc", StringComparison.OrdinalIgnoreCase),
            "Portable incident report leaked absolute/local trace paths.");

        Check(markdown.Contains("Joystick\\|command", StringComparison.Ordinal) &&
              markdown.Contains("quality\\|warning", StringComparison.Ordinal) &&
              markdown.Contains("Строка 1<br>Строка 2", StringComparison.Ordinal),
            "Markdown table content was not safely escaped.");

        Check(markdown.Contains("-0.120 s", StringComparison.Ordinal) &&
              markdown.Contains("+0.030 s", StringComparison.Ordinal) &&
              markdown.Contains("+150 ms", StringComparison.Ordinal),
            "Incident report timing formatting is incorrect.");

        Check(markdown.Contains("- HIGH: 1", StringComparison.Ordinal) &&
              markdown.Contains("CAPTURE_UNCERTAIN", StringComparison.Ordinal) &&
              markdown.Contains("Test \\| crane", StringComparison.Ordinal),
            "Incident report summary/profile/quality details are incomplete.");

        Check(markdown.Contains("2026-09-09 09:30:00.000", StringComparison.Ordinal),
            "Deterministic report generation timestamp was not used.");

        var folder = Path.Combine(
            Path.GetTempPath(),
            $"cranecan-incident-report-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "comparison.md");
        try
        {
            await IncidentComparisonReportCodec.SaveAsync(
                path,
                baseline,
                current,
                result,
                profile);
            var saved = await File.ReadAllTextAsync(path);
            Check(saved.Contains("# CraneCAN — GOOD/FAULT Incident Chain Comparison", StringComparison.Ordinal) &&
                  saved.Contains("0x123", StringComparison.Ordinal),
                "Incident comparison report file was not written correctly.");
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }

        var noDifference = IncidentComparisonReportCodec.BuildMarkdown(
            baseline,
            current,
            result with { Differences = [] },
            null,
            generatedAt);
        Check(noDifference.Contains("По текущим критериям различий не найдено.", StringComparison.Ordinal) &&
              noDifference.Contains("Machine Profile: не загружен.", StringComparison.Ordinal),
            "Empty-difference/no-profile report was not handled.");
    }

    private static LoadedIncidentPackage Package(
        string metadataPath,
        string rawTracePath,
        Guid incidentId,
        DateTimeOffset marker,
        string channel,
        int bitrate,
        bool complete,
        IReadOnlyList<string> qualityCodes)
    {
        var frames = new[]
        {
            new CraneCAN.Core.Models.CanFrame
            {
                Timestamp = marker.AddSeconds(-1),
                Channel = 0,
                Id = 0x123,
                Data = [0x00, 0x00, 0x00],
                Protocol = CraneCAN.Core.Models.BusProtocol.ClassicalCan,
                Direction = CraneCAN.Core.Models.CanDirection.Rx
            }
        };

        var incident = new PreFaultIncident(
            incidentId,
            marker.AddSeconds(-10),
            marker.AddSeconds(5),
            marker.AddSeconds(5),
            [new IncidentMarker(marker, "fault")],
            frames,
            complete ? [] : qualityCodes);

        var source = new IncidentSource(
            "pcan-usb",
            channel,
            bitrate,
            null,
            rawTracePath,
            0,
            0,
            true);

        return new LoadedIncidentPackage(
            metadataPath,
            rawTracePath,
            incident,
            source,
            "liveOrUnknown",
            marker.AddMinutes(2),
            "Наблюдаемая запись.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
