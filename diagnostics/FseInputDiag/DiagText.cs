using System;
using System.Globalization;
using System.Text;

namespace FseInputDiag;

public static class DiagText
{
    public const int DUp = 0;
    public const int DDown = 1;
    public const int DLeft = 2;
    public const int DRight = 3;
    public const int Start = 4;
    public const int Back = 5;
    public const int L3 = 6;
    public const int R3 = 7;
    public const int L1 = 8;
    public const int R1 = 9;
    public const int B1 = 10;
    public const int B2 = 11;
    public const int B3 = 12;
    public const int B4 = 13;
    public const int L2Soft = 14;
    public const int R2Soft = 15;
    public const int L2Full = 16;
    public const int R2Full = 17;
    public const int Special = 18;
    public const int L4 = 19;
    public const int R4 = 20;

    private static readonly string[] ButtonNames =
    [
        "DUp", "DDown", "DLeft", "DRight", "Start", "Back", "L3", "R3",
        "L1", "R1", "B1", "B2", "B3", "B4", "L2Soft", "R2Soft", "L2Full", "R2Full",
        "Special", "L4", "R4",
    ];

    public static uint Fnv1a32(string? text)
    {
        uint hash = 2166136261;
        if (string.IsNullOrEmpty(text))
            return hash;

        foreach (char ch in text)
        {
            hash ^= (byte)ch;
            hash *= 16777619;
            hash ^= (byte)(ch >> 8);
            hash *= 16777619;
        }

        return hash;
    }

    public static string? SanitizeTail(string? value)
    {
        string? segment = LastSegment(value);
        return Keep(segment, 16, allowPunctuation: false);
    }

    public static string? SanitizeExe(string? value)
    {
        string? segment = LastSegment(value);
        return Keep(segment, 32, allowPunctuation: true);
    }

    public static int ParseDelayMs(string? text, DiagLimits limits)
    {
        int min = Math.Clamp(limits.MinDelayMs, DiagLimits.HardMinDelayMs, DiagLimits.HardMaxDelayMs);
        int max = Math.Clamp(limits.MaxDelayMs, min, DiagLimits.HardMaxDelayMs);
        int fallback = Math.Clamp(limits.DefaultDelayMs, min, max);
        if (string.IsNullOrWhiteSpace(text))
            return fallback;

        int start = -1;
        int end = -1;
        for (int i = 0; i < text.Length && i < 64; i++)
        {
            if (text[i] is >= '0' and <= '9')
            {
                if (start < 0)
                    start = i;
                end = i;
            }
            else if (start >= 0)
            {
                break;
            }
        }

        if (start < 0 || !int.TryParse(text.AsSpan(start, end - start + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int seconds))
            return fallback;

        long ms = (long)seconds * 1000L;
        if (ms < min)
            return min;
        if (ms > max)
            return max;
        return (int)ms;
    }

    public static string FormatSample(DiagSample sample)
    {
        var sb = new StringBuilder(256);
        sb.Append("{\"kind\":\"");
        sb.Append(sample.Stage switch
        {
            DiagStage.Raw => "raw",
            DiagStage.Map => "map",
            _ => "send",
        });
        sb.Append("\",\"f\":").Append(sample.Frame);
        sb.Append(",\"ms\":").Append(sample.Ms);

        switch (sample.Stage)
        {
            case DiagStage.Raw:
                sb.Append(",\"present\":").Append(sample.Present ? "true" : "false");
                sb.Append(",\"be\":\"").Append(Backend(sample.Backend)).Append('"');
                sb.Append(",\"alias\":\"").Append(Alias(sample.Alias)).Append('"');
                sb.Append(",\"ui\":").Append(sample.UserIndex);
                sb.Append(",\"pnpUi\":").Append(sample.PnpUserIndex);
                sb.Append(",\"vid\":").Append(sample.Vid);
                sb.Append(",\"pid\":").Append(sample.Pid);
                sb.Append(",\"tail\":").Append(JsonToken(SanitizeTail(sample.Tail)));
                sb.Append(",\"inst\":").Append(sample.InstanceHash);
                sb.Append(",\"virt\":").Append(sample.IsVirtual ? "true" : "false");
                sb.Append(",\"dummy\":").Append(sample.IsDummy ? "true" : "false");
                sb.Append(",\"xinput\":").Append(sample.IsXInput ? "true" : "false");
                sb.Append(",\"vidPidMatch\":").Append(sample.VidPidMatch ? "true" : "false");
                sb.Append(",\"sdlType\":").Append(sample.SdlType);
                sb.Append(",\"res\":\"").Append(Read(sample.ReadResult)).Append('"');
                sb.Append(",\"err\":").Append(sample.ErrorCode);
                sb.Append(",\"skip\":\"").Append(Skip(sample.Skip)).Append('"');
                AppendAxes(sb, sample.Buttons, sample.Lx, sample.Ly, sample.Rx, sample.Ry, sample.L2, sample.R2, output: false);
                break;
            case DiagStage.Map:
                sb.Append(",\"present\":").Append(sample.Present ? "true" : "false");
                sb.Append(",\"publishLive\":").Append(sample.PublishLive ? "true" : "false");
                sb.Append(",\"halt\":\"").Append(Halt(sample.Halt)).Append('"');
                AppendAxes(sb, sample.Buttons, sample.Lx, sample.Ly, sample.Rx, sample.Ry, sample.L2, sample.R2, output: false);
                sb.Append(",\"outBtn\":\"").Append(FormatButtons(sample.OutButtons)).Append('"');
                sb.Append(",\"outLx\":").Append(sample.OutLx);
                sb.Append(",\"outLy\":").Append(sample.OutLy);
                sb.Append(",\"outRx\":").Append(sample.OutRx);
                sb.Append(",\"outRy\":").Append(sample.OutRy);
                sb.Append(",\"outL2\":").Append(sample.OutL2);
                sb.Append(",\"outR2\":").Append(sample.OutR2);
                sb.Append(",\"mute\":").Append(sample.Mute ? "true" : "false");
                sb.Append(",\"listen\":").Append(sample.Listening ? "true" : "false");
                sb.Append(",\"focus\":").Append(sample.Focus ? "true" : "false");
                sb.Append(",\"fgPid\":").Append(sample.ForegroundPid);
                sb.Append(",\"fg\":").Append(JsonToken(SanitizeExe(sample.ForegroundExe)));
                sb.Append(",\"hid\":").Append(sample.HidStatus);
                sb.Append(",\"mode\":").Append(sample.HidMode);
                sb.Append(",\"vVid\":").Append(sample.VirtualVid);
                sb.Append(",\"vPid\":").Append(sample.VirtualPid);
                sb.Append(",\"vConn\":").Append(sample.VirtualConnected ? "true" : "false");
                break;
            default:
                sb.Append(",\"decision\":\"").Append(Decision(sample.Send)).Append('"');
                break;
        }

        sb.Append('}');
        return sb.ToString();
    }

    public static string FormatButtons(uint mask)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < ButtonNames.Length; i++)
        {
            if ((mask & (1u << i)) == 0)
                continue;
            if (sb.Length > 0)
                sb.Append('+');
            sb.Append(ButtonNames[i]);
        }

        return sb.ToString();
    }

    private static void AppendAxes(StringBuilder sb, uint buttons, short lx, short ly, short rx, short ry, short l2, short r2, bool output)
    {
        _ = output;
        sb.Append(",\"btn\":\"").Append(FormatButtons(buttons)).Append('"');
        sb.Append(",\"lx\":").Append(lx);
        sb.Append(",\"ly\":").Append(ly);
        sb.Append(",\"rx\":").Append(rx);
        sb.Append(",\"ry\":").Append(ry);
        sb.Append(",\"l2\":").Append(l2);
        sb.Append(",\"r2\":").Append(r2);
    }

    private static string JsonToken(string? sanitized)
    {
        return sanitized is null ? "null" : "\"" + sanitized + "\"";
    }

    private static string? LastSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        int cut = value.LastIndexOfAny(['\\', '/']);
        return cut >= 0 ? value[(cut + 1)..] : value;
    }

    private static string? Keep(string? value, int max, bool allowPunctuation)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (char c in value)
        {
            bool ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
            if (!ok && allowPunctuation)
                ok = c is '.' or '_' or '-';
            if (!ok)
                continue;
            sb.Append(c);
            if (sb.Length == max)
                break;
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static string Backend(DiagBackend backend) => backend switch
    {
        DiagBackend.XInput => "xinput",
        DiagBackend.Sdl => "sdl",
        _ => "none",
    };

    private static string Alias(DiagAlias alias) => alias switch
    {
        DiagAlias.Oxp => "oxp",
        DiagAlias.Switch => "switch",
        DiagAlias.XInput => "xinput",
        DiagAlias.Sdl => "sdl",
        _ => "none",
    };

    private static string Read(DiagReadResult result) => result switch
    {
        DiagReadResult.Ok => "ok",
        DiagReadResult.Failed => "failed",
        DiagReadResult.NullController => "null",
        DiagReadResult.Skipped => "skipped",
        _ => "missing",
    };

    private static string Skip(DiagSkipReason reason) => reason switch
    {
        DiagSkipReason.InputsNull => "inputs",
        DiagSkipReason.Busy => "busy",
        DiagSkipReason.NotPlugged => "unplugged",
        DiagSkipReason.Disposing => "disposing",
        DiagSkipReason.NotConnected => "disconnected",
        _ => "none",
    };

    private static string Halt(DiagHalt halt) => halt switch
    {
        DiagHalt.NoTarget => "no_target",
        DiagHalt.NullState => "null_state",
        DiagHalt.Broadcast => "broadcast",
        _ => "none",
    };

    public static string Decision(DiagSendDecision decision) => decision switch
    {
        DiagSendDecision.Listening => "listening",
        DiagSendDecision.Sleep => "sleep",
        DiagSendDecision.Unchanged => "unchanged",
        DiagSendDecision.NullTarget => "null_target",
        DiagSendDecision.Disconnected => "disconnected",
        DiagSendDecision.Sent => "sent",
        DiagSendDecision.SendError => "send_error",
        _ => "not_reached",
    };
}
