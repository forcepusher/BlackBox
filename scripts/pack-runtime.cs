// Packs the native libraries of a single-file publish for publish.ps1: a zip with its files stored
// as they are, the whole of it compressed with Brotli at its best. PortableRuntime unpacks it into
// Data/runtime on first run. Brotli makes them 7.6 MB where the zip's own compression made 10.
//
//   dotnet run scripts/pack-runtime.cs -- <folder of libraries> <payload file>
using System.IO.Compression;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: dotnet run scripts/pack-runtime.cs -- <folder> <payload>");
    return 1;
}
var (folder, payload) = (args[0], args[1]);
using var zipped = new MemoryStream();
using (var zip = new ZipArchive(zipped, ZipArchiveMode.Create, leaveOpen: true))
    foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
        zip.CreateEntryFromFile(file, Path.GetFileName(file), CompressionLevel.NoCompression);
var bytes = zipped.ToArray();
var packed = new byte[BrotliEncoder.GetMaxCompressedLength(bytes.Length)];
// Quality 11 and a 16 MB window: the libraries are read once, at the first start.
if (!BrotliEncoder.TryCompress(bytes, packed, out var written, quality: 11, window: 24))
{
    Console.Error.WriteLine("Brotli failed.");
    return 1;
}
File.WriteAllBytes(payload, packed.AsSpan(0, written).ToArray());
Console.WriteLine($"Native libraries: {bytes.Length / 1048576.0:0.0} MB packed into {written / 1048576.0:0.0} MB");
return 0;
