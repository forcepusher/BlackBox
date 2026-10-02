using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

namespace BlackBox.App;

/// <summary>
/// Makes the single-file build work without a folder of DLLs next to it.
/// </summary>
/// <remarks>
/// .NET can bundle managed code into one exe, but native libraries (Skia, HarfBuzz, ANGLE) and
/// llama.cpp have to exist as real files. The single-file publish embeds them as a zip; on first
/// run (or after an update) they are unpacked into <c>Data/runtime</c> next to the exe, never
/// into %TEMP%, so the app stays portable and leaves nothing elsewhere on the system.
/// </remarks>
internal static class PortableRuntime
{
    private const string PayloadName = "BlackBox.runtime.zip";

    /// <summary>
    /// The portable root is the exe folder. BLACKBOX_HOME overrides it, and debug builds use the
    /// repository root so Models/, llama/ and Data/ are shared between builds.
    /// </summary>
    public static string FindAppRoot()
    {
        if (Environment.GetEnvironmentVariable("BLACKBOX_HOME") is { Length: > 0 } home)
            return home;
#if DEBUG
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.EnumerateFiles("BlackBox.sln*").Any())
                return dir.FullName;
        }
#endif
        return AppContext.BaseDirectory;
    }

    public static string RuntimeDir(string root) => Path.Combine(root, "Data", "runtime");

    /// <summary>Unpacks the embedded payload if this build has one, then points native loading at it.</summary>
    public static void Prepare(string root)
    {
        var dir = RuntimeDir(root);
        using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadName))
        {
            if (payload is null)
                return; // normal folder build: natives already sit next to the exe

            var stamp = Path.Combine(dir, ".version");
            var version = $"{Assembly.GetExecutingAssembly().ManifestModule.ModuleVersionId}:{payload.Length}";
            if (!File.Exists(stamp) || File.ReadAllText(stamp) != version)
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(dir);
                using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                    zip.ExtractToDirectory(dir, overwriteFiles: true);
                File.WriteAllText(stamp, version);
            }
        }

        if (OperatingSystem.IsWindows())
            SetDllDirectory(dir);
        // Belt and braces for the two libraries everything depends on: load them from our folder
        // even if another copy is on PATH (Git for Windows ships an incompatible libSkiaSharp).
        Resolve(typeof(SkiaSharp.SKColor).Assembly, dir, "libSkiaSharp");
        Resolve(typeof(HarfBuzzSharp.Blob).Assembly, dir, "libHarfBuzzSharp");
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
