using UICtl.Core;

namespace UICtl.Core.Tests;

public class SelfReportTests
{
    private static (int? Exit, string Out, string Err) Run(string[] args, Func<string, string?> resources)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int? exit = SelfReport.TryHandle(args, resources, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("--libs-version", "uictl.libs-version.txt")]
    [InlineData("--sbom", "uictl.bom.json")]
    public void Flag_PrintsTheEmbeddedResource(string flag, string resource)
    {
        var (exit, output, err) = Run([flag], name => name == resource ? "embedded content" : null);

        Assert.Equal(0, exit);
        Assert.Equal("embedded content", output);
        Assert.Equal("", err);
    }

    [Theory]
    [InlineData("--libs-version")]
    [InlineData("--sbom")]
    public void Flag_WithoutEmbeddedData_FailsWithRebuildHint(string flag)
    {
        var (exit, output, err) = Run([flag], _ => null);

        Assert.Equal(1, exit);
        Assert.Equal("", output);
        Assert.Contains("make build", err);
    }

    [Theory]
    [InlineData]
    [InlineData("windows")]
    [InlineData("--sbom", "extra")]
    [InlineData("--help")]
    public void OtherArguments_AreNotHandled(params string[] args)
    {
        var (exit, output, _) = Run(args, _ => "should not be read");

        Assert.Null(exit);
        Assert.Equal("", output);
    }
}
