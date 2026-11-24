using System;
using System.IO;

namespace FseInputDiag;

public enum DiagStage : byte
{
    Raw = 1,
    Map = 2,
    Send = 3,
}

public enum DiagBackend : byte
{
    None = 0,
    XInput = 1,
    Sdl = 2,
}

public enum DiagAlias : byte
{
    None = 0,
    Oxp = 1,
    Switch = 2,
    XInput = 3,
    Sdl = 4,
}

public enum DiagReadResult : byte
{
    Missing = 0,
    Ok = 1,
    Failed = 2,
    NullController = 3,
    Skipped = 4,
}

public enum DiagSkipReason : byte
{
    None = 0,
    InputsNull = 1,
    Busy = 2,
    NotPlugged = 3,
    Disposing = 4,
    NotConnected = 5,
}

public enum DiagSendDecision : byte
{
    NotReached = 0,
    Listening = 1,
    Sleep = 2,
    Unchanged = 3,
    NullTarget = 4,
    Disconnected = 5,
    Sent = 6,
    SendError = 7,
}

public enum DiagHalt : byte
{
    None = 0,
    NoTarget = 1,
    NullState = 2,
    Broadcast = 3,
}

public enum DiagSessionState : byte
{
    Idle = 0,
    Armed = 1,
    Capturing = 2,
    Finished = 3,
}

public sealed class DiagLimits
{
    public const int HardMaxDurationMs = 30_000;
    public const int HardMaxQueue = 512;
    public const int HardMaxSamples = 12_000;
    public const int HardMaxOutputBytes = 3_145_728;
    public const int HardMinDelayMs = 3_000;
    public const int HardMaxDelayMs = 20_000;
    public const int HardDefaultDelayMs = 12_000;

    public int MaxDurationMs { get; init; } = HardMaxDurationMs;
    public int MaxQueue { get; init; } = HardMaxQueue;
    public int MaxSamples { get; init; } = HardMaxSamples;
    public int MaxOutputBytes { get; init; } = HardMaxOutputBytes;
    public int MinDelayMs { get; init; } = HardMinDelayMs;
    public int MaxDelayMs { get; init; } = HardMaxDelayMs;
    public int DefaultDelayMs { get; init; } = HardDefaultDelayMs;
}

public sealed class DiagSample
{
    public DiagStage Stage { get; init; }
    public uint Frame { get; init; }
    public long Ms { get; init; }
    public bool Present { get; init; }
    public DiagBackend Backend { get; init; }
    public DiagAlias Alias { get; init; }
    public byte UserIndex { get; init; }
    public byte PnpUserIndex { get; init; }
    public ushort Vid { get; init; }
    public ushort Pid { get; init; }
    public string? Tail { get; init; }
    public uint InstanceHash { get; init; }
    public bool IsVirtual { get; init; }
    public bool IsDummy { get; init; }
    public bool IsXInput { get; init; }
    public bool VidPidMatch { get; init; }
    public int SdlType { get; init; }
    public DiagReadResult ReadResult { get; init; }
    public int ErrorCode { get; init; }
    public DiagSkipReason Skip { get; init; }
    public uint Buttons { get; init; }
    public short Lx { get; init; }
    public short Ly { get; init; }
    public short Rx { get; init; }
    public short Ry { get; init; }
    public short L2 { get; init; }
    public short R2 { get; init; }
    public bool PublishLive { get; init; }
    public uint OutButtons { get; init; }
    public short OutLx { get; init; }
    public short OutLy { get; init; }
    public short OutRx { get; init; }
    public short OutRy { get; init; }
    public short OutL2 { get; init; }
    public short OutR2 { get; init; }
    public bool Mute { get; init; }
    public bool Listening { get; init; }
    public bool Focus { get; init; }
    public int ForegroundPid { get; init; }
    public string? ForegroundExe { get; init; }
    public int HidStatus { get; init; }
    public int HidMode { get; init; }
    public ushort VirtualVid { get; init; }
    public ushort VirtualPid { get; init; }
    public bool VirtualConnected { get; init; }
    public DiagHalt Halt { get; init; }
    public DiagSendDecision Send { get; init; }
}

public sealed class DiagStreams : IDisposable
{
    public DiagStreams(Stream samples, Stream summary)
    {
        Samples = samples;
        Summary = summary;
    }

    public Stream Samples { get; }
    public Stream Summary { get; }

    public void Dispose()
    {
        Samples.Dispose();
        Summary.Dispose();
    }
}
