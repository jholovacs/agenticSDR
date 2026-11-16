using System.IO.Compression;
using System.Reflection;

namespace AgenticSdr.Dsp;

/// <summary>
/// JS8Call JSC word list (262144 entries), used to turn free-text dictionary indexes into words.
/// Extracted from JS8Call jsc_map.cpp (Jordan Sherer / GPL-3.0).
/// </summary>
public static class Js8JscDictionary
{
    public const int Size = 262_144;
    private static readonly object Gate = new();
    private static string[]? _words;

    /// <summary>Looks up a JSC index. Returns null when the dictionary is missing or the index is out of range.</summary>
    public static string? Word(int index)
    {
        string[] words = EnsureLoaded();
        if ((uint)index >= (uint)words.Length)
            return null;
        string word = words[index];
        return word.Length == 0 ? null : word;
    }

    private static string[] EnsureLoaded()
    {
        if (_words is not null)
            return _words;

        lock (Gate)
        {
            if (_words is not null)
                return _words;

            var loaded = new string[Size];
            Assembly assembly = typeof(Js8JscDictionary).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream("AgenticSdr.Dsp.jsc_words.txt.gz")
                ?? OpenBesideAssembly();
            if (stream is null)
            {
                _words = loaded;
                return loaded;
            }

            using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            for (int i = 0; i < Size; i++)
            {
                string? line = reader.ReadLine();
                if (line is null)
                    break;
                loaded[i] = Unescape(line);
            }

            _words = loaded;
            return loaded;
        }
    }

    private static Stream? OpenBesideAssembly()
    {
        string? dir = Path.GetDirectoryName(typeof(Js8JscDictionary).Assembly.Location);
        if (dir is null)
            return null;
        string path = Path.Combine(dir, "jsc_words.txt.gz");
        return File.Exists(path) ? File.OpenRead(path) : null;
    }

    private static string Unescape(string line)
    {
        var chars = new List<char>(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
            {
                chars.Add(line[i + 1] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    '\\' => '\\',
                    char other => other
                });
                i++;
                continue;
            }

            chars.Add(line[i]);
        }

        return new string(chars.ToArray());
    }
}
