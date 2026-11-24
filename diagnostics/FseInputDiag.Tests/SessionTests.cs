using System.IO;
using System.Text;
using System.Text.Json;
using FseInputDiag;
using Xunit;

namespace FseInputDiag.Tests;

public class SessionTests
{
    private const string Sha = "0a0a13b937ed987a131a0729226a03de374e7d91";

    [Fact]
    public void DisabledEnqueueIsNoop()
    {
        using var harness = Harness.Create();
        Assert.False(harness.Session.IsCapturing);
        Assert.False(harness.Session.TryEnqueue(Send(1, DiagSendDecision.Sent)));
        harness.Session.Pump();
        harness.Session.Flush();

        Assert.Equal(0, harness.Session.QueueDrops);
        Assert.Equal(0, harness.Session.SampleDrops);
        Assert.Equal(0, harness.Session.ByteDrops);
        Assert.Equal(0, harness.Session.WrittenSamples);
        Assert.Equal(0, harness.Samples.Length);
    }

    [Fact]
    public void DurationIsHardCappedAtThirtySeconds()
    {
        long now = 5_000;
        using var harness = Harness.Create(nowProvider: () => now, limits: new DiagLimits { MaxDurationMs = 120_000 });
        Assert.Equal(DiagLimits.HardMaxDurationMs, harness.Session.EffectiveDurationMs);
        Assert.True(harness.Session.TryArm("", Sha));
        now = harness.Session.ArmDeadlineMs;
        harness.Session.Pump();

        Assert.True(harness.Session.IsCapturing);
        Assert.Equal(now + DiagLimits.HardMaxDurationMs, harness.Session.CaptureDeadlineMs);
        long deadline = harness.Session.CaptureDeadlineMs;
        Assert.False(harness.Session.TryArm("20", Sha));
        Assert.Equal(deadline, harness.Session.CaptureDeadlineMs);

        now = deadline - 1;
        Assert.True(harness.Session.TryEnqueue(Send(1, DiagSendDecision.Sent)));
        now = deadline;
        Assert.False(harness.Session.TryEnqueue(Send(999, DiagSendDecision.Sent)));
        harness.Session.Pump();
        Assert.False(harness.Session.IsCapturing);
        Assert.Equal(0, harness.Session.QueueDrops);

        now = deadline + 60_000;
        Assert.False(harness.Session.TryEnqueue(Send(1000, DiagSendDecision.Sent)));
        harness.Session.Flush();

        string text = harness.ReadSamples();
        Assert.Contains("\"f\":1", text);
        Assert.DoesNotContain("\"f\":999", text);
        Assert.DoesNotContain("\"f\":1000", text);
        Assert.Contains("\"reason\":\"duration\"", text);
        Assert.Contains("\"elapsedMs\":30000", text);
    }

    [Fact]
    public void NewArmAfterFinishDoesNotExtendThePreviousWindow()
    {
        long now = 0;
        using var harness = Harness.Create(nowProvider: () => now);
        Assert.True(harness.Session.TryArm("3", Sha));
        now = harness.Session.ArmDeadlineMs;
        harness.Session.Pump();
        long firstDeadline = harness.Session.CaptureDeadlineMs;
        now = firstDeadline;
        harness.Session.Pump();
        harness.Session.Flush();

        Assert.True(harness.Session.TryArm("", Sha));
        Assert.Equal(0, harness.Session.CaptureDeadlineMs);
        now = harness.Session.ArmDeadlineMs;
        harness.Session.Pump();
        Assert.Equal(now + DiagLimits.HardMaxDurationMs, harness.Session.CaptureDeadlineMs);
        Assert.NotEqual(firstDeadline + DiagLimits.HardMaxDurationMs, harness.Session.CaptureDeadlineMs);
    }

    [Fact]
    public void QueueOverflowCountsDrops()
    {
        long now = 0;
        using var harness = Harness.Create(nowProvider: () => now, limits: new DiagLimits { MaxQueue = 2 });
        Start(harness, ref now);
        Assert.True(harness.Session.TryEnqueue(Send(1, DiagSendDecision.Sent)));
        Assert.True(harness.Session.TryEnqueue(Send(2, DiagSendDecision.Sent)));
        Assert.False(harness.Session.TryEnqueue(Send(3, DiagSendDecision.Sent)));
        Assert.False(harness.Session.TryEnqueue(Send(4, DiagSendDecision.Sent)));
        Assert.Equal(2, harness.Session.QueueDrops);
        harness.Finish(ref now);
        Assert.Equal(2, harness.Session.WrittenSamples);
    }

    [Fact]
    public void SampleCapCountsDrops()
    {
        long now = 0;
        using var harness = Harness.Create(nowProvider: () => now, limits: new DiagLimits { MaxSamples = 2, MaxQueue = 8 });
        Start(harness, ref now);
        Assert.True(harness.Session.TryEnqueue(Send(1, DiagSendDecision.Unchanged)));
        Assert.True(harness.Session.TryEnqueue(Send(2, DiagSendDecision.Unchanged)));
        Assert.False(harness.Session.TryEnqueue(Send(3, DiagSendDecision.Unchanged)));
        Assert.Equal(1, harness.Session.SampleDrops);
        harness.Finish(ref now);
        Assert.Equal(2, harness.Session.WrittenSamples);
        Assert.True(harness.Session.WrittenBytes <= harness.Session.EffectiveMaxBytes);
    }

    [Fact]
    public void OutputByteCapStopsWriting()
    {
        long now = 0;
        using var harness = Harness.Create(nowProvider: () => now, limits: new DiagLimits { MaxOutputBytes = 220, MaxQueue = 16, MaxSamples = 16 });
        Start(harness, ref now);
        for (uint frame = 1; frame <= 8; frame++)
            harness.Session.TryEnqueue(Send(frame, DiagSendDecision.Sent));
        harness.Finish(ref now);

        Assert.True(harness.Session.ByteDrops > 0);
        Assert.True(harness.Session.WrittenSamples < 8);
        Assert.True(harness.Session.WrittenBytes <= 220);
    }

    [Fact]
    public void CorrelatesRawMappedAndSendIncludingMutedListeningUnchangedAndMissing()
    {
        long now = 0;
        using var harness = Harness.Create(nowProvider: () => now);
        Start(harness, ref now);

        EnqueueFrame(harness, raw: Raw(1, DiagReadResult.Missing, present: false), map: Map(1, mute: true, listen: false, buttons: 0, outButtons: 0), send: DiagSendDecision.Unchanged);
        EnqueueFrame(harness, raw: Raw(2, DiagReadResult.Ok, present: true, buttons: 1u << DiagText.B1, lx: 1200), map: Map(2, mute: false, listen: true, buttons: 1u << DiagText.B1, outButtons: 1u << DiagText.B1), send: DiagSendDecision.Listening);
        EnqueueFrame(harness, raw: Raw(3, DiagReadResult.Ok, present: true, buttons: 1u << DiagText.B2), map: Map(3, mute: true, listen: false, buttons: 1u << DiagText.B2, outButtons: 0), send: DiagSendDecision.Sent);
        EnqueueFrame(harness, raw: Raw(4, DiagReadResult.Skipped, present: true), map: Map(4, mute: false, listen: false, buttons: 0, outButtons: 0, present: false), send: DiagSendDecision.NotReached);

        harness.Finish(ref now);
        var rows = harness.ReadObjects().Where(row => row.GetProperty("kind").GetString() is "raw" or "map" or "send").ToList();

        AssertTriplet(rows, 1, "missing", mute: true, listen: false, decision: "unchanged", rawBtn: "", mapBtn: "");
        AssertTriplet(rows, 2, "ok", mute: false, listen: true, decision: "listening", rawBtn: "B1", mapBtn: "B1");
        AssertTriplet(rows, 3, "ok", mute: true, listen: false, decision: "sent", rawBtn: "B2", mapBtn: "B2");
        JsonElement map3 = rows.Single(row => row.GetProperty("kind").GetString() == "map" && row.GetProperty("f").GetUInt32() == 3);
        Assert.Equal("", map3.GetProperty("outBtn").GetString());
        AssertTriplet(rows, 4, "skipped", mute: false, listen: false, decision: "not_reached", rawBtn: "", mapBtn: "");
        JsonElement map4 = rows.Single(row => row.GetProperty("kind").GetString() == "map" && row.GetProperty("f").GetUInt32() == 4);
        Assert.False(map4.GetProperty("present").GetBoolean());
    }

    [Fact]
    public void FormatterDropsPathsUsernamesAndUnsafeText()
    {
        string line = DiagText.FormatSample(new DiagSample
        {
            Stage = DiagStage.Raw,
            Frame = 7,
            Present = true,
            ReadResult = DiagReadResult.Ok,
            Tail = @"C:\Users\tim\USB\VID_045E&PID_028E\0123456789",
            Backend = DiagBackend.XInput,
            Alias = DiagAlias.Oxp,
        });
        string map = DiagText.FormatSample(new DiagSample
        {
            Stage = DiagStage.Map,
            Frame = 7,
            Present = true,
            ForegroundExe = @"C:\Users\tim\steam.exe",
        });

        Assert.Contains("\"tail\":\"0123456789\"", line);
        Assert.DoesNotContain("Users", line);
        Assert.DoesNotContain("tim", line);
        Assert.Contains("\"fg\":\"steam.exe\"", map);
        Assert.DoesNotContain("Users", map);
        Assert.DoesNotContain("tim", map);
        Assert.Equal("0123456789", DiagText.SanitizeTail(@"USB\VID_045E&PID_028E\0123456789"));
        Assert.Equal("296013F", DiagText.SanitizeTail(@"USB\VID_045E&PID_028E\296013F"));
        Assert.Equal(4062603164u, DiagText.Fnv1a32("0123456789"));
    }

    [Theory]
    [InlineData("", 12_000)]
    [InlineData("delay=8", 8_000)]
    [InlineData("1", 3_000)]
    [InlineData("100", 20_000)]
    [InlineData("not-a-delay", 12_000)]
    public void DelayIsClamped(string text, int expectedMs)
    {
        Assert.Equal(expectedMs, DiagText.ParseDelayMs(text, new DiagLimits()));
    }

    [Fact]
    public void ClockFailureDoesNotEscape()
    {
        var session = new FseInputDiagSession(new DiagLimits(), () => throw new InvalidOperationException("clock"), () => new DiagStreams(new MemoryStream(), new MemoryStream()), false);
        session.Pump();
        Assert.False(session.TryEnqueue(Send(1, DiagSendDecision.Sent)));
        session.Stop();
    }

    [Fact]
    public async Task BackgroundWriterDrainsOffTheCaller()
    {
        long now = 0;
        var samples = new MemoryStream();
        var summary = new MemoryStream();
        var session = new FseInputDiagSession(new DiagLimits(), () => now, () => new DiagStreams(samples, summary), backgroundWriter: true);
        Assert.True(session.TryArm("", Sha));
        now = session.ArmDeadlineMs;
        session.Pump();
        Assert.True(session.TryEnqueue(Send(4, DiagSendDecision.Disconnected)));
        now = session.CaptureDeadlineMs;
        session.Pump();
        session.Stop();

        string text = Encoding.UTF8.GetString(samples.ToArray());
        string summaryText = Encoding.UTF8.GetString(summary.ToArray());
        Assert.Contains("\"decision\":\"disconnected\"", text);
        Assert.Contains("\"reason\":\"duration\"", text);
        Assert.Contains("base=" + Sha, summaryText);
        Assert.DoesNotContain("Users", text);
        await Task.CompletedTask;
    }

    private static void Start(Harness harness, ref long now)
    {
        Assert.True(harness.Session.TryArm("", Sha));
        now = harness.Session.ArmDeadlineMs;
        harness.Session.Pump();
        Assert.True(harness.Session.IsCapturing);
    }

    private static void EnqueueFrame(Harness harness, DiagSample raw, DiagSample map, DiagSendDecision send)
    {
        Assert.True(harness.Session.TryEnqueue(raw));
        Assert.True(harness.Session.TryEnqueue(map));
        Assert.True(harness.Session.TryEnqueue(Send(raw.Frame, send)));
    }

    private static void AssertTriplet(List<JsonElement> rows, uint frame, string read, bool mute, bool listen, string decision, string rawBtn, string mapBtn)
    {
        JsonElement raw = rows.Single(row => row.GetProperty("kind").GetString() == "raw" && row.GetProperty("f").GetUInt32() == frame);
        JsonElement map = rows.Single(row => row.GetProperty("kind").GetString() == "map" && row.GetProperty("f").GetUInt32() == frame);
        JsonElement send = rows.Single(row => row.GetProperty("kind").GetString() == "send" && row.GetProperty("f").GetUInt32() == frame);
        Assert.Equal(read, raw.GetProperty("res").GetString());
        Assert.Equal(rawBtn, raw.GetProperty("btn").GetString());
        Assert.Equal(mapBtn, map.GetProperty("btn").GetString());
        Assert.Equal(mute, map.GetProperty("mute").GetBoolean());
        Assert.Equal(listen, map.GetProperty("listen").GetBoolean());
        Assert.Equal(decision, send.GetProperty("decision").GetString());
        Assert.Equal(raw.GetProperty("ms").GetInt64(), map.GetProperty("ms").GetInt64());
        Assert.Equal(raw.GetProperty("ms").GetInt64(), send.GetProperty("ms").GetInt64());
    }

    [Fact]
    public void HostRearmReleasesTheFileAndStartsAFreshCapture()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fse-rearm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            long now = 0;
            var session = new FseInputDiagSession(new DiagLimits(), () => now, () => DiagFiles.Open(directory), backgroundWriter: true);
            File.WriteAllText(Path.Combine(directory, DiagFiles.ArmFileName), "20");
            Assert.Equal("accepted", DiagFiles.ConsumeAfterAccept(directory, session, Sha));
            Assert.False(File.Exists(Path.Combine(directory, DiagFiles.ArmFileName)));
            now = session.ArmDeadlineMs;
            session.Pump();
            long firstDeadline = session.CaptureDeadlineMs;
            Assert.Equal(now + DiagLimits.HardMaxDurationMs, firstDeadline);
            Assert.True(session.TryEnqueue(Send(1, DiagSendDecision.Sent)));
            File.WriteAllText(Path.Combine(directory, DiagFiles.ArmFileName), "20");
            Assert.Equal("busy", DiagFiles.ConsumeAfterAccept(directory, session, Sha));
            Assert.Equal(firstDeadline, session.CaptureDeadlineMs);
            Assert.False(File.Exists(Path.Combine(directory, DiagFiles.ArmFileName)));
            now = firstDeadline;
            session.Pump();
            session.Stop();

            string firstPath = Path.Combine(directory, DiagFiles.SampleFileName);
            string first = File.ReadAllText(firstPath);
            Assert.Contains("\"v\":2", first);
            Assert.Contains("\"utc\":", first);
            string sid1 = session.SessionId;
            Assert.Contains(sid1, first);
            using (FileStream probe = new(firstPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                Assert.True(probe.CanWrite);
            }

            File.WriteAllText(Path.Combine(directory, DiagFiles.ArmFileName), "20");
            Assert.Equal("accepted", DiagFiles.ConsumeAfterAccept(directory, session, Sha));
            Assert.Equal(0, session.CaptureDeadlineMs);
            now = session.ArmDeadlineMs;
            session.Pump();
            Assert.Equal(now + DiagLimits.HardMaxDurationMs, session.CaptureDeadlineMs);
            Assert.NotEqual(firstDeadline, session.CaptureDeadlineMs);
            Assert.True(session.TryEnqueue(Send(2, DiagSendDecision.Unchanged)));
            now = session.CaptureDeadlineMs;
            session.Pump();
            session.Stop();

            string second = File.ReadAllText(firstPath);
            Assert.DoesNotContain(sid1, second);
            Assert.Contains(session.SessionId, second);
            Assert.Contains("\"f\":2", second);
            Assert.DoesNotContain("\"f\":1", second);
            Assert.Contains("\"maxMs\":30000", second);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    private static DiagSample Send(uint frame, DiagSendDecision decision) => new()
    {
        Stage = DiagStage.Send,
        Frame = frame,
        Ms = 10,
        Send = decision,
    };

    private static DiagSample Raw(uint frame, DiagReadResult result, bool present, uint buttons = 0, short lx = 0) => new()
    {
        Stage = DiagStage.Raw,
        Frame = frame,
        Ms = 10,
        Present = present,
        ReadResult = result,
        Backend = DiagBackend.XInput,
        Alias = DiagAlias.Oxp,
        Buttons = buttons,
        Lx = lx,
        Tail = "0123456789",
    };

    private static DiagSample Map(uint frame, bool mute, bool listen, uint buttons, uint outButtons, bool present = true) => new()
    {
        Stage = DiagStage.Map,
        Frame = frame,
        Ms = 10,
        Present = present,
        PublishLive = present,
        Buttons = buttons,
        OutButtons = outButtons,
        Mute = mute,
        Listening = listen,
        Focus = mute,
        ForegroundPid = 12160,
        ForegroundExe = "steam.exe",
    };

    private sealed class Harness : IDisposable
    {
        private readonly MemoryStream _samples;
        private readonly MemoryStream _summary;
        private Harness(FseInputDiagSession session, MemoryStream samples, MemoryStream summary)
        {
            Session = session;
            _samples = samples;
            _summary = summary;
        }

        public FseInputDiagSession Session { get; }
        public MemoryStream Samples => _samples;

        public static Harness Create(Func<long>? nowProvider = null, DiagLimits? limits = null)
        {
            long now = 0;
            nowProvider ??= () => now;
            var samples = new MemoryStream();
            var summary = new MemoryStream();
            var session = new FseInputDiagSession(limits, nowProvider, () => new DiagStreams(samples, summary), backgroundWriter: false);
            return new Harness(session, samples, summary);
        }

        public void Finish(ref long now)
        {
            now = Session.CaptureDeadlineMs;
            Session.Pump();
            Session.Flush();
        }

        public string ReadSamples()
        {
            Session.Flush();
            return Encoding.UTF8.GetString(_samples.ToArray());
        }

        public List<JsonElement> ReadObjects()
        {
            return ReadSamples()
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                .ToList();
        }

        public void Dispose() => Session.Stop();
    }
}
