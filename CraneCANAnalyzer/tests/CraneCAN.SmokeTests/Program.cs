using CraneCAN.Core.Analysis;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;
using CraneCAN.Core.Storage;
using System.Text;

var now = DateTimeOffset.UtcNow;
Require(Enum.GetValues<BusProtocol>().Contains(BusProtocol.Onk160Serial),
    "ONK-160 protocol marker missing.");
Require(Enum.GetValues<BusProtocol>().Contains(BusProtocol.ClassicalCan),
    "Classical CAN protocol marker missing.");
Require(BuiltInProfiles.All.Count == 1, "The ONK-160 test profile set must remain unchanged.");
var onk = BuiltInProfiles.All.Single(profile => profile.Id == "onk160s-02-ks55727");
Require(onk.ConfirmedBitrate == 38_400, "ONK-160 bitrate must be 38400 bit/s.");
Require(onk.Protocol.Contains("8E1"), "ONK-160 8E1 profile marker missing.");
Require(onk.Nodes.Any(node => node.Address == 30), "ONK piston pressure address missing.");
Require(onk.DiscreteSignals.Any(signal => signal.Channel == 806 && signal.Meaning.Contains("Подъем стрелы")),
    "ONK channel 806 mapping missing.");

var healthyCycle = Convert.FromHexString(
    "CA6B014B116D" +
    "E63500E4" +
    "E57900A1" +
    "E8B2093900B5FF0000FD00690241C6" +
    "F78187");
var parser = new Onk160PacketParser();
Require(parser.Append(healthyCycle.AsSpan(0, 3), now).Count == 0,
    "Partial ONK packet must remain buffered.");
var parsed = parser.Append(healthyCycle.AsSpan(3), now.AddMilliseconds(1));
Require(parsed.Count == 5, "Expected five ONK packets in the healthy cycle.");
Require(parsed.All(packet => packet.IsChecksumValid == true),
    "Every healthy ONK packet checksum must be valid.");
Require(parsed.Select(packet => packet.Header).SequenceEqual(new byte[] { 0xCA, 0xE6, 0xE5, 0xE8, 0xF7 }),
    "ONK packet order mismatch.");

var e31 = parser.Append(Convert.FromHexString("E6008099"), now.AddMilliseconds(2)).Single();
Require(e31.IsChecksumValid == true, "E31 packet checksum mismatch.");
Require(Onk160Interpreter.Describe(e31.Header, e31.Payload.Span, e31.IsChecksumValid).Contains("E31"),
    "E31 decoder did not identify the rod-side sensor fault.");

var e83 = parser.Append(Convert.FromHexString("F70107"), now.AddMilliseconds(3)).Single();
Require(Onk160Interpreter.Describe(e83.Header, e83.Payload.Span, e83.IsChecksumValid).Contains("E83"),
    "E83 decoder did not identify the hook limit.");

var e55 = parser.Append(Convert.FromHexString("F710F82A"), now.AddMilliseconds(4));
Require(e55.Count == 2 && e55[0].IsChecksumValid == true && e55[1].Header == 0x2A,
    "E55 packet plus raw 0x2A marker was not preserved.");
Require(Onk160Interpreter.Describe(e55[0].Header, e55[0].Payload.Span, e55[0].IsChecksumValid).Contains("E55"),
    "E55 reference marker was not decoded.");

var normalBenchCapture = new Onk160BenchCapture(3);
for (var cycle = 0; cycle < 4; cycle++)
{
    AppendBenchCycle(normalBenchCapture, healthyCycle, now.AddMilliseconds(10 + cycle * 60));
}
Require(normalBenchCapture.IsComplete, "Normal bench capture did not collect three complete cycles.");

var e83Cycle = Convert.FromHexString(
    "CA6B014B116D" +
    "E63500E4" +
    "E57900A1" +
    "E8B2093900B5FF0000FD00690241C6" +
    "F70107");
var changedBenchCapture = new Onk160BenchCapture(3);
for (var cycle = 0; cycle < 4; cycle++)
{
    AppendBenchCycle(changedBenchCapture, e83Cycle, now.AddMilliseconds(300 + cycle * 60));
}
Require(changedBenchCapture.IsComplete, "Changed bench capture did not collect three complete cycles.");

var normalSnapshot = normalBenchCapture.CreateSnapshot();
var changedSnapshot = changedBenchCapture.CreateSnapshot();
var benchComparison = Onk160BenchStateAnalyzer.Compare(normalSnapshot, changedSnapshot);
var f7StatusChange = benchComparison.Single(row =>
    row.Header == 0xF7 && row.DataIndex == 0);
Require(f7StatusChange.NormalValue == 0x81 && f7StatusChange.ChangedValue == 0x01,
    "Bench comparison did not preserve the F7 state transition.");
Require(f7StatusChange.XorMask == 0x80 && f7StatusChange.ChangedBits.Contains("7: 1→0"),
    "Bench comparison did not identify F7 bit 7 for E83.");
Require(f7StatusChange.NormalAgreementPercent == 100 && f7StatusChange.ChangedAgreementPercent == 100,
    "Stable bench values must have 100 percent agreement.");

var onkFrames = parsed.Select(packet => new CanFrame
{
    Timestamp = packet.Timestamp,
    Channel = 7,
    Id = packet.Header,
    Data = packet.Bytes.Skip(1).ToArray(),
    Protocol = BusProtocol.Onk160Serial,
    IsChecksumValid = packet.IsChecksumValid
}).ToArray();
Require(onkFrames.Single(frame => frame.Id == 0xE8).Dlc == 15,
    "The 15-byte E8 packet must be preserved completely.");
foreach (var frame in onkFrames)
{
    frame.Validate();
}

var referenceCan = Enumerable.Range(0, 4).Select(index => new CanFrame
{
    Timestamp = now.AddMilliseconds(1000 + index * 20),
    Channel = 0,
    Id = 0x181,
    Data = new byte[] { 0x10, 0x20, 0x00, 0x40, 0x50, 0x60, 0x70, 0x80 },
    Protocol = BusProtocol.ClassicalCan,
    Direction = CanDirection.Rx
}).ToArray();
var actionCan = Enumerable.Range(0, 4).Select(index => new CanFrame
{
    Timestamp = now.AddMilliseconds(1200 + index * 20),
    Channel = 0,
    Id = 0x181,
    Data = new byte[] { 0x10, 0x20, 0x20, 0x40, 0x50, 0x60, 0x70, 0x80 },
    Protocol = BusProtocol.ClassicalCan,
    Direction = CanDirection.Rx
}).ToArray();
Require(referenceCan.All(frame => frame.Dlc == 8 && frame.IdText == "181"),
    "Classical CAN DLC or standard identifier formatting is incorrect.");
var genericComparison = GenericExperimentComparator.Compare(referenceCan, actionCan);
var genericBitChange = genericComparison.Single(row => row.Id == 0x181 && row.DataIndex == 2);
Require(genericBitChange.ReferenceValue == 0x00 && genericBitChange.ActionValue == 0x20,
    "Generic experiment comparison did not preserve the CAN byte transition.");
Require(genericBitChange.XorMask == 0x20 && genericBitChange.ChangedBits.Contains("5: 0→1"),
    "Generic experiment comparison did not identify CAN bit 5.");
Require(genericBitChange.ReferenceAgreementPercent == 100 && genericBitChange.ActionAgreementPercent == 100,
    "Stable generic CAN values must have 100 percent agreement.");

var onkTemporaryFile = Path.Combine(Path.GetTempPath(), $"onk160-{Guid.NewGuid():N}.csv");
var canTemporaryFile = Path.Combine(Path.GetTempPath(), $"classical-can-{Guid.NewGuid():N}.csv");
var benchTemporaryFile = Path.Combine(Path.GetTempPath(), $"onk160-bench-{Guid.NewGuid():N}.csv");
try
{
    await CanCsvCodec.SaveAsync(onkTemporaryFile, onkFrames);
    var restoredOnk = await CanCsvCodec.LoadAsync(onkTemporaryFile);
    Require(restoredOnk.Count == onkFrames.Length, "ONK CSV packet count mismatch.");
    Require(restoredOnk.All(frame => frame.Protocol == BusProtocol.Onk160Serial),
        "ONK CSV protocol marker was not restored.");
    Require(restoredOnk.All(frame => frame.IsChecksumValid == true),
        "ONK CSV checksum state was not restored.");

    await CanCsvCodec.SaveAsync(canTemporaryFile, referenceCan);
    var restoredCan = await CanCsvCodec.LoadAsync(canTemporaryFile);
    Require(restoredCan.Count == referenceCan.Length &&
            restoredCan.All(frame => frame.Protocol == BusProtocol.ClassicalCan && frame.Dlc == 8),
        "Classical CAN CSV round-trip failed.");

    await Onk160BenchReportCodec.SaveAsync(
        benchTemporaryFile,
        new Onk160BenchReportMetadata(now, "E83", "Разомкнута цепь концевика", "E83",
            "Концевой подъёма крюка", normalSnapshot.CycleCount, changedSnapshot.CycleCount),
        benchComparison);
    var benchReportLines = await File.ReadAllLinesAsync(benchTemporaryFile);
    var f7ReportRow = benchReportLines
        .Select(ParseSemicolonCsvLine)
        .SingleOrDefault(columns => columns.Count >= 7 &&
                                    columns[0] == "F7" &&
                                    columns[1] == "0" &&
                                    columns[2] == "DATA[0]");
    Require(f7ReportRow is not null &&
            f7ReportRow[3] == "81" &&
            f7ReportRow[4] == "01" &&
            f7ReportRow[5] == "80" &&
            f7ReportRow[6] == "7: 1→0",
        "Bench CSV report did not preserve the bit transition.");
}
finally
{
    if (File.Exists(onkTemporaryFile))
    {
        File.Delete(onkTemporaryFile);
    }

    if (File.Exists(canTemporaryFile))
    {
        File.Delete(canTemporaryFile);
    }

    if (File.Exists(benchTemporaryFile))
    {
        File.Delete(benchTemporaryFile);
    }
}

Console.WriteLine("CraneCAN smoke tests passed.");
return;

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AppendBenchCycle(Onk160BenchCapture capture, byte[] cycleBytes, DateTimeOffset timestamp)
{
    var parser = new Onk160PacketParser();
    foreach (var packet in parser.Append(cycleBytes, timestamp))
    {
        capture.Append(new CanFrame
        {
            Timestamp = packet.Timestamp,
            Channel = 0,
            Id = packet.Header,
            Data = packet.Payload.ToArray(),
            Protocol = BusProtocol.Onk160Serial,
            Direction = CanDirection.Rx,
            IsChecksumValid = packet.IsChecksumValid
        });
    }
}

static IReadOnlyList<string> ParseSemicolonCsvLine(string line)
{
    var columns = new List<string>();
    var value = new StringBuilder();
    var insideQuotes = false;

    for (var index = 0; index < line.Length; index++)
    {
        var character = line[index];
        if (character == '"')
        {
            if (insideQuotes && index + 1 < line.Length && line[index + 1] == '"')
            {
                value.Append('"');
                index++;
            }
            else
            {
                insideQuotes = !insideQuotes;
            }
        }
        else if (character == ';' && !insideQuotes)
        {
            columns.Add(value.ToString());
            value.Clear();
        }
        else
        {
            value.Append(character);
        }
    }

    if (insideQuotes)
    {
        throw new FormatException("Unclosed quote in the generated bench CSV report.");
    }

    columns.Add(value.ToString());
    return columns;
}
