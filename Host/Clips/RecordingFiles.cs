namespace AgenticSdr.Host;

/// <summary>
/// One in-progress MP3 in the OS temp directory. The file is removed when the recording
/// stops, whether or not the listener downloads it, and again when the host starts or stops.
/// </summary>
public sealed class RecordingFiles : IDisposable
{
    private readonly string _directory;

    public RecordingFiles()
        : this(Path.Combine(Path.GetTempPath(), "AgenticSDR"))
    {
    }

    public RecordingFiles(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        Clear();
    }

    public string Create()
    {
        string id = Guid.NewGuid().ToString("N");
        using var stream = new FileStream(PathFor(id), FileMode.CreateNew, FileAccess.Write, FileShare.Read | FileShare.Delete);
        return id;
    }

    public void Append(string id, ReadOnlySpan<byte> bytes)
    {
        string path = PathFor(id);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read | FileShare.Delete);
        stream.Seek(0, SeekOrigin.End);
        stream.Write(bytes);
    }

    public bool TryOpen(string id, out Stream? stream)
    {
        string path = PathFor(id);
        if (!File.Exists(path))
        {
            stream = null;
            return false;
        }

        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return true;
    }

    public void Delete(string id)
    {
        try
        {
            File.Delete(PathFor(id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
    }

    public void Clear()
    {
        if (!Directory.Exists(_directory))
            return;
        foreach (string path in Directory.EnumerateFiles(_directory, "*.mp3"))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose() => Clear();

    private string PathFor(string id)
    {
        if (id.Length != 32 || !id.All(Uri.IsHexDigit))
            throw new ArgumentException("Unknown recording.");
        return Path.Combine(_directory, id + ".mp3");
    }
}
