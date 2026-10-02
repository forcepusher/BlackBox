using System.Text;
using System.Text.Json;
using BlackBox.Core.Settings;
using Xunit;

namespace BlackBox.Core.Tests;

public class EmbeddedPayloadTests
{
    [Fact]
    public async Task Appended_files_are_found_and_extracted()
    {
        var dir = Directory.CreateTempSubdirectory("bb-payload-").FullName;
        var exe = Path.Combine(dir, "fake.exe");
        var head = Encoding.ASCII.GetBytes("MZ pretend this is a program");
        var model = Enumerable.Range(0, 5_000_000).Select(i => (byte)i).ToArray();
        var proj = Encoding.ASCII.GetBytes("projector");

        await using (var fs = File.Create(exe))
        {
            fs.Write(head);
            var entries = new List<EmbeddedPayload.Entry>();
            foreach (var (name, data) in new[] { ("m.gguf", model), ("p.gguf", proj) })
            {
                entries.Add(new EmbeddedPayload.Entry(name, fs.Position, data.Length));
                fs.Write(data);
            }
            var index = JsonSerializer.SerializeToUtf8Bytes(entries);
            fs.Write(index);
            fs.Write(BitConverter.GetBytes((long)index.Length));
            fs.Write(Encoding.ASCII.GetBytes(EmbeddedPayload.Magic));
        }

        var payload = EmbeddedPayload.TryOpen(exe);
        Assert.NotNull(payload);
        var entry = payload!.Find("M.GGUF")!;
        var dest = Path.Combine(dir, "Models", "m.gguf");
        Assert.False(EmbeddedPayload.IsExtracted(entry, dest));

        await payload.ExtractAsync(entry, dest, null, CancellationToken.None);

        Assert.Equal(model, File.ReadAllBytes(dest));
        Assert.True(EmbeddedPayload.IsExtracted(entry, dest));
    }

    [Fact]
    public void Plain_files_have_no_payload()
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, "just a file");
        Assert.Null(EmbeddedPayload.TryOpen(file));
    }
}
