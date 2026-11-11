using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AgenticSdr.Dsp;

/// <summary>One line of VarAC / VARA traffic from the bundled xara monitor (or an optional external one).</summary>
public sealed record XaraMessage(string Text, string? Kind = null);

/// <summary>
/// Receive bridge for VarAC / VARA HF. Prefers the bundled MIT <c>xaramonitor</c> native library
/// (xara phy/arq/monitor). Falls back to an external <c>xara-monitor</c>/<c>zara-monitor</c> on PATH if present.
/// </summary>
public sealed class XaraMonitor : IDisposable
{
    public const int PreferredRate = 12_000;
    public const int BandwidthHz = 2_300;

    private readonly object _gate = new();
    private IntPtr _native;
    private Process? _process;
    private Stream? _stdin;
    private readonly List<string> _pending = [];
    private readonly byte[] _pcmScratch = new byte[64_000];
    private bool _started;
    private string? _binary;
    private string? _error;
    private bool _useNative;

    public bool Available
    {
        get
        {
            EnsureStarted();
            lock (_gate)
                return _useNative ? _native != IntPtr.Zero : _process is { HasExited: false };
        }
    }

    public string? MissingHint
    {
        get
        {
            EnsureStarted();
            if (_error is not null)
                return _error;
            if (_useNative || _binary is not null)
                return null;
            return "VarAC decode needs Host/native/xaramonitor.dll (rebuild native/xaramonitor) or xara-monitor on PATH.";
        }
    }

    public static string Format(IReadOnlyList<XaraMessage> messages)
    {
        if (messages.Count == 0)
            return "";
        var lines = new List<string>
        {
            "VarAC / VARA  (bundled xara monitor; ARQ text is all-or-nothing)"
        };
        foreach (XaraMessage message in messages)
        {
            lines.Add(message.Text);
            lines.Add($"           {Explain(message.Kind, message.Text)}");
        }

        return string.Join("\n", lines);
    }

    public static string Explain(string? kind, string? text)
    {
        string k = kind?.Trim().ToLowerInvariant() ?? "";
        string t = text ?? "";
        if (k.Contains("cq") || t.StartsWith("CQ ", StringComparison.OrdinalIgnoreCase))
            return "VarAC CQ / calling.";
        if (k.Contains("beacon") || t.Contains("BEACON", StringComparison.OrdinalIgnoreCase))
            return "VarAC beacon.";
        if (k.Contains("broadcast") || t.Contains("BROADCAST", StringComparison.OrdinalIgnoreCase))
            return "VarAC broadcast.";
        if (k.Contains("connect") || t.Contains("CONNECT", StringComparison.OrdinalIgnoreCase))
            return "VARA connect / link control.";
        if (k == "note")
            return "Monitor status.";
        return "VarAC / VARA traffic.";
    }

    public static IReadOnlyList<XaraMessage> Decode(ReadOnlySpan<float> audio, int sampleRate)
    {
        using var monitor = new XaraMonitor();
        if (!monitor.Available)
            return [];
        monitor.Push(audio, sampleRate);
        monitor.Finish();
        return monitor.Drain();
    }

    /// <summary>Decode or a one-line status when the monitor is missing or ARQ produced no text.</summary>
    public static string FormatAudio(ReadOnlySpan<float> audio, int sampleRate)
    {
        using var monitor = new XaraMonitor();
        if (!monitor.Available)
            return monitor.MissingHint ?? "VarAC decode needs Host/native/xaramonitor.dll.";
        monitor.Push(audio, sampleRate);
        monitor.Finish();
        IReadOnlyList<XaraMessage> messages = monitor.Drain();
        if (messages.Count > 0)
            return Format(messages);
        return "VarAC  (no complete ARQ text this period - channel energy alone is not a decode)";
    }

    public void Push(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.IsEmpty || sampleRate < 4_000)
            return;
        EnsureStarted();
        lock (_gate)
        {
            if (_useNative && _native != IntPtr.Zero)
            {
                float[] wave = sampleRate == PreferredRate ? audio.ToArray() : Resample(audio, sampleRate, PreferredRate);
                XaraNative.PushF32(_native, wave, (nuint)wave.Length);
                while (XaraNative.TryPop(_native) is { } line)
                    _pending.Add(line);
                return;
            }
        }

        Stream? stdin;
        lock (_gate)
            stdin = _stdin;
        if (stdin is null)
            return;

        float[] processWave = sampleRate == PreferredRate ? audio.ToArray() : Resample(audio, sampleRate, PreferredRate);
        int offset = 0;
        while (offset < processWave.Length)
        {
            int count = Math.Min(processWave.Length - offset, _pcmScratch.Length / 2);
            WritePcm(stdin, processWave.AsSpan(offset, count));
            offset += count;
        }
    }

    public IReadOnlyList<XaraMessage> Drain()
    {
        lock (_gate)
        {
            if (_useNative && _native != IntPtr.Zero)
            {
                while (XaraNative.TryPop(_native) is { } line)
                    _pending.Add(line);
            }

            if (_pending.Count == 0)
                return [];
            var messages = new List<XaraMessage>(_pending.Count);
            foreach (string line in _pending)
            {
                if (!TryParseLine(line, out XaraMessage message))
                    continue;
                // xara finish() always emits a stats summary; skip that noise for the transcript.
                if (message.Text.StartsWith("summary:", StringComparison.OrdinalIgnoreCase))
                    continue;
                messages.Add(message);
            }

            _pending.Clear();
            return messages;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_native != IntPtr.Zero)
            {
                try { XaraNative.Destroy(_native); } catch { /* ignore */ }
                _native = IntPtr.Zero;
            }

            try { _stdin?.Dispose(); } catch { /* ignore */ }
            _stdin = null;
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                }
                catch { /* ignore */ }

                try { _process.Dispose(); } catch { /* ignore */ }
                _process = null;
            }
        }
    }

    /// <summary>Flush the monitor after a clip so late packets can drain.</summary>
    public void Finish()
    {
        lock (_gate)
        {
            if (_useNative && _native != IntPtr.Zero)
            {
                XaraNative.Finish(_native);
                while (XaraNative.TryPop(_native) is { } line)
                    _pending.Add(line);
                return;
            }
        }

        CloseStdin();
        Thread.Sleep(200);
    }

    private void CloseStdin()
    {
        lock (_gate)
        {
            try { _stdin?.Dispose(); } catch { /* ignore */ }
            _stdin = null;
        }
    }

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;

            if (XaraNative.TryCreate(BandwidthHz, PreferredRate, out IntPtr handle))
            {
                _native = handle;
                _useNative = true;
                return;
            }

            _binary = FindBinary();
            if (_binary is null)
                return;

            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = _binary,
                    Arguments = $"--stdin {PreferredRate} --hf {BandwidthHz} --json",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                _process = new Process { StartInfo = start, EnableRaisingEvents = true };
                _process.OutputDataReceived += OnOutput;
                _process.ErrorDataReceived += OnError;
                if (!_process.Start())
                {
                    _error = "Could not start xara/zara monitor.";
                    _process = null;
                    return;
                }

                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
                _stdin = _process.StandardInput.BaseStream;
            }
            catch (Exception ex)
            {
                _error = "VarAC monitor failed to start: " + ex.Message;
                _process = null;
                _stdin = null;
            }
        }
    }

    private void OnOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;
        lock (_gate)
            _pending.Add(e.Data.Trim());
    }

    private void OnError(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;
        lock (_gate)
        {
            _error = e.Data.Trim();
            _pending.Add(e.Data.Trim());
        }
    }

    private void WritePcm(Stream stdin, ReadOnlySpan<float> samples)
    {
        int bytes = samples.Length * 2;
        Span<byte> buffer = _pcmScratch.AsSpan(0, bytes);
        for (int i = 0; i < samples.Length; i++)
        {
            float clamped = Math.Clamp(samples[i], -1f, 1f);
            short sample = (short)Math.Round(clamped * 32_767f);
            buffer[i * 2] = (byte)(sample & 0xFF);
            buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xFF);
        }

        try
        {
            stdin.Write(buffer);
            stdin.Flush();
        }
        catch (Exception ex)
        {
            lock (_gate)
                _error = "VarAC monitor stdin closed: " + ex.Message;
        }
    }

    public static bool TryParseLine(string line, out XaraMessage message)
    {
        message = new XaraMessage("");
        if (string.IsNullOrWhiteSpace(line))
            return false;
        line = line.Trim();
        if (line.StartsWith('{'))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                string text = FirstString(root, "text", "msg", "message", "payload", "data", "body") ?? "";
                string? kind = FirstString(root, "kind", "type", "event", "frame");
                string? from = FirstString(root, "from", "src", "source", "call", "callsign");
                string? to = FirstString(root, "to", "dst", "dest", "destination");
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                        text = $"{from} > {to}";
                    else if (!string.IsNullOrWhiteSpace(from))
                        text = from!;
                    else if (!string.IsNullOrWhiteSpace(kind))
                        text = kind!;
                }

                if (string.IsNullOrWhiteSpace(text))
                    return false;
                if (!string.IsNullOrWhiteSpace(from) && !text.Contains(from, StringComparison.OrdinalIgnoreCase))
                    text = $"{from}: {text}";
                message = new XaraMessage(text.Trim(), kind);
                return true;
            }
            catch (JsonException)
            {
                // Fall through to plain text.
            }
        }

        if (line.Count(ch => ch is >= ' ' and <= '~') < 3)
            return false;
        message = new XaraMessage(line);
        return true;
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (string name in names)
        {
            if (!root.TryGetProperty(name, out JsonElement value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                string? text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            else if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static string? FindBinary()
    {
        string[] names = ["xara-monitor.exe", "zara-monitor.exe", "xara-monitor", "zara-monitor"];
        foreach (string dir in SearchDirs())
        {
            foreach (string name in names)
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return path;
            }
        }

        foreach (string name in names)
        {
            string? onPath = FindOnPath(name);
            if (onPath is not null)
                return onPath;
        }

        return null;
    }

    private static IEnumerable<string> SearchDirs()
    {
        string baseDir = AppContext.BaseDirectory;
        yield return baseDir;
        yield return Path.Combine(baseDir, "native");
        string? cwd = Directory.GetCurrentDirectory();
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            yield return cwd;
            yield return Path.Combine(cwd, "native");
            yield return Path.Combine(cwd, "Host", "native");
        }

        foreach (string key in new[] { "XARA_MONITOR", "ZARA_MONITOR" })
        {
            string? env = Environment.GetEnvironmentVariable(key);
            if (string.IsNullOrWhiteSpace(env))
                continue;
            if (File.Exists(env))
                yield return Path.GetDirectoryName(env) ?? env;
            else if (Directory.Exists(env))
                yield return env;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(dir, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static float[] Resample(ReadOnlySpan<float> audio, int fromRate, int toRate)
    {
        double step = fromRate / (double)toRate;
        int count = Math.Max(1, (int)(audio.Length / step));
        var output = new float[count];
        for (int i = 0; i < count; i++)
        {
            double at = i * step;
            int lo = (int)at;
            int hi = Math.Min(audio.Length - 1, lo + 1);
            double frac = at - lo;
            output[i] = (float)(audio[lo] * (1 - frac) + audio[hi] * frac);
        }

        return output;
    }
}
