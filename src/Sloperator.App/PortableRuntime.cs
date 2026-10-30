using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Sloperator.Core.Settings;

namespace Sloperator.App;

/// <summary>
/// Makes the single-file build work without a folder of DLLs next to it.
/// </summary>
/// <remarks>
/// .NET can bundle managed code into one exe, but native libraries (Skia, HarfBuzz, ANGLE) have
/// to exist as real files. The single-file publish embeds them as a zip packed with Brotli
/// (scripts/pack-runtime.cs: 2.4 MB less than the zip's own compression); on first
/// run (or after an update) they are unpacked into <c>Data/runtime</c> next to the exe, never
/// into %TEMP%, so the app stays portable and leaves nothing elsewhere on the system.
/// </remarks>
internal static class PortableRuntime
{
    private const string PayloadName = "Sloperator.runtime.br";

#if DEBUG
    private const bool DebugBuild = true;
#else
    private const bool DebugBuild = false;
#endif

    /// <summary>The single-file exe publish.ps1 makes, which carries its native libraries: not a debug or folder build.</summary>
    public static bool SingleFile { get; } = Assembly.GetExecutingAssembly().GetManifestResourceInfo(PayloadName) is not null;

    /// <summary>The portable folder this build keeps its data in (debug builds: <c>.dev/</c> in the repository).</summary>
    public static AppPaths FindPaths() => new(AppPaths.FindRoot(useRepositoryDevFolder: DebugBuild));

    /// <summary>Unpacks the embedded payload if this build has one, then points native loading at it.</summary>
    public static void Prepare(AppPaths paths)
    {
        var dir = paths.RuntimeDir;
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName))
        {
            if (payload is null)
                return; // normal folder build: natives already sit next to the exe

            var moduleId = Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId;
            var version = $"{moduleId}:{payload.Length}";
            if (!Unpacked(dir, version))
            {
                try
                {
                    Unpack(payload, dir, version);
                }
                // Another version of Sloperator runs from this folder (a newer download, "Sloperator (1).exe",
                // started while the old one was open) and holds the libraries: "Sloperator could not
                // start. UnauthorizedAccessException: Access to the path 'libSkiaSharp.dll' is denied."
                // This one unpacks beside them; the next start alone goes back to Data/runtime.
                // Whatever is left of the folder counts: started by a restart into an update, the old
                // version, still ending, had let go of libSkiaSharp.dll but not of libHarfBuzzSharp.dll,
                // and the deleting got rid of the one it looked for before failing on the other.
                // A Data folder that takes nothing (Program Files) fails beside them too, and says so.
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && Directory.Exists(dir))
                {
                    dir = Path.Combine(paths.DataDir, $"runtime-{moduleId.ToString("N")[..8]}");
                    if (!Unpacked(dir, version))
                    {
                        payload.Position = 0;
                        Unpack(payload, dir, version);
                    }
                }
            }
            // Left by a start beside another version, once nothing holds them.
            if (dir == paths.RuntimeDir)
                foreach (var old in Directory.EnumerateDirectories(paths.DataDir, "runtime-*"))
                    try { Directory.Delete(old, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        if (OperatingSystem.IsWindows())
            SetDllDirectory(dir);
        // Belt and braces for the two libraries everything depends on: load them from our folder
        // even if another copy is on PATH (Git for Windows ships an incompatible libSkiaSharp).
        Resolve(typeof(SkiaSharp.SKColor).Assembly, dir, "libSkiaSharp");
        Resolve(typeof(HarfBuzzSharp.Blob).Assembly, dir, "libHarfBuzzSharp");
    }

    private static bool Unpacked(string dir, string version)
    {
        var stamp = Path.Combine(dir, ".version");
        return File.Exists(stamp) && File.ReadAllText(stamp) == version;
    }

    private static void Unpack(Stream payload, string dir, string version)
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        // The zip, which can't be read from a stream that only goes forward, is unpacked in memory.
        using (var zip = new ZipArchive(new BrotliStream(payload, CompressionMode.Decompress, leaveOpen: true), ZipArchiveMode.Read))
            zip.ExtractToDirectory(dir, overwriteFiles: true);
        File.WriteAllText(Path.Combine(dir, ".version"), version);
    }

    private static void Resolve(Assembly assembly, string dir, string library)
    {
        NativeLibrary.SetDllImportResolver(assembly, (name, _, _) =>
        {
            if (!name.StartsWith(library, StringComparison.OrdinalIgnoreCase))
                return IntPtr.Zero;
            var file = Path.Combine(dir, library + (OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so"));
            return File.Exists(file) && NativeLibrary.TryLoad(file, out var handle) ? handle : IntPtr.Zero;
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);
}
