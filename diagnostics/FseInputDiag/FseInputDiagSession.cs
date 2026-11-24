using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace FseInputDiag;

public sealed class FseInputDiagSession
{
    private readonly DiagLimits _limits;
    private readonly Func<long> _nowMs;
    private readonly Func<DiagStreams> _open;
    private readonly bool _backgroundWriter;
    private readonly int _durationMs;
    private readonly int _maxSamples;
    private readonly int _maxBytes;
    private readonly int _maxQueue;
    private readonly object _sync = new();

    private BlockingCollection<DiagSample> _queue;
    private DiagStreams? _output;
    private StreamWriter? _samples;
    private StreamWriter? _summary;
    private Thread? _writer;
    private int _state = (int)DiagSessionState.Idle;
    private long _armDeadline;
    private long _captureStart;
    private long _captureDeadline;
    private long _elapsedAtFinish;
    private string _reason = "idle";
    private int _accepted;
    private int _written;
    private int _writtenBytes;
    private int _queueDrops;
    private int _sampleDrops;
    private int _byteDrops;
    private int _bytesExhausted;
    private int _headerWritten;
    private int _footerWritten;
    private string _baseSha = "";

    public FseInputDiagSession(DiagLimits? limits, Func<long> nowMs, Func<DiagStreams> open, bool backgroundWriter)
    {
        _limits = limits ?? new DiagLimits();
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
        _open = open ?? throw new ArgumentNullException(nameof(open));
        _backgroundWriter = backgroundWriter;
        _durationMs = Math.Clamp(_limits.MaxDurationMs, 1, DiagLimits.HardMaxDurationMs);
        _maxSamples = Math.Clamp(_limits.MaxSamples, 1, DiagLimits.HardMaxSamples);
        _maxBytes = Math.Clamp(_limits.MaxOutputBytes, 1, DiagLimits.HardMaxOutputBytes);
        _maxQueue = Math.Clamp(_limits.MaxQueue, 1, DiagLimits.HardMaxQueue);
        _queue = new BlockingCollection<DiagSample>(_maxQueue);
    }

    public DiagSessionState State => (DiagSessionState)Volatile.Read(ref _state);
    public bool IsCapturing => State == DiagSessionState.Capturing;
    public int EffectiveDurationMs => _durationMs;
    public int EffectiveMaxSamples => _maxSamples;
    public int EffectiveMaxBytes => _maxBytes;
    public int EffectiveMaxQueue => _maxQueue;
    public long CaptureDeadlineMs => Volatile.Read(ref _captureDeadline);
    public long ArmDeadlineMs => Volatile.Read(ref _armDeadline);

    public long ElapsedMs()
    {
        long start = Volatile.Read(ref _captureStart);
        if (start == 0)
            return 0;
        return Math.Max(0, SafeNow() - start);
    }
    public int QueueDrops => Volatile.Read(ref _queueDrops);
    public int SampleDrops => Volatile.Read(ref _sampleDrops);
    public int ByteDrops => Volatile.Read(ref _byteDrops);
    public int WrittenSamples => Volatile.Read(ref _written);
    public int WrittenBytes => Volatile.Read(ref _writtenBytes);
    public string BaseSha => _baseSha;

    public bool TryArm(string? armText, string baseSha)
    {
        try
        {
            int delay = DiagText.ParseDelayMs(armText, _limits);
            lock (_sync)
            {
                if (State is DiagSessionState.Armed or DiagSessionState.Capturing)
                    return false;
                if (_writer is { IsAlive: true })
                    return false;

                ResetUnlocked();
                _baseSha = baseSha ?? "";
                Volatile.Write(ref _armDeadline, SafeNow() + delay);
                Volatile.Write(ref _state, (int)DiagSessionState.Armed);
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    public void Pump()
    {
        try
        {
            var state = State;
            long now = SafeNow();
            if (state == DiagSessionState.Armed)
            {
                lock (_sync)
                {
                    if (State == DiagSessionState.Armed && now >= _armDeadline)
                        EnterCaptureUnlocked(now);
                }
            }
            else if (state == DiagSessionState.Capturing && now >= Volatile.Read(ref _captureDeadline))
            {
                Finish("duration");
            }
        }
        catch
        {
            // Diagnostic timing must not escape into the caller.
        }
    }

    public bool TryEnqueue(DiagSample sample)
    {
        try
        {
            if (Volatile.Read(ref _state) != (int)DiagSessionState.Capturing)
                return false;

            long now = SafeNow();
            if (now >= Volatile.Read(ref _captureDeadline))
            {
                Finish("duration");
                return false;
            }

            if (Volatile.Read(ref _bytesExhausted) == 1)
            {
                Interlocked.Increment(ref _byteDrops);
                return false;
            }

            if (Volatile.Read(ref _accepted) >= _maxSamples)
            {
                Interlocked.Increment(ref _sampleDrops);
                return false;
            }

            if (!_queue.TryAdd(sample))
            {
                Interlocked.Increment(ref _queueDrops);
                return false;
            }

            Interlocked.Increment(ref _accepted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Flush()
    {
        try
        {
            if (_backgroundWriter)
                return;
            WriteAvailable();
            if (State == DiagSessionState.Finished)
                WriteFooterOnce();
        }
        catch
        {
            Volatile.Write(ref _bytesExhausted, 1);
        }
    }

    public void Stop()
    {
        try
        {
            lock (_sync)
            {
                if (State == DiagSessionState.Armed)
                {
                    Volatile.Write(ref _state, (int)DiagSessionState.Idle);
                    _reason = "cancelled";
                    CompleteQueue();
                    return;
                }
            }

            if (State == DiagSessionState.Capturing)
                Finish("stop");

            if (!_backgroundWriter)
                Flush();
            else
                _writer?.Join(TimeSpan.FromMilliseconds(500));
        }
        catch
        {
            // Disposal of the diagnostic session must not break controller shutdown.
        }
    }

    private void EnterCaptureUnlocked(long now)
    {
        Volatile.Write(ref _captureStart, now);
        Volatile.Write(ref _captureDeadline, now + _durationMs);
        _reason = "duration";
        Volatile.Write(ref _state, (int)DiagSessionState.Capturing);
        if (_backgroundWriter)
        {
            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "fse-input-diag",
            };
            _writer.Start();
        }
    }

    private void WriterLoop()
    {
        try
        {
            foreach (DiagSample sample in _queue.GetConsumingEnumerable())
                WriteSample(sample);
            WriteFooterOnce();
        }
        catch
        {
            Volatile.Write(ref _bytesExhausted, 1);
            try
            {
                while (_queue.TryTake(out _))
                    Interlocked.Increment(ref _byteDrops);
            }
            catch
            {
                // Ignore a queue that was already completed.
            }
        }
    }

    private void Finish(string reason)
    {
        long elapsed = Math.Max(0, SafeNow() - Volatile.Read(ref _captureStart));
        if (Interlocked.CompareExchange(ref _state, (int)DiagSessionState.Finished, (int)DiagSessionState.Capturing) != (int)DiagSessionState.Capturing)
            return;

        _reason = reason;
        Volatile.Write(ref _elapsedAtFinish, elapsed);
        CompleteQueue();
    }

    private void WriteAvailable()
    {
        while (_queue.TryTake(out DiagSample? sample))
            WriteSample(sample);
    }

    private void WriteSample(DiagSample sample)
    {
        EnsureWriters();
        if (_samples is null || Volatile.Read(ref _bytesExhausted) == 1)
        {
            Interlocked.Increment(ref _byteDrops);
            return;
        }

        string line = DiagText.FormatSample(sample);
        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (Volatile.Read(ref _writtenBytes) > _maxBytes - bytes)
        {
            Volatile.Write(ref _bytesExhausted, 1);
            Interlocked.Increment(ref _byteDrops);
            return;
        }

        _samples.WriteLine(line);
        Interlocked.Add(ref _writtenBytes, bytes);
        Interlocked.Increment(ref _written);
    }

    private void EnsureWriters()
    {
        if (_samples is not null)
            return;

        _output = _open();
        _samples = new StreamWriter(_output.Samples, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n",
        };
        _summary = new StreamWriter(_output.Summary, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true)
        {
            AutoFlush = false,
            NewLine = "\n",
        };
        WriteHeader();
    }

    private void WriteHeader()
    {
        if (Interlocked.Exchange(ref _headerWritten, 1) == 1 || _samples is null)
            return;

        string line =
            "{\"kind\":\"header\",\"v\":1,\"base\":\"" + SafeSha(_baseSha) +
            "\",\"maxMs\":" + _durationMs.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"maxSamples\":" + _maxSamples.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"maxBytes\":" + _maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"maxQueue\":" + _maxQueue.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (bytes > _maxBytes)
        {
            Volatile.Write(ref _bytesExhausted, 1);
            Interlocked.Increment(ref _byteDrops);
            return;
        }

        _samples.WriteLine(line);
        Interlocked.Add(ref _writtenBytes, bytes);
    }

    private void WriteFooterOnce()
    {
        if (Interlocked.Exchange(ref _footerWritten, 1) == 1)
            return;

        EnsureWriters();
        if (_samples is null)
            return;

        long elapsed = Volatile.Read(ref _elapsedAtFinish);
        if (elapsed == 0 && _captureStart != 0)
            elapsed = Math.Max(0, SafeNow() - _captureStart);

        string reason = string.IsNullOrEmpty(_reason) ? "stop" : _reason;
        string line =
            "{\"kind\":\"footer\",\"reason\":\"" + reason +
            "\",\"written\":" + Volatile.Read(ref _written).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"bytes\":" + Volatile.Read(ref _writtenBytes).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"queueDrops\":" + Volatile.Read(ref _queueDrops).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"sampleDrops\":" + Volatile.Read(ref _sampleDrops).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"byteDrops\":" + Volatile.Read(ref _byteDrops).ToString(System.Globalization.CultureInfo.InvariantCulture) +
            ",\"elapsedMs\":" + elapsed.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";

        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
        if (Volatile.Read(ref _writtenBytes) <= _maxBytes - bytes)
        {
            _samples.WriteLine(line);
            Interlocked.Add(ref _writtenBytes, bytes);
        }
        else
        {
            Interlocked.Increment(ref _byteDrops);
        }

        _samples.Flush();
        if (_summary is not null)
        {
            _summary.WriteLine("base=" + SafeSha(_baseSha));
            _summary.WriteLine("reason=" + reason);
            _summary.WriteLine("written=" + Volatile.Read(ref _written).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _summary.WriteLine("bytes=" + Volatile.Read(ref _writtenBytes).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _summary.WriteLine("queueDrops=" + Volatile.Read(ref _queueDrops).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _summary.WriteLine("sampleDrops=" + Volatile.Read(ref _sampleDrops).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _summary.WriteLine("byteDrops=" + Volatile.Read(ref _byteDrops).ToString(System.Globalization.CultureInfo.InvariantCulture));
            _summary.WriteLine("elapsedMs=" + elapsed.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _summary.Flush();
        }
    }

    private void ResetUnlocked()
    {
        CompleteQueue();
        _queue = new BlockingCollection<DiagSample>(_maxQueue);
        _output = null;
        _samples = null;
        _summary = null;
        _writer = null;
        Volatile.Write(ref _armDeadline, 0);
        Volatile.Write(ref _captureStart, 0);
        Volatile.Write(ref _captureDeadline, 0);
        _elapsedAtFinish = 0;
        _reason = "idle";
        Volatile.Write(ref _accepted, 0);
        Volatile.Write(ref _written, 0);
        Volatile.Write(ref _writtenBytes, 0);
        Volatile.Write(ref _queueDrops, 0);
        Volatile.Write(ref _sampleDrops, 0);
        Volatile.Write(ref _byteDrops, 0);
        Volatile.Write(ref _bytesExhausted, 0);
        Volatile.Write(ref _headerWritten, 0);
        Volatile.Write(ref _footerWritten, 0);
    }

    private void CompleteQueue()
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch
        {
            // Already completed.
        }
    }

    private long SafeNow()
    {
        try
        {
            return _nowMs();
        }
        catch
        {
            return 0;
        }
    }

    private static string SafeSha(string sha)
    {
        var sb = new StringBuilder(sha.Length);
        foreach (char c in sha)
        {
            if (c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')
                sb.Append(c);
            if (sb.Length == 64)
                break;
        }

        return sb.ToString();
    }
}
