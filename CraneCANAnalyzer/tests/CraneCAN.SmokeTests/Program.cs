using CraneCAN.Core.Analysis;
using CraneCAN.Core.Drivers;
using CraneCAN.Core.Guided;
using CraneCAN.Core.Live;
using CraneCAN.Core.Models;
using CraneCAN.Core.Profiles;
using CraneCAN.Core.Protocols;
using CraneCAN.Core.Storage;
using CraneCAN.Driver.PcanBasic;
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
Require(genericBitChange.Priority == GenericCanChangePriority.VeryHigh && genericBitChange.IsSignificant,
    "A stable single-bit change must receive very high priority.");

var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "soosan_mixed.trc");
var trcImport = await PcanTrcCodec.LoadWithDiagnosticsAsync(fixturePath, defaultChannel: 2);
Require(trcImport.Frames.Count == 21, "SOOSAN TRC fixture frame count mismatch.");
Require(trcImport.Frames.Select(frame => frame.Timestamp).SequenceEqual(
        trcImport.Frames.Select(frame => frame.Timestamp).OrderBy(value => value)),
    "TRC importer changed the original frame order.");
Require(trcImport.ErrorFramesSkipped == 1 && trcImport.RemoteFramesSkipped == 1 &&
        trcImport.UnknownOrMalformedLines == 1,
    "TRC importer diagnostics did not count error, RTR and unknown rows.");

var requiredHydacIds = new uint[]
{
    0x18012101, 0x0CFF1421, 0x0CFF2221, 0x18800121,
    0x0CFF2121, 0x0CFF3621, 0x0CFF5821, 0x188C0121,
    0x18740121, 0x18720121, 0x0CFF5321, 0x0CF00400
};
foreach (var hydacId in requiredHydacIds)
{
    var hydacFrame = trcImport.Frames.Single(frame => frame.Id == hydacId);
    Require(hydacFrame.IsExtended && hydacFrame.Dlc == 8 && hydacFrame.Channel == 2,
        $"HYDAC extended ID {hydacId:X8} was not preserved.");
}

var standardJchFrames = trcImport.Frames.Where(frame => frame.Id == 0x18F).ToArray();
Require(standardJchFrames.Length == 8 && standardJchFrames.All(frame => !frame.IsExtended && frame.Dlc == 5),
    "JCH4 standard 0x18F frames were not imported correctly.");
var zeroDlcFrame = trcImport.Frames.Single(frame => frame.Id == 0x18A);
Require(zeroDlcFrame.Direction == CanDirection.Tx && zeroDlcFrame.Dlc == 0,
    "TRC importer did not preserve Tx DLC=0 frame.");
Require((standardJchFrames[1].Timestamp - standardJchFrames[0].Timestamp).TotalMilliseconds == 10,
    "TRC timestamps were not preserved in milliseconds.");

var explicitlyExtended = PcanTrcCodec.Parse(new[]
{
    ";$FILEVERSION=1.1",
    "1) 0.000 Rx 00000123x 1 AA"
}).Single();
Require(explicitlyExtended.Id == 0x123 && explicitlyExtended.IsExtended,
    "An explicitly extended 29-bit PCAN ID with a low numeric value was not preserved.");

var version10Frame = PcanTrcCodec.Parse(new[]
{
    "1) 0.000 018F 1 08"
}).Single();
var version12Frame = PcanTrcCodec.Parse(new[]
{
    ";$FILEVERSION=1.2",
    "1) 1.000 1 Rx 018F 1 09"
}, defaultChannel: 4).Single();
var version13Frame = PcanTrcCodec.Parse(new[]
{
    ";$FILEVERSION=1.3",
    "1) 2.000 1 Rx 018F - 1 0A"
}, defaultChannel: 4).Single();
var version30Frames = PcanTrcCodec.Parse(new[]
{
    ";$FILEVERSION=3.0",
    ";$COLUMNS=N,O,T,B,I,d,R,l,D",
    "1 3.000 DT 1 18F Rx - 1 0B",
    "2 4.000 DT 1 0CFF5321 Rx - 1 0C"
}, defaultChannel: 4);
Require(!version10Frame.IsExtended && version10Frame.Id == 0x18F && version10Frame.Data[0] == 0x08,
    "PCAN TRC 1.0 layout was not parsed correctly.");
Require(version12Frame.Channel == 4 && version12Frame.Data[0] == 0x09 &&
        version13Frame.Channel == 4 && version13Frame.Data[0] == 0x0A,
    "PCAN TRC 1.2/1.3 bus, reserved field or DATA layout was not parsed correctly.");
Require(version30Frames.Count == 2 && !version30Frames[0].IsExtended &&
        version30Frames[1].IsExtended && version30Frames[1].Id == 0x0CFF5321,
    "PCAN TRC 3.0 Standard/Extended layout was not parsed correctly.");

var jchExperiment = await GenericTraceExperiment.CompareWindowsAsync(
    fixturePath,
    new TraceWindow(0, 40),
    new TraceWindow(200, 240),
    channel: 2);
var jchByte1 = jchExperiment.Comparisons.Single(row => row.Id == 0x18F && row.DataIndex == 1);
var jchByte2 = jchExperiment.Comparisons.Single(row => row.Id == 0x18F && row.DataIndex == 2);
Require(jchExperiment.ReferenceFrameCount == 4 && jchExperiment.ActionFrameCount == 4,
    "TRC temporal windows selected an incorrect number of frames.");
Require(jchByte1.ReferenceValue == 0x00 && jchByte1.ActionValue == 0x02 && jchByte1.XorMask == 0x02 &&
        jchByte1.ChangedBits.Contains("bit 1: 0→1"),
    "JCH4 08 00 00 00 00 → 08 02 C8 00 00 transition was not detected at DATA[1].");
Require(jchByte2.ReferenceValue == 0x00 && jchByte2.ActionValue == 0xC8 && jchByte2.XorMask == 0xC8,
    "JCH4 transition was not detected at DATA[2].");
Require(jchByte1.Kind == GenericCanChangeKind.MultipleStableBytes && jchByte1.IsSignificant,
    "Multiple stable JCH4 byte changes were not ranked as significant.");

var presenceReference = CreateClassicalFrames(now, 0x300, false, 4, _ => new byte[] { 0x00 })
    .Concat(CreateClassicalFrames(now, 0x301, false, 4, _ => new byte[] { 0x11 }));
var presenceAction = CreateClassicalFrames(now, 0x300, false, 4, _ => new byte[] { 0x00 })
    .Concat(CreateClassicalFrames(now, 0x302, false, 4, _ => new byte[] { 0x22 }));
var presenceComparison = GenericExperimentComparator.Compare(presenceReference, presenceAction);
Require(presenceComparison.Single(row => row.Id == 0x301).MessageDisappeared,
    "Disappeared CAN ID was not reported.");
Require(presenceComparison.Single(row => row.Id == 0x302).MessageAppeared,
    "Appeared CAN ID was not reported.");

var noisyReference = CreateClassicalFrames(now, 0x400, false, 4,
    index => new byte[] { (byte)(0x10 + index) });
var noisyAction = CreateClassicalFrames(now, 0x400, false, 4,
    index => new byte[] { (byte)(0x11 + index) });
var noisyChange = GenericExperimentComparator.Compare(noisyReference, noisyAction)
    .Single(row => row.Id == 0x400 && row.DataIndex == 0);
Require(noisyChange.IsChanged && noisyChange.Priority == GenericCanChangePriority.Low &&
        noisyChange.Kind == GenericCanChangeKind.UnstableOrAnalogNoise && !noisyChange.IsSignificant,
    "Continuous analog-like noise must remain visible but receive low priority.");

var variableDlcReference = CreateClassicalFrames(now, 0x410, false, 4,
    index => index % 2 == 0 ? new byte[] { 0x10 } : new byte[] { 0x10, 0x20 });
var variableDlcAction = CreateClassicalFrames(now, 0x410, false, 4,
    index => index % 2 == 0 ? new byte[] { 0x11 } : new byte[] { 0x11, 0x20 });
var variableDlcChange = GenericExperimentComparator.Compare(variableDlcReference, variableDlcAction)
    .Single(row => row.Id == 0x410 && row.DataIndex == 0);
Require(variableDlcChange.Priority == GenericCanChangePriority.Low &&
        variableDlcChange.ReferenceAgreementPercent == 50 &&
        variableDlcChange.ActionAgreementPercent == 50,
    "A changing DLC must reduce agreement instead of producing a false stable change.");

var identicalFiles = await GenericTraceExperiment.CompareFilesAsync(fixturePath, fixturePath, channel: 2);
Require(identicalFiles.Comparisons.All(row => !row.IsChanged),
    "Two identical TRC files must not produce Reference/Action changes.");

var liveFixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "live_guided_demo.trc");
var liveFixtureFrames = await PcanTrcCodec.LoadAsync(liveFixturePath);
Require(liveFixtureFrames.Count == 48 && liveFixtureFrames.Select(frame => frame.Timestamp)
        .SequenceEqual(liveFixtureFrames.Select(frame => frame.Timestamp).OrderBy(value => value)),
    "Live replay fixture order or timestamps were not preserved.");
Require(liveFixtureFrames.Any(frame => frame.Id == 0x18F && !frame.IsExtended && frame.Dlc == 5) &&
        liveFixtureFrames.Any(frame => frame.Id == 0x0CFF5321 && frame.IsExtended && frame.Dlc == 8),
    "Live replay lost Standard/Extended separation or DLC.");

await using (var replay = new ReplayCanDriver(liveFixturePath, ReplayTimingMode.Accelerated, 100_000))
{
    var replayChannels = await replay.DiscoverChannelsAsync();
    Require(replayChannels.Count == 1 && replay.SupportsListenOnly,
        "Replay channel discovery or LISTEN ONLY capability is missing.");
    await replay.OpenAsync(new CanChannelSettings("replay-trc", 250_000, ListenOnly: true));
    var replayed = new List<CanFrame>();
    await foreach (var frame in replay.ReadFramesAsync()) replayed.Add(frame);
    Require(replayed.Count == liveFixtureFrames.Count && replayed.Select(frame => frame.Id)
            .SequenceEqual(liveFixtureFrames.Select(frame => frame.Id)) &&
            replayed.Select(frame => frame.Timestamp).SequenceEqual(liveFixtureFrames.Select(frame => frame.Timestamp)),
        "Replay driver changed frame order, identifiers or timestamps.");
    Require(((ICanDriverDiagnostics)replay).GetStatus().ReceivedFrames == 48,
        "Replay diagnostics did not count received frames.");
}

await using (var cancellableReplay = new ReplayCanDriver(liveFixturePath, ReplayTimingMode.Step))
{
    await cancellableReplay.OpenAsync(new CanChannelSettings("replay-trc", 250_000));
    using var cancellation = new CancellationTokenSource();
    await using var enumerator = cancellableReplay.ReadFramesAsync(cancellation.Token).GetAsyncEnumerator();
    var waitingForStep = enumerator.MoveNextAsync().AsTask();
    cancellation.Cancel();
    var cancellationError = await CaptureExceptionAsync(async () => await waitingForStep);
    Require(cancellationError is OperationCanceledException,
        "Replay ReadFramesAsync did not honor CancellationToken.");
}

var buffer = new LiveCanBuffer(TimeSpan.FromSeconds(2), maximumFrameCount: 3);
buffer.AppendRange(liveFixtureFrames.Take(5));
Require(buffer.Count == 3 && buffer.EvictedFrames == 2 &&
        buffer.Snapshot().Select(frame => frame.Timestamp).SequenceEqual(liveFixtureFrames.Skip(2).Take(3).Select(frame => frame.Timestamp)),
    "Live buffer rollover did not preserve the newest ordered frames.");
var rangedFrames = buffer.GetRange(liveFixtureFrames[2].Timestamp, liveFixtureFrames[4].Timestamp);
Require(rangedFrames.Count == 2, "Live buffer time-range boundaries are incorrect.");

var liveRawPath = Path.Combine(Path.GetTempPath(), $"cranecan-live-{Guid.NewGuid():N}.trc");
try
{
    var liveStart = liveFixtureFrames[0].Timestamp;
    var liveConfiguration = new LiveExperimentConfiguration
    {
        ActionName = "TELESCOPE_OUT",
        OperatorInstruction = "Плавно отклоните джойстик EXTEND и удерживайте.",
        Bus = "CAN1",
        RepeatNumber = 1
    };
    var liveSession = new LiveExperimentSession(liveConfiguration);
    await using var liveReplay = new ReplayCanDriver(liveFixturePath, ReplayTimingMode.Accelerated, 100_000);
    await using var liveReceiver = new LiveCanReceiver(liveReplay);
    liveReceiver.AttachSession(liveSession);
    liveSession.Start(liveStart);
    await liveReceiver.StartAsync(new CanChannelSettings("replay-trc", 250_000, ListenOnly: true),
        liveRawPath, liveStart);
    await liveReceiver.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    Require(liveSession.State == LiveExperimentState.Analyzing,
        "Complete replay did not reach the Analyzing state.");
    var liveResult = liveSession.Analyze();
    await liveReceiver.StopAsync(invalidateActiveExperiment: false);
    Require(liveResult.Outcome == LiveExperimentOutcome.Valid && liveResult.Analysis.Quality.CanAnalyze,
        "Complete REFERENCE/ACTION/POST replay was marked invalid.");
    Require(liveResult.Transitions.Select(item => item.State).SequenceEqual(new[]
        {
            LiveExperimentState.Baseline,
            LiveExperimentState.WaitingForAction,
            LiveExperimentState.Action,
            LiveExperimentState.PostAction,
            LiveExperimentState.Analyzing,
            LiveExperimentState.Completed
        }),
        "Live state machine transition sequence is incorrect.");
    Require(liveResult.Run.ReferenceFrames.All(frame => frame.Timestamp >= liveResult.Boundaries.BaselineStart &&
                                                frame.Timestamp < liveResult.Boundaries.BaselineEnd) &&
            liveResult.Run.ActionFrames.All(frame => frame.Timestamp >= liveResult.Boundaries.ActionStart &&
                                             frame.Timestamp < liveResult.Boundaries.ActionEnd) &&
            liveResult.Run.ReturnFrames!.All(frame => frame.Timestamp >= liveResult.Boundaries.ActionEnd &&
                                              frame.Timestamp < liveResult.Boundaries.PostActionEnd),
        "REFERENCE/ACTION/POST frame boundaries are not exact.");
    Require(liveResult.Analysis.Candidates.Any(candidate => candidate.Id == 0x18F &&
                candidate.DataIndex == 1 && candidate.BitIndex == 1 &&
                candidate.ReferenceValue == 0x00 && candidate.ActionValue == 0x02),
        "Full Live Guided cycle did not detect JCH4 0x18F DATA[1] bit 1: 0→1.");
    Require(liveResult.Analysis.Candidates.Any(candidate => candidate.Id == 0x18F &&
                candidate.DataIndex == 2 && candidate.ReferenceValue == 0x00 && candidate.ActionValue == 0xC8),
        "Full Live Guided cycle did not detect JCH4 DATA[2] transition.");
    var restoredLiveRaw = await PcanTrcCodec.LoadAsync(liveRawPath);
    Require(restoredLiveRaw.Count == 48 && restoredLiveRaw.Any(frame => frame.IsExtended),
        "Continuous raw Live TRC capture did not round-trip.");
}
finally
{
    if (File.Exists(liveRawPath)) File.Delete(liveRawPath);
}

var emptyLiveSession = new LiveExperimentSession(new LiveExperimentConfiguration());
emptyLiveSession.Start(now);
emptyLiveSession.AdvanceTo(now.AddSeconds(23));
var emptyLiveResult = emptyLiveSession.Analyze();
Require(emptyLiveResult.Outcome == LiveExperimentOutcome.Invalid &&
        emptyLiveResult.Analysis.Candidates.Count == 0 &&
        emptyLiveResult.Warnings.Any(warning => warning.Code == LiveSessionWarningCode.NoFrames),
    "An empty Live CAN session produced confident candidates.");
var abortedLiveSession = new LiveExperimentSession(new LiveExperimentConfiguration());
abortedLiveSession.Start(now);
abortedLiveSession.Abort(now.AddSeconds(1));
Require(abortedLiveSession.State == LiveExperimentState.Aborted &&
        abortedLiveSession.Outcome == LiveExperimentOutcome.Aborted,
    "Operator Abort did not terminate the Live experiment safely.");

Require(PcanBasicCanDriver.TryMapBitrate(250_000, out var pcan250) && pcan250 == 0x011C &&
        PcanBasicCanDriver.TryMapBitrate(500_000, out _) &&
        !PcanBasicCanDriver.TryMapBitrate(123_456, out _),
    "PCAN Classical CAN bitrate mapping is incorrect.");
Require(PcanBasicCanDriver.TryParseChannelId("pcan-usb:0051", out var pcanChannel) && pcanChannel == 0x51,
    "PCAN-USB channel identifier parsing failed.");
Require(typeof(PcanBasicCanDriver).GetMethods().All(method =>
        !method.Name.Contains("Write", StringComparison.OrdinalIgnoreCase) &&
        !method.Name.Contains("Send", StringComparison.OrdinalIgnoreCase)),
    "Receive-only PCAN driver unexpectedly exposes a transmit method.");

var emptyWindowError = CaptureException(() => GenericTraceExperiment.CompareWindows(
    trcImport.Frames,
    new TraceWindow(300, 400),
    new TraceWindow(0, 40)));
Require(emptyWindowError is InvalidOperationException && emptyWindowError.Message.Contains("REFERENCE"),
    "An empty Reference window must produce a clear Russian error.");

var emptyTrcError = CaptureException(() => PcanTrcCodec.Parse(new[] { "; only header", "unknown" }));
Require(emptyTrcError is FormatException && emptyTrcError.Message.Contains("не содержит"),
    "Empty/invalid TRC must produce a clear format error.");

var oversizedTrcError = CaptureException(() => PcanTrcCodec.Parse(new[]
{
    "1) 0.000 Rx 123 1 00",
    "2) 1.000 Rx 123 1 01"
}, maximumFrameCount: 1));
Require(oversizedTrcError is InvalidDataException && oversizedTrcError.Message.Contains("исходный файл не изменён"),
    "An oversized TRC must stop with a clear memory-safety message.");

var guidedReference = CreateClassicalFrames(now.AddSeconds(10), 0x281, false, 5,
    _ => new byte[] { 0x00, 0x20 });
var guidedAction = CreateClassicalFrames(now.AddSeconds(11), 0x281, false, 5,
    _ => new byte[] { 0x20, 0x00 });
var guidedReturn = CreateClassicalFrames(now.AddSeconds(12), 0x281, false, 3,
    _ => new byte[] { 0x00, 0x20 });
var guidedRuns = Enumerable.Range(1, 3)
    .Select(repeat => new GuidedExperimentRun(
        repeat,
        "CAN1",
        guidedReference,
        guidedAction,
        ApproximateEventTime: guidedAction[0].Timestamp,
        EventSearchTolerance: TimeSpan.Zero,
        ReturnFrames: guidedReturn))
    .ToArray();
var guidedResult = GuidedDiagnosticsAnalyzer.Analyze("Joystick EXTEND", guidedRuns);
Require(guidedResult.Quality.CanAnalyze, "A complete guided experiment must be analyzable.");
var risingBit = guidedResult.Candidates.Single(candidate =>
    candidate.Id == 0x281 && candidate.DataIndex == 0 && candidate.BitIndex == 5);
var fallingBit = guidedResult.Candidates.Single(candidate =>
    candidate.Id == 0x281 && candidate.DataIndex == 1 && candidate.BitIndex == 5);
Require(risingBit.ReferenceValue == 0 && risingBit.ActionValue == 0x20 &&
        risingBit.ChangeKind == CandidateChangeKind.StableBit,
    "Guided diagnostics did not detect stable bit 0→1.");
Require(fallingBit.ReferenceValue == 0x20 && fallingBit.ActionValue == 0 &&
        fallingBit.ChangeKind == CandidateChangeKind.StableBit,
    "Guided diagnostics did not detect stable bit 1→0.");
Require(risingBit.RepeatabilityCount == 3 && risingBit.RepeatCount == 3 && risingBit.Score >= 90,
    "A repeatable 3/3 stable bit with return must receive a high transparent score.");
Require(risingBit.ScoreExplanation.Any(item => item.Reason == ScoreReason.RepeatedAllThreeOrMore) &&
        risingBit.ScoreExplanation.Any(item => item.Reason == ScoreReason.ReturnsToBaseline),
    "The confidence explanation did not preserve repeatability and return evidence.");
Require(guidedResult.Timeline.Any(item => item.EventType == "CandidateChanged" && item.Id == 0x281),
    "Guided event timeline did not include the candidate transition.");
var guidedReport = GuidedReportFormatter.Format(guidedResult, "Joystick EXTEND", now);
Require(guidedReport.Contains("КАНДИДАТ #1") && guidedReport.Contains("Повторяемость: 3/3") &&
        guidedReport.Contains("не доказывает внутреннюю логику ECU") &&
        !guidedReport.Contains("это точно сигнал", StringComparison.OrdinalIgnoreCase),
    "Human-readable guided report lost candidate wording or the safety limitation.");

var twoOfThreeRuns = guidedRuns.Take(2).Append(new GuidedExperimentRun(
    3,
    "CAN1",
    CreateClassicalFrames(now.AddSeconds(20), 0x281, false, 5, _ => new byte[] { 0x00, 0x20 }),
    CreateClassicalFrames(now.AddSeconds(21), 0x281, false, 5, _ => new byte[] { 0x00, 0x20 }))).ToArray();
var twoOfThreeResult = GuidedDiagnosticsAnalyzer.Analyze("Joystick EXTEND", twoOfThreeRuns);
Require(twoOfThreeResult.Candidates.Single(candidate =>
        candidate.Id == 0x281 && candidate.DataIndex == 0).RepeatabilityCount == 2,
    "Guided diagnostics did not report 2/3 repeatability.");

var rampActionValues = new byte[] { 0x02, 0x0F, 0x11, 0x40, 0xC8, 0xC8, 0xC8 };
var rampResult = GuidedDiagnosticsAnalyzer.Analyze("Joystick EXTEND",
[
    new GuidedExperimentRun(
        1,
        "CAN1",
        CreateClassicalFrames(now.AddSeconds(30), 0x18F, false, 5, _ => new byte[] { 0x00 }),
        CreateClassicalFrames(now.AddSeconds(31), 0x18F, false, rampActionValues.Length,
            index => new[] { rampActionValues[index] }))
]);
var rampCandidate = rampResult.Candidates.Single(candidate => candidate.Id == 0x18F);
Require(rampCandidate.ChangeKind == CandidateChangeKind.Ramp &&
        rampCandidate.Temporal is { MinimumValue: 0x02, MaximumValue: 0xC8, TransitionCount: >= 4 } &&
        rampCandidate.Temporal.MonotonicityPercent == 100,
    "Temporal analyzer did not recognize the JCH4-style monotonic ramp.");

var beforeActionResult = GuidedDiagnosticsAnalyzer.Analyze("Delayed action",
[
    new GuidedExperimentRun(
        1,
        "CAN1",
        CreateClassicalFrames(now.AddSeconds(40), 0x500, false, 4, _ => new byte[] { 0x00 }),
        CreateClassicalFrames(now.AddSeconds(41), 0x500, false, 4, _ => new byte[] { 0x01 }),
        ApproximateEventTime: now.AddSeconds(41.2),
        EventSearchTolerance: TimeSpan.Zero)
]);
var beforeActionCandidate = beforeActionResult.Candidates.Single(candidate => candidate.Id == 0x500);
Require(beforeActionCandidate.ReactionMilliseconds < 0 &&
        beforeActionCandidate.ScoreExplanation.Any(item => item.Reason == ScoreReason.OccursBeforeAction),
    "A change before the declared physical action must be penalized.");

var sameNumericIdResult = GuidedDiagnosticsAnalyzer.Analyze("Format separation",
[
    new GuidedExperimentRun(
        1,
        "CAN1",
        CreateClassicalFrames(now.AddSeconds(50), 0x123, false, 4, _ => new byte[] { 0x00 })
            .Concat(CreateClassicalFrames(now.AddSeconds(50), 0x123, true, 4, _ => new byte[] { 0x10 })).ToArray(),
        CreateClassicalFrames(now.AddSeconds(51), 0x123, false, 4, _ => new byte[] { 0x01 })
            .Concat(CreateClassicalFrames(now.AddSeconds(51), 0x123, true, 4, _ => new byte[] { 0x12 })).ToArray())
]);
Require(sameNumericIdResult.Candidates.Count(candidate => candidate.Id == 0x123) == 2 &&
        sameNumericIdResult.Candidates.Select(candidate => candidate.IsExtended).Distinct().Count() == 2,
    "Standard and Extended frames with the same numeric ID were merged incorrectly.");

var overlappingQuality = ExperimentQualityAnalyzer.Evaluate(
[
    new GuidedExperimentRun(
        1,
        "CAN1",
        guidedReference,
        guidedAction,
        ReferenceWindow: new TraceWindow(0, 100),
        ActionWindow: new TraceWindow(50, 150),
        ReferencePath: "same.trc",
        ActionPath: "same.trc")
]);
Require(!overlappingQuality.CanAnalyze && overlappingQuality.Issues.Any(issue =>
        issue.Code == ExperimentQualityCode.OverlappingWindows),
    "Overlapping windows must make a guided experiment invalid.");
var differentBusQuality = ExperimentQualityAnalyzer.Evaluate(
[
    new GuidedExperimentRun(
        1,
        "CAN1",
        guidedReference,
        guidedAction,
        ReferenceBus: "CAN1",
        ActionBus: "CAN2")
]);
Require(!differentBusQuality.CanAnalyze && differentBusQuality.Issues.Any(issue =>
        issue.Code == ExperimentQualityCode.DifferentBuses),
    "REFERENCE and ACTION from different CAN buses must not be compared silently.");
var swappedWindowQuality = ExperimentQualityAnalyzer.Evaluate(
[
    new GuidedExperimentRun(
        1,
        "CAN1",
        guidedReference,
        guidedAction,
        ReferenceWindow: new TraceWindow(200, 300),
        ActionWindow: new TraceWindow(0, 100),
        ReferencePath: "same.trc",
        ActionPath: "same.trc")
]);
Require(swappedWindowQuality.CanAnalyze && swappedWindowQuality.Issues.Any(issue =>
        issue.Code == ExperimentQualityCode.ReferenceAfterAction &&
        issue.Severity == ExperimentQualitySeverity.Warning),
    "A possible REFERENCE/ACTION swap must remain analyzable but show a warning.");
var emptyReferenceQuality = ExperimentQualityAnalyzer.Evaluate(
[
    new GuidedExperimentRun(1, "CAN1", [], guidedAction)
]);
var emptyActionQuality = ExperimentQualityAnalyzer.Evaluate(
[
    new GuidedExperimentRun(1, "CAN1", guidedReference, [])
]);
Require(!emptyReferenceQuality.CanAnalyze && !emptyActionQuality.CanAnalyze,
    "Empty REFERENCE or ACTION must make the experiment invalid.");
Require(Enum.GetValues<SignalKnowledgeState>().SequenceEqual(new[]
    {
        SignalKnowledgeState.Unknown,
        SignalKnowledgeState.Candidate,
        SignalKnowledgeState.Probable,
        SignalKnowledgeState.Confirmed,
        SignalKnowledgeState.Rejected
    }),
    "Signal knowledge states are incomplete or out of order.");

var onkTemporaryFile = Path.Combine(Path.GetTempPath(), $"onk160-{Guid.NewGuid():N}.csv");
var canTemporaryFile = Path.Combine(Path.GetTempPath(), $"classical-can-{Guid.NewGuid():N}.csv");
var benchTemporaryFile = Path.Combine(Path.GetTempPath(), $"onk160-bench-{Guid.NewGuid():N}.csv");
var profileTemporaryFile = Path.Combine(Path.GetTempPath(), $"machine-{Guid.NewGuid():N}.craneprofile");
var experimentTemporaryFile = Path.Combine(Path.GetTempPath(), $"experiment-{Guid.NewGuid():N}.canexperiment");
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

    var profile = MachineProfileService.AddCandidate(
        new MachineProfile
        {
            Manufacturer = "unknown",
            Model = "unknown",
            MachineName = "Test machine",
            CanBusName = "CAN1"
        },
        risingBit,
        "Joystick EXTEND",
        SignalKnowledgeState.Candidate,
        "Synthetic smoke test");
    await GuidedJsonCodec.SaveProfileAsync(profileTemporaryFile, profile);
    var restoredProfile = await GuidedJsonCodec.LoadProfileAsync(profileTemporaryFile);
    Require(restoredProfile.ProfileId == profile.ProfileId &&
            restoredProfile.ExperimentalSignals.Single().Confidence == SignalKnowledgeState.Candidate &&
            restoredProfile.ExperimentalSignals.Single().Evidence.Single().Kind == EvidenceKind.RepeatedExperiment,
        "Machine profile or evidence JSON round-trip failed.");

    var upsertConfirmedProfile = MachineProfileService.AddCandidate(
        restoredProfile,
        risingBit,
        "Joystick EXTEND",
        SignalKnowledgeState.Confirmed,
        "Confirmed after repeated experiment");
    Require(upsertConfirmedProfile.ExperimentalSignals.Count == 0 &&
            upsertConfirmedProfile.KnownSignals.Count == 1 &&
            upsertConfirmedProfile.KnownSignals.Single().SignalId ==
                restoredProfile.ExperimentalSignals.Single().SignalId &&
            upsertConfirmedProfile.KnownSignals.Single().Evidence.Any(item =>
                item.Kind == EvidenceKind.UserConfirmation),
        "Re-analyzing the same location must promote CANDIDATE to CONFIRMED without a duplicate.");

    var confirmedProfile = MachineProfileService.PromoteSignal(
        restoredProfile,
        restoredProfile.ExperimentalSignals.Single().SignalId,
        SignalKnowledgeState.Confirmed,
        new SignalEvidence
        {
            Kind = EvidenceKind.UserConfirmation,
            Description = "Physically confirmed"
        });
    Require(confirmedProfile.ExperimentalSignals.Count == 0 &&
            confirmedProfile.KnownSignals.Single().Confidence == SignalKnowledgeState.Confirmed &&
            confirmedProfile.KnownSignals.Single().Evidence.Count == 2,
        "Candidate-to-confirmed profile promotion failed.");

    var experiment = new GuidedExperiment
    {
        Name = "Joystick EXTEND",
        ActionName = "Joystick EXTEND",
        Bus = "CAN1",
        Repeats =
        [
            new GuidedExperimentRepeat
            {
                RepeatNumber = 1,
                ReferenceSource = new ExperimentTraceSource
                {
                    Path = fixturePath,
                    Bus = "CAN1",
                    Window = new TraceWindow(0, 40)
                },
                ActionSource = new ExperimentTraceSource
                {
                    Path = fixturePath,
                    Bus = "CAN1",
                    Window = new TraceWindow(200, 240)
                },
                ReturnSource = new ExperimentTraceSource
                {
                    Path = fixturePath,
                    Bus = "CAN1",
                    Window = new TraceWindow(0, 40)
                }
            }
        ],
        Candidates =
        [
            new GuidedCandidateSnapshot
            {
                StableKey = risingBit.StableKey,
                Id = risingBit.Id,
                DataIndex = risingBit.DataIndex,
                BitIndex = risingBit.BitIndex,
                Score = risingBit.Score,
                RepeatabilityCount = 3,
                RepeatCount = 3
            }
        ],
        LiveCaptures =
        [
            new LiveCaptureMetadata
            {
                SessionId = Guid.NewGuid(),
                RepeatNumber = 1,
                DriverId = "pcan-trc-replay",
                ChannelId = "replay-trc",
                Bitrate = 250_000,
                ListenOnlyConfirmed = true,
                RawCapturePath = liveFixturePath,
                CaptureStart = now,
                BaselineStart = now,
                BaselineEnd = now.AddSeconds(5),
                ActionStart = now.AddSeconds(8),
                ActionEnd = now.AddSeconds(18),
                PostActionEnd = now.AddSeconds(23),
                OperatorInstruction = "Joystick EXTEND",
                Outcome = "VALID",
                ReceivedFrames = 48,
                StandardFrames = 24,
                ExtendedFrames = 24
            }
        ]
    };
    await GuidedJsonCodec.SaveExperimentAsync(experimentTemporaryFile, experiment);
    var restoredExperiment = await GuidedJsonCodec.LoadExperimentAsync(experimentTemporaryFile);
    Require(restoredExperiment.ExperimentId == experiment.ExperimentId &&
            restoredExperiment.Repeats.Single().ReferenceSource.Window == new TraceWindow(0, 40) &&
            restoredExperiment.Candidates.Single().Status == SignalKnowledgeState.Candidate &&
            restoredExperiment.LiveCaptures.Single().ListenOnlyConfirmed &&
            restoredExperiment.LiveCaptures.Single().ReceivedFrames == 48,
        "Guided experiment JSON round-trip failed.");
    var restoredRun = await GuidedExperimentRunLoader.LoadAsync(restoredExperiment.Repeats.Single());
    Require(restoredRun.ReferenceFrames.Count == 4 && restoredRun.ActionFrames.Count == 4 &&
            restoredRun.ReturnFrames?.Count == 4,
        "Saved experiment could not reopen REFERENCE/ACTION/POST through the common pipeline.");
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

    if (File.Exists(profileTemporaryFile))
    {
        File.Delete(profileTemporaryFile);
    }

    if (File.Exists(experimentTemporaryFile))
    {
        File.Delete(experimentTemporaryFile);
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

static IReadOnlyList<CanFrame> CreateClassicalFrames(
    DateTimeOffset start,
    uint id,
    bool isExtended,
    int count,
    Func<int, byte[]> dataFactory) =>
    Enumerable.Range(0, count).Select(index => new CanFrame
    {
        Timestamp = start.AddMilliseconds(index * 10),
        Channel = 0,
        Id = id,
        IsExtended = isExtended,
        Data = dataFactory(index),
        Protocol = BusProtocol.ClassicalCan,
        Direction = CanDirection.Rx
    }).ToArray();

static Exception? CaptureException(Action action)
{
    try
    {
        action();
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}

static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
{
    try
    {
        await action();
        return null;
    }
    catch (Exception exception)
    {
        return exception;
    }
}
