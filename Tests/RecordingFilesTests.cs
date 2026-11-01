using AgenticSdr.Host;

namespace AgenticSdr.Tests;

public class RecordingFilesTests
{
    [Fact]
    public void A_recording_is_removed_when_it_ends()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agentic-sdr-rec-" + Guid.NewGuid().ToString("N"));
        try
        {
            var files = new RecordingFiles(dir);
            string id = files.Create();
            string path = Path.Combine(dir, id + ".mp3");
            files.Append(id, "abc"u8);
            Assert.True(File.Exists(path));

            files.Delete(id);
            Assert.False(File.Exists(path));
            files.Delete(id);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Starting_the_store_removes_a_file_left_behind()
    {
        string dir = Path.Combine(Path.GetTempPath(), "agentic-sdr-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, new string('a', 32) + ".mp3"), [1, 2, 3]);
            var files = new RecordingFiles(dir);
            Assert.Empty(Directory.GetFiles(dir, "*.mp3"));
            files.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
