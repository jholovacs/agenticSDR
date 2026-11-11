using System.Numerics;
using System.Runtime.InteropServices;
using AgenticSdr.Dsp;
using AgenticSdr.Radio;

/// <summary>
/// Capture FM IQ to a raw float32 IQ file for offline RDS analysis.
/// Usage: RdsAnalyze [frequencyHz=98700000] [seconds=6] [outPath=capture.iq]
/// </summary>
double frequencyHz = args.Length > 0 && double.TryParse(args[0], out double f) ? f : 98_700_000;
double seconds = args.Length > 1 && double.TryParse(args[1], out double s) ? Math.Clamp(s, 1, 20) : 6;
string outPath = args.Length > 2 ? args[2] : "capture.iq";

var devices = RadioDevices.List();
if (devices.Count == 0)
{
    Console.Error.WriteLine("No radio.");
    return 1;
}

using IRadioDevice device = RadioDevices.Open(devices[0], RadioLimits.DefaultSampleRateHz, null, 0);
double lo = frequencyHz - Demodulator.WideFmOffsetHz;
device.Tune(lo);
if (device is RtlSdrDevice rtl)
{
    try { rtl.SetBandwidth(Demodulator.TunerBandwidthHz(RadioMode.Wbfm)); }
    catch (RadioException) { }
}

Console.WriteLine($"LO {lo / 1e6:0.000} for {frequencyHz / 1e6:0.000} at {device.SampleRateHz / 1e6:0.###} MS/s → {outPath}");

int stride = device.Encoding == SampleEncoding.Cu8 ? 2 : 8;
int Align(int bytes) => Math.Max(device.ReadAlignment, bytes) / device.ReadAlignment * device.ReadAlignment;
var junk = new byte[Align(32 * 1024)];
device.Read(junk);

long need = (long)(seconds * device.SampleRateHz);
var iq = new List<Complex>((int)Math.Min(need, int.MaxValue));
var chunk = new byte[Align(262_144)];
while (iq.Count < need)
{
    int n = device.Read(chunk);
    if (n <= 0)
        throw new RadioException("No samples.");
    int usable = n / stride * stride;
    if (device.Encoding == SampleEncoding.Cu8)
    {
        for (int i = 0; i + 1 < usable; i += 2)
            iq.Add(new Complex((chunk[i] - 127.5) / 127.5, (chunk[i + 1] - 127.5) / 127.5));
    }
    else
    {
        for (int i = 0; i + 7 < usable; i += 8)
        {
            float re = BitConverter.ToSingle(chunk, i);
            float im = BitConverter.ToSingle(chunk, i + 4);
            iq.Add(new Complex(re, im));
        }
    }
}

using (var writer = new BinaryWriter(File.Create(outPath)))
{
    writer.Write(device.SampleRateHz);
    writer.Write(lo);
    writer.Write(frequencyHz);
    writer.Write(iq.Count);
    foreach (Complex c in iq)
    {
        writer.Write((float)c.Real);
        writer.Write((float)c.Imaginary);
    }
}

Console.WriteLine($"Wrote {iq.Count / device.SampleRateHz:0.00} s ({iq.Count} complex samples)");

var demod = new Demodulator(RadioMode.Wbfm, Demodulator.WideFmOffsetHz, device.SampleRateHz);
const int push = 131_072;
for (int offset = 0; offset < iq.Count; offset += push)
{
    int len = Math.Min(push, iq.Count - offset);
    demod.Push(CollectionsMarshal.AsSpan(iq).Slice(offset, len));
}

DemodResult result = demod.Finish(normalize: false);
Console.WriteLine($"stereo={demod.WbfmStereo}");
Console.WriteLine(result.Text ?? "(no text)");
return 0;
