using System.Reflection;

namespace UICtl.Core;

/// <summary>
/// Every executable's `--libs-version` / `--sbom`: prints the library list or
/// CycloneDX SBOM that `make build` embedded at compile time (see
/// eng/SelfReport.targets), so a shipped binary can describe its own
/// dependencies without the .NET SDK or network. Handled before any other
/// argument parsing so neither flag ever touches the daemon.
/// </summary>
public static class SelfReport
{
    public const string LibsVersionFlag = "--libs-version";
    public const string SbomFlag = "--sbom";

    private const string LibsVersionResource = "uictl.libs-version.txt";
    private const string SbomResource = "uictl.bom.json";

    /// <summary>Returns an exit code if <paramref name="args"/> was a self-report request, else null (carry on normally).</summary>
    public static int? TryHandle(string[] args) =>
        TryHandle(args, ReadEmbedded, Console.Out, Console.Error);

    internal static int? TryHandle(string[] args, Func<string, string?> readResource, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length != 1)
            return null;

        string resource = args[0] switch
        {
            LibsVersionFlag => LibsVersionResource,
            SbomFlag => SbomResource,
            _ => "",
        };
        if (resource.Length == 0)
            return null;

        string? content = readResource(resource);
        if (content is null)
        {
            stderr.WriteLine($"{args[0]}: this binary was built without embedded dependency data - rebuild with `make build`");
            return 1;
        }

        stdout.Write(content);
        return 0;
    }

    private static string? ReadEmbedded(string resourceName)
    {
        var assembly = Assembly.GetEntryAssembly();
        using var stream = assembly?.GetManifestResourceStream(resourceName);
        if (stream is null)
            return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
