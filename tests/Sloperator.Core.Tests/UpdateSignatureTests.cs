using Sloperator.Core.Web;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>A downloaded update kept or not by its Authenticode signature against the running exe's.</summary>
public class UpdateSignatureTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bb-signature-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private const string Running = "Sloperator.exe", Fresh = "Sloperator.exe.new";

    private const string Ours = "CN=SignPath Foundation, O=SignPath Foundation, L=Lewes, S=Delaware, C=US";

    private static Verdict Verify(Signature running, Signature fresh) =>
        SelfUpdate.Verify(Running, Fresh, path => path == Running ? running : fresh).Verdict;

    [Fact]
    public void An_unsigned_running_exe_takes_any_download()
    {
        Assert.Equal(Verdict.Keep, Verify(Signature.None, Signature.None));
        Assert.Equal(Verdict.Keep, Verify(Signature.None, new Signature(Signed.Yes, "CN=Someone Else")));
        Assert.Equal(Verdict.Keep, Verify(Signature.None, new Signature(Signed.Unknown)));
    }

    [Fact]
    public void An_unsigned_running_exe_does_not_even_check_the_download()
    {
        var asked = new List<string>();
        SelfUpdate.Verify(Running, Fresh, path => { asked.Add(path); return Signature.None; });
        Assert.Equal([Running], asked);
    }

    [Fact]
    public void A_signed_running_exe_rejects_an_unsigned_download() =>
        Assert.Equal(Verdict.Reject, Verify(new Signature(Signed.Yes, Ours), new Signature(Signed.No, Code: unchecked((int)0x800B0100))));

    [Fact]
    public void A_signed_running_exe_rejects_another_publishers_download() =>
        Assert.Equal(Verdict.Reject, Verify(new Signature(Signed.Yes, Ours), new Signature(Signed.Yes, "CN=Someone Else, O=Someone Else, C=US")));

    [Fact]
    public void The_publisher_is_compared_exactly() =>
        Assert.Equal(Verdict.Reject, Verify(new Signature(Signed.Yes, Ours), new Signature(Signed.Yes, Ours.ToLowerInvariant())));

    [Fact]
    public void A_signed_running_exe_keeps_its_own_publishers_download() =>
        // Another certificate (renewed), the same subject.
        Assert.Equal(Verdict.Keep, Verify(new Signature(Signed.Yes, Ours), new Signature(Signed.Yes, Ours)));

    [Fact]
    public void A_signature_that_could_not_be_checked_is_tried_again()
    {
        Assert.Equal(Verdict.TryAgain, Verify(new Signature(Signed.Yes, Ours), new Signature(Signed.Unknown, Code: unchecked((int)0x800B010E))));
        Assert.Equal(Verdict.TryAgain, Verify(new Signature(Signed.Unknown, Code: unchecked((int)0x80092013)), new Signature(Signed.Yes, Ours)));
    }

    [Fact]
    public void A_rejected_version_is_written_down_and_read_back()
    {
        var file = Path.Combine(_root, "update-rejected.txt");
        Assert.Null(SelfUpdate.Rejected(file));
        SelfUpdate.Reject(file, new Version(1, 2, 0));
        Assert.Equal(new Version(1, 2, 0), SelfUpdate.Rejected(file));
        Assert.NotEqual(new Version(1, 3, 0), SelfUpdate.Rejected(file));
    }

#if WINDOWS
    /// <summary>
    /// An exe that carries its own (embedded) Microsoft signature: the portable SDK's dotnet.exe
    /// (.dev\dotnet), the one DOTNET_ROOT names, or an installed one.
    /// </summary>
    private static string? SignedExe()
    {
        var candidates = new List<string>();
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, ".dev", "dotnet", "dotnet.exe"));
        if (Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root)
            candidates.Add(Path.Combine(root, "dotnet.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string RequireSignedExe() => SignedExe() ?? throw new InvalidOperationException("No dotnet.exe to check a real signature on.");

    [Fact]
    public void Windows_reads_a_valid_signature_and_its_publisher()
    {
        var signature = Sloperator.Platform.Windows.WindowsSignature.Of(RequireSignedExe());
        Assert.Equal(Signed.Yes, signature.Signed);
        Assert.Contains("O=Microsoft Corporation", signature.Publisher);
    }

    [Fact]
    public void Windows_finds_no_signature_on_an_unsigned_exe()
    {
        // This test assembly, just built: a PE file nobody signed.
        var signature = Sloperator.Platform.Windows.WindowsSignature.Of(typeof(UpdateSignatureTests).Assembly.Location);
        Assert.Equal(Signed.No, signature.Signed);
        Assert.Null(signature.Publisher);
    }

    [Fact]
    public void Windows_finds_no_valid_signature_on_a_file_altered_after_signing()
    {
        var copy = Path.Combine(_root, "altered.exe");
        File.Copy(RequireSignedExe(), copy);
        using (var file = File.Open(copy, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = file.Length / 3;
            var b = file.ReadByte();
            file.Position--;
            file.WriteByte((byte)(b ^ 0xFF));
        }
        Assert.Equal(Signed.No, Sloperator.Platform.Windows.WindowsSignature.Of(copy).Signed);
    }

    [Fact]
    public void Windows_finds_no_signature_on_a_file_that_is_no_program()
    {
        var text = Path.Combine(_root, "notes.exe");
        File.WriteAllText(text, "not a program");
        Assert.Equal(Signed.No, Sloperator.Platform.Windows.WindowsSignature.Of(text).Signed);
    }

    [Fact]
    public void A_signed_running_exe_keeps_a_copy_of_itself_and_rejects_an_unsigned_exe()
    {
        var running = RequireSignedExe();
        var same = Path.Combine(_root, "same.exe");
        File.Copy(running, same);
        Assert.Equal(Verdict.Keep, SelfUpdate.Verify(running, same, Sloperator.Platform.Windows.WindowsSignature.Of).Verdict);
        Assert.Equal(Verdict.Reject, SelfUpdate.Verify(running, typeof(UpdateSignatureTests).Assembly.Location, Sloperator.Platform.Windows.WindowsSignature.Of).Verdict);
    }

    [Fact]
    public void An_unsigned_running_exe_keeps_whatever_it_downloaded() =>
        Assert.Equal(Verdict.Keep, SelfUpdate.Verify(typeof(UpdateSignatureTests).Assembly.Location, Path.Combine(_root, "missing.exe"),
            Sloperator.Platform.Windows.WindowsSignature.Of).Verdict);
#endif
}
