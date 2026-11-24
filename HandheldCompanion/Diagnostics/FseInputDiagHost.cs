using FseInputDiag;
using HandheldCompanion.Controllers;
using HandheldCompanion.Controllers.SDL;
using HandheldCompanion.Inputs;
using HandheldCompanion.Managers;
using HandheldCompanion.Shared;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace HandheldCompanion.Diagnostics;

/// <summary>
/// Opt-in, time-boxed trace of one controller tick. Disabled until an arm file appears.
/// Hooks must stay allocation-free and side-effect free while no frame is open.
/// </summary>
internal static class FseInputDiagHost
{
    internal const string BaseSha = "0a0a13b937ed987a131a0729226a03de374e7d91";
    private const string ArmFileName = "fse-input-diag.arm";
    private const string SampleFileName = "fse-input-diag.jsonl";
    private const string SummaryFileName = "fse-input-diag.summary.txt";

    [ThreadStatic] private static uint t_frame;
    [ThreadStatic] private static FrameBag? t_bag;

    private static readonly long Frequency = Stopwatch.Frequency;
    private static FseInputDiagSession? _session;
    private static FileSystemWatcher? _watcher;
    private static Timer? _pump;
    private static string? _directory;
    private static int _armBusy;
    private static int _started;
    private static int _frameSeq;

    public static bool IsFrameOpen
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => t_frame != 0;
    }

    public static void Start(string? directory)
    {
        try
        {
            if (Interlocked.Exchange(ref _started, 1) == 1)
                return;
            if (string.IsNullOrWhiteSpace(directory))
                return;

            Directory.CreateDirectory(directory);
            _directory = directory;
            _session = new FseInputDiagSession(new DiagLimits(), NowMs, OpenStreams, backgroundWriter: true);

            _watcher = new FileSystemWatcher(directory, ArmFileName)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Created += OnArmSignal;
            _watcher.Changed += OnArmSignal;
            _watcher.Renamed += OnArmSignal;

            if (File.Exists(Path.Combine(directory, ArmFileName)))
                ScheduleRead();
        }
        catch
        {
            // A failed watcher must not stop controller startup.
        }
    }

    public static void Stop()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Created -= OnArmSignal;
                _watcher.Changed -= OnArmSignal;
                _watcher.Renamed -= OnArmSignal;
                _watcher.Dispose();
                _watcher = null;
            }

            try
            {
                _pump?.Change(Timeout.Infinite, Timeout.Infinite);
                _pump?.Dispose();
            }
            catch
            {
                // The pump timer may already be disposed.
            }
            _pump = null;
            _session?.Stop();
            t_frame = 0;
            t_bag = null;
        }
        catch
        {
            // Diagnostic shutdown must not change controller disposal.
        }
    }

    public static uint BeginFrame()
    {
        try
        {
            FseInputDiagSession? session = _session;
            if (session is not { IsCapturing: true })
            {
                t_frame = 0;
                t_bag = null;
                return 0;
            }

            session.Pump();
            if (!session.IsCapturing)
            {
                t_frame = 0;
                t_bag = null;
                return 0;
            }

            uint id = (uint)Interlocked.Increment(ref _frameSeq);
            t_frame = id;
            t_bag = new FrameBag
            {
                Frame = id,
                Ms = session.ElapsedMs(),
            };
            return id;
        }
        catch
        {
            t_frame = 0;
            t_bag = null;
            return 0;
        }
    }

    public static void EndFrame(uint frame)
    {
        if (frame == 0)
            return;

        try
        {
            FrameBag? bag = t_bag;
            t_bag = null;
            t_frame = 0;
            if (bag is null || bag.Frame != frame)
                return;

            FseInputDiagSession? session = _session;
            if (session is null)
                return;

            session.TryEnqueue(bag.ToRaw());
            session.TryEnqueue(bag.ToMap());
            session.TryEnqueue(bag.ToSend());
        }
        catch
        {
            t_bag = null;
            t_frame = 0;
        }
    }

    public static void NotePublish(bool publishLive)
    {
        try
        {
            if (t_bag is { } bag)
                bag.PublishLive = publishLive;
        }
        catch
        {
            // Ignore diagnostic failures.
        }
    }

    public static void NoteHalt(DiagHalt halt)
    {
        try
        {
            if (t_bag is { } bag)
                bag.Halt = halt;
        }
        catch
        {
            // Ignore diagnostic failures.
        }
    }

    public static void NoteRaw(IController controller, DiagReadResult result, int error, DiagSkipReason skip)
    {
        try
        {
            if (t_bag is not { } bag)
                return;

            bag.HasRaw = true;
            bag.ReadResult = result;
            bag.ErrorCode = error;
            bag.Skip = skip;
            bag.Backend = controller is SDLController ? DiagBackend.Sdl : controller is XInputController ? DiagBackend.XInput : DiagBackend.None;
            bag.Alias = controller switch
            {
                OneXPlayerX2Controller => DiagAlias.Oxp,
                NintendoSwitchProController => DiagAlias.Switch,
                XInputController => DiagAlias.XInput,
                SDLController => DiagAlias.Sdl,
                _ => DiagAlias.None,
            };

            try
            {
                int userIndex = controller.GetUserIndex();
                bag.UserIndex = (uint)userIndex <= byte.MaxValue ? (byte)userIndex : byte.MaxValue;
                bag.IsVirtual = controller.IsVirtual();
                bag.IsDummy = controller.IsDummy();
                bag.IsXInput = controller.IsXInput();
                bag.Vid = controller.GetVendorID();
                bag.Pid = controller.GetProductID();
                bag.PnpUserIndex = controller.Details?.XInputUserIndex ?? byte.MaxValue;
                string instanceId = controller.GetInstanceId() ?? string.Empty;
                bag.InstanceHash = DiagText.Fnv1a32(instanceId);
                bag.Tail = DiagText.SanitizeTail(instanceId);
                if (controller is SDLController sdl)
                    bag.SdlType = (int)sdl.GamepadType;
                bag.VidPidMatch = bag.IsVirtual && bag.Vid == VirtualManager.VendorId && bag.Pid == VirtualManager.ProductId;
            }
            catch
            {
                // Identity is best-effort; the read result above still stands.
            }

            try
            {
                ControllerState? inputs = controller.Inputs;
                if (inputs is not null)
                {
                    bag.Buttons = Pack(inputs.ButtonState);
                    ReadAxes(inputs.AxisState, out bag.Lx, out bag.Ly, out bag.Rx, out bag.Ry, out bag.L2, out bag.R2);
                }
            }
            catch
            {
                // Leave axes at zero when the state cannot be read.
            }
        }
        catch
        {
            // Never break the controller read.
        }
    }

    public static void NoteLayout(ControllerState state)
    {
        try
        {
            if (t_bag is not { } bag || state is null)
                return;
            bag.HasLayout = true;
            bag.LayoutButtons = Pack(state.ButtonState);
            ReadAxes(state.AxisState, out bag.LayoutLx, out bag.LayoutLy, out bag.LayoutRx, out bag.LayoutRy, out bag.LayoutL2, out bag.LayoutR2);
        }
        catch
        {
            // Ignore diagnostic failures.
        }
    }

    public static void NoteOutput(ControllerState state, bool mute, bool listening, bool focus, int foregroundPid, string? foregroundExe, int hidStatus, int hidMode, ushort virtualVid, ushort virtualPid, bool virtualConnected)
    {
        try
        {
            if (t_bag is not { } bag || state is null)
                return;
            bag.HasOutput = true;
            bag.OutButtons = Pack(state.ButtonState);
            ReadAxes(state.AxisState, out bag.OutLx, out bag.OutLy, out bag.OutRx, out bag.OutRy, out bag.OutL2, out bag.OutR2);
            bag.Mute = mute;
            bag.Listening = listening;
            bag.Focus = focus;
            bag.ForegroundPid = foregroundPid;
            bag.ForegroundExe = DiagText.SanitizeExe(foregroundExe);
            bag.HidStatus = hidStatus;
            bag.HidMode = hidMode;
            bag.VirtualVid = virtualVid;
            bag.VirtualPid = virtualPid;
            bag.VirtualConnected = virtualConnected;
        }
        catch
        {
            // Ignore diagnostic failures.
        }
    }

    public static void NoteSend(DiagSendDecision decision)
    {
        try
        {
            if (t_bag is not { } bag || bag.HasSend)
                return;
            bag.HasSend = true;
            bag.Send = decision;
        }
        catch
        {
            // Ignore diagnostic failures.
        }
    }

    private static void OnArmSignal(object sender, FileSystemEventArgs e) => ScheduleRead();

    private static void ScheduleRead()
    {
        if (Interlocked.CompareExchange(ref _armBusy, 1, 0) != 0)
            return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                Thread.Sleep(300);
                if (!TryConsumeArm(out string text) || _session is not { } session || !session.TryArm(text, BaseSha))
                {
                    Interlocked.Exchange(ref _armBusy, 0);
                    return;
                }

                StartPump();
                try
                {
                    LogManager.LogInformation("FSE input diagnostic armed");
                }
                catch
                {
                    // Logging is optional.
                }
            }
            catch
            {
                Interlocked.Exchange(ref _armBusy, 0);
            }
        });
    }

    private static bool TryConsumeArm(out string text)
    {
        text = string.Empty;
        try
        {
            if (string.IsNullOrEmpty(_directory))
                return false;
            string path = Path.Combine(_directory, ArmFileName);
            if (!File.Exists(path))
                return false;

            byte[] buffer = new byte[64];
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                text = System.Text.Encoding.ASCII.GetString(buffer, 0, read);
            }

            try
            {
                File.Delete(path);
            }
            catch
            {
                // A leftover arm file is ignored while this capture is busy.
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void StartPump()
    {
        Timer? timer = _pump;
        if (timer is null)
        {
            _pump = new Timer(OnPump, null, 200, 200);
            return;
        }

        try
        {
            timer.Change(200, 200);
        }
        catch
        {
            _pump = new Timer(OnPump, null, 200, 200);
        }
    }

    private static void OnPump(object? state)
    {
        try
        {
            FseInputDiagSession? session = _session;
            if (session is null)
                return;
            session.Pump();
            if (session.State is DiagSessionState.Finished or DiagSessionState.Idle)
            {
                Interlocked.Exchange(ref _armBusy, 0);
                try
                {
                    _pump?.Change(Timeout.Infinite, Timeout.Infinite);
                }
                catch
                {
                    // Ignore a disposed timer.
                }
            }
        }
        catch
        {
            // The pump must not take down the process.
        }
    }

    private static DiagStreams OpenStreams()
    {
        string directory = _directory ?? ".";
        FileStream samples = new(Path.Combine(directory, SampleFileName), FileMode.Create, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
        FileStream summary = new(Path.Combine(directory, SummaryFileName), FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.SequentialScan);
        return new DiagStreams(samples, summary);
    }

    private static long NowMs()
    {
        return Stopwatch.GetTimestamp() * 1000L / Frequency;
    }

    private static uint Pack(ButtonState buttons)
    {
        uint mask = 0;
        if (buttons[ButtonFlags.DPadUp]) mask |= 1u << DiagText.DUp;
        if (buttons[ButtonFlags.DPadDown]) mask |= 1u << DiagText.DDown;
        if (buttons[ButtonFlags.DPadLeft]) mask |= 1u << DiagText.DLeft;
        if (buttons[ButtonFlags.DPadRight]) mask |= 1u << DiagText.DRight;
        if (buttons[ButtonFlags.Start]) mask |= 1u << DiagText.Start;
        if (buttons[ButtonFlags.Back]) mask |= 1u << DiagText.Back;
        if (buttons[ButtonFlags.LeftStickClick]) mask |= 1u << DiagText.L3;
        if (buttons[ButtonFlags.RightStickClick]) mask |= 1u << DiagText.R3;
        if (buttons[ButtonFlags.L1]) mask |= 1u << DiagText.L1;
        if (buttons[ButtonFlags.R1]) mask |= 1u << DiagText.R1;
        if (buttons[ButtonFlags.B1]) mask |= 1u << DiagText.B1;
        if (buttons[ButtonFlags.B2]) mask |= 1u << DiagText.B2;
        if (buttons[ButtonFlags.B3]) mask |= 1u << DiagText.B3;
        if (buttons[ButtonFlags.B4]) mask |= 1u << DiagText.B4;
        if (buttons[ButtonFlags.L2Soft]) mask |= 1u << DiagText.L2Soft;
        if (buttons[ButtonFlags.R2Soft]) mask |= 1u << DiagText.R2Soft;
        if (buttons[ButtonFlags.L2Full]) mask |= 1u << DiagText.L2Full;
        if (buttons[ButtonFlags.R2Full]) mask |= 1u << DiagText.R2Full;
        if (buttons[ButtonFlags.Special]) mask |= 1u << DiagText.Special;
        if (buttons[ButtonFlags.L4]) mask |= 1u << DiagText.L4;
        if (buttons[ButtonFlags.R4]) mask |= 1u << DiagText.R4;
        return mask;
    }

    private static void ReadAxes(AxisState axes, out short lx, out short ly, out short rx, out short ry, out short l2, out short r2)
    {
        lx = axes[AxisFlags.LeftStickX];
        ly = axes[AxisFlags.LeftStickY];
        rx = axes[AxisFlags.RightStickX];
        ry = axes[AxisFlags.RightStickY];
        l2 = axes[AxisFlags.L2];
        r2 = axes[AxisFlags.R2];
    }

    private sealed class FrameBag
    {
        public uint Frame;
        public long Ms;
        public bool HasRaw;
        public DiagReadResult ReadResult;
        public int ErrorCode;
        public DiagSkipReason Skip;
        public DiagBackend Backend;
        public DiagAlias Alias;
        public byte UserIndex;
        public byte PnpUserIndex;
        public ushort Vid;
        public ushort Pid;
        public string? Tail;
        public uint InstanceHash;
        public bool IsVirtual;
        public bool IsDummy;
        public bool IsXInput;
        public bool VidPidMatch;
        public int SdlType;
        public uint Buttons;
        public short Lx;
        public short Ly;
        public short Rx;
        public short Ry;
        public short L2;
        public short R2;
        public bool PublishLive;
        public DiagHalt Halt;
        public bool HasLayout;
        public uint LayoutButtons;
        public short LayoutLx;
        public short LayoutLy;
        public short LayoutRx;
        public short LayoutRy;
        public short LayoutL2;
        public short LayoutR2;
        public bool HasOutput;
        public uint OutButtons;
        public short OutLx;
        public short OutLy;
        public short OutRx;
        public short OutRy;
        public short OutL2;
        public short OutR2;
        public bool Mute;
        public bool Listening;
        public bool Focus;
        public int ForegroundPid;
        public string? ForegroundExe;
        public int HidStatus;
        public int HidMode;
        public ushort VirtualVid;
        public ushort VirtualPid;
        public bool VirtualConnected;
        public bool HasSend;
        public DiagSendDecision Send;

        public DiagSample ToRaw() => new()
        {
            Stage = DiagStage.Raw,
            Frame = Frame,
            Ms = Ms,
            Present = HasRaw,
            Backend = Backend,
            Alias = Alias,
            UserIndex = UserIndex,
            PnpUserIndex = PnpUserIndex,
            Vid = Vid,
            Pid = Pid,
            Tail = Tail,
            InstanceHash = InstanceHash,
            IsVirtual = IsVirtual,
            IsDummy = IsDummy,
            IsXInput = IsXInput,
            VidPidMatch = VidPidMatch,
            SdlType = SdlType,
            ReadResult = HasRaw ? ReadResult : DiagReadResult.Missing,
            ErrorCode = ErrorCode,
            Skip = Skip,
            Buttons = Buttons,
            Lx = Lx,
            Ly = Ly,
            Rx = Rx,
            Ry = Ry,
            L2 = L2,
            R2 = R2,
        };

        public DiagSample ToMap() => new()
        {
            Stage = DiagStage.Map,
            Frame = Frame,
            Ms = Ms,
            Present = HasLayout,
            PublishLive = PublishLive,
            Halt = Halt,
            Buttons = LayoutButtons,
            Lx = LayoutLx,
            Ly = LayoutLy,
            Rx = LayoutRx,
            Ry = LayoutRy,
            L2 = LayoutL2,
            R2 = LayoutR2,
            OutButtons = OutButtons,
            OutLx = OutLx,
            OutLy = OutLy,
            OutRx = OutRx,
            OutRy = OutRy,
            OutL2 = OutL2,
            OutR2 = OutR2,
            Mute = Mute,
            Listening = Listening,
            Focus = Focus,
            ForegroundPid = ForegroundPid,
            ForegroundExe = ForegroundExe,
            HidStatus = HidStatus,
            HidMode = HidMode,
            VirtualVid = VirtualVid,
            VirtualPid = VirtualPid,
            VirtualConnected = VirtualConnected,
        };

        public DiagSample ToSend() => new()
        {
            Stage = DiagStage.Send,
            Frame = Frame,
            Ms = Ms,
            Send = HasSend ? Send : DiagSendDecision.NotReached,
        };
    }
}
