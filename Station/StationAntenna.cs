using System.Net;
using System.Numerics;
using System.Text.Json;
using AgenticSdr.Dsp;
using AgenticSdr.Host;
using AgenticSdr.Radio;

namespace AgenticSdr.Station;

/// <summary>
/// The same antenna the receiver uses: length, vertical or horizontal, and a compass bearing for a wire.
/// Guess listens to the noise floor. It never transmits. When the receiver already has the radio open,
/// the guess is done there so two programs do not fight over one dongle.
/// </summary>
public sealed class StationAntenna
{
    readonly object gate = new();
    readonly SemaphoreSlim probe = new(1, 1);
    readonly AntennaStore store;
    readonly HttpClient? receiver;
    readonly string? receiverBase;
    readonly JsonSerializerOptions json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    AntennaSpec? current;

    public StationAntenna(string antennaPath, string? receiverBase = "http://127.0.0.1:5088")
    {
        store = new AntennaStore(antennaPath);
        current = store.Load();
        this.receiverBase = string.IsNullOrWhiteSpace(receiverBase) ? null : receiverBase.TrimEnd('/');
        if (this.receiverBase != null)
        {
            var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) };
            receiver = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(90) };
        }
    }

    public AntennaAdvice Current()
    {
        lock (gate)
            return current is null ? AntennaPlan.None() : AntennaPlan.Advise(current);
    }

    public AntennaAdvice Set(double length, string? unit, string? orientation, double? azimuthDegrees)
    {
        AntennaSpec spec = AntennaPlan.Parse(length, unit, orientation, azimuthDegrees);
        lock (gate)
        {
            current = spec;
            store.Save(spec);
            return AntennaPlan.Advise(spec);
        }
    }

    public async Task<AntennaAdvice> GuessAsync(CancellationToken cancellationToken)
    {
        if (!await probe.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("An antenna guess is already running.");

        try
        {
            AntennaAdvice? remote = await TryReceiverAsync(cancellationToken).ConfigureAwait(false);
            if (remote is not null)
                return Keep(remote);

            return await GuessLocalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            probe.Release();
        }
    }

    AntennaAdvice Keep(AntennaAdvice remote)
    {
        lock (gate)
        {
            AntennaSpec? loaded = store.Load();
            if (loaded is not null
                && loaded.Orientation == remote.Orientation
                && Math.Abs(loaded.LengthMeters - remote.LengthMeters) < 0.02)
            {
                current = loaded;
                AntennaAdvice advice = AntennaPlan.Advise(loaded);
                return string.IsNullOrWhiteSpace(remote.Summary) ? advice : advice with { Summary = remote.Summary };
            }
        }

        AntennaSpec spec = Adopt(remote);
        Remember(spec);
        AntennaAdvice saved = AntennaPlan.Advise(spec);
        return string.IsNullOrWhiteSpace(remote.Summary) ? saved : saved with { Summary = remote.Summary };
    }

    static AntennaSpec Adopt(AntennaAdvice remote)
    {
        try
        {
            return AntennaPlan.Parse(remote.LengthMeters, "m", remote.Orientation, remote.AzimuthDegrees);
        }
        catch (ArgumentException) when (remote.Orientation == "horizontal" && remote.AzimuthDegrees is null)
        {
            double rounded = Math.Round(remote.LengthMeters, 2);
            return new AntennaSpec(rounded, "horizontal", null, $"{rounded:0.00} m");
        }
    }

    async Task<AntennaAdvice?> TryReceiverAsync(CancellationToken cancellationToken)
    {
        if (receiver is null || receiverBase is null)
            return null;

        try
        {
            using var response = await receiver.PostAsync(
                receiverBase + "/api/antenna/guess",
                new StringContent(""),
                cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                AntennaAdvice? advice = JsonSerializer.Deserialize<AntennaAdvice>(body, json);
                if (advice is null || !advice.Configured)
                    throw new InvalidOperationException("The receiver guessed an antenna but the result was empty.");
                return advice;
            }

            string error = ReadError(body) ?? "The receiver could not guess an antenna.";
            if (response.StatusCode == HttpStatusCode.BadRequest && error == "Open the radio first.")
                return null;
            if ((int)response.StatusCode == 499)
                throw new OperationCanceledException();
            throw new InvalidOperationException(error);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    async Task<AntennaAdvice> GuessLocalAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RadioDeviceInfo> devices = RadioDevices.List();
        if (devices.Count == 0)
            throw new InvalidOperationException("No radio is connected. Open a radio on the receiver at port 5088 and guess again, or plug in a radio this desk can listen with.");

        RadioException? openError = null;
        foreach (RadioDeviceInfo info in devices)
        {
            IRadioDevice? device = null;
            try
            {
                device = RadioDevices.Open(info, 2_400_000, 28, 0);
            }
            catch (RadioException ex)
            {
                openError = ex;
                continue;
            }

            try
            {
                return await ProbeAsync(device, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                device.Dispose();
            }
        }

        throw new InvalidOperationException(
            "Could not open a radio to listen for the antenna. If the receiver on port 5088 has the dongle, open the radio there and guess again. "
            + (openError?.Message ?? "No radio accepted the open request."));
    }

    async Task<AntennaAdvice> ProbeAsync(IRadioDevice device, CancellationToken cancellationToken)
    {
        List<double> probes = AntennaGuess.ProbesInRange(device.Capabilities.MinFrequencyHz, device.Capabilities.MaxFrequencyHz).ToList();
        if (probes.Count < 3)
            throw new InvalidOperationException("This radio cannot tune enough of the dial to guess an antenna.");

        double? savedGain = device.Capabilities.GainDb;
        double? probeGain = device.DirectSampling ? 8 : (savedGain is > 30 ? 30 : savedGain);
        if (probeGain != savedGain)
            device.SetGain(probeGain);

        var readings = new List<SignalStrength>(probes.Count);
        try
        {
            for (int i = 0; i < probes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Complex[] samples = await CaptureAsync(device, probes[i], cancellationToken).ConfigureAwait(false);
                double[] db = SpectrumAnalyzer.AveragePowerDb(samples, 4096);
                readings.Add(new SignalStrength(probes[i], Percentile(db, 0.15)));
            }
        }
        finally
        {
            if (probeGain != savedGain)
                device.SetGain(savedGain);
        }

        if (!AntennaGuess.TryFromFloors(readings, out AntennaSpec spec, out string note))
            throw new InvalidOperationException(note);

        Remember(spec);
        AntennaAdvice advice = AntennaPlan.Advise(spec);
        return advice with { Summary = note + " " + advice.Summary };
    }

    void Remember(AntennaSpec spec)
    {
        lock (gate)
        {
            current = spec;
            store.Save(spec);
        }
    }

    static async Task<Complex[]> CaptureAsync(IRadioDevice device, double frequencyHz, CancellationToken cancellationToken)
    {
        int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
        int unit = Math.Max(1, device.ReadAlignment / stride);
        int samples = Math.Max(unit, (int)(device.SampleRateHz * 80 / 1000.0));
        samples = Math.Max(unit, samples / unit * unit);
        var buffer = new byte[samples * stride];
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            device.Tune(frequencyHz);
            var junk = new byte[Aligned(device, 32 * 1024)];
            device.Read(junk);
            int got = 0;
            var chunk = new byte[Aligned(device, 262_144)];
            while (got + device.ReadAlignment <= buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int ask = Math.Min(chunk.Length, (buffer.Length - got) / device.ReadAlignment * device.ReadAlignment);
                byte[] block = ask == chunk.Length ? chunk : new byte[ask];
                int n = device.Read(block);
                if (n <= 0)
                    throw new RadioException("The radio returned no samples.");
                int copy = Math.Min(n, buffer.Length - got);
                Buffer.BlockCopy(block, 0, buffer, got, copy);
                got += copy;
            }
        }, cancellationToken).ConfigureAwait(false);

        return ToIq(device, buffer);
    }

    static Complex[] ToIq(IRadioDevice device, byte[] bytes)
    {
        if (device.Encoding == SampleEncoding.Cu8)
            return Iq.Convert(bytes, device.DirectSampling).Samples;

        int count = bytes.Length / 8;
        var iq = new Complex[count];
        for (int i = 0; i < count; i++)
        {
            float real = BitConverter.ToSingle(bytes, i * 8);
            float imag = BitConverter.ToSingle(bytes, i * 8 + 4);
            iq[i] = new Complex(real, imag);
        }

        return iq;
    }

    static int Aligned(IRadioDevice device, int bytes)
    {
        int align = device.ReadAlignment;
        if (bytes < align)
            bytes = align;
        return bytes / align * align;
    }

    static double Percentile(double[] values, double fraction)
    {
        var sorted = (double[])values.Clone();
        Array.Sort(sorted);
        int index = (int)Math.Clamp(Math.Round(fraction * (sorted.Length - 1)), 0, sorted.Length - 1);
        return sorted[index];
    }

    static string? ReadError(string body)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
                return error.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
