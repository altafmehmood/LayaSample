using System.Reflection;
using DocIntake.Contracts;

namespace DocIntake.Tests;

/// <summary>Dependency rules that keep the layers apart as the code moves into separate services.</summary>
public class ArchitectureTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DocIntake.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("DocIntake.slnx not found above the test folder");
    }

    [Fact]
    public void Contracts_depends_on_nothing_but_the_runtime()
    {
        var references = typeof(FailureKind).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.All(references, name => Assert.True(
            name is "System.Runtime" or "System.ComponentModel" or "System.Collections" or "System.Linq" or "System.Text.Json"
                or "System.Private.CoreLib" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Contracts references {name}"));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("DocIntake.", StringComparison.Ordinal));
    }

    // Constants are inlined by the compiler, so a compiled assembly cannot show who uses StatusCodes: read the source.
    [Theory]
    [InlineData("src/DocIntake.Api/Services/Documents")]
    [InlineData("src/DocIntake.Api/Services/Security")]
    public void The_services_below_the_endpoints_never_mention_http_status_codes(string folder)
    {
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();

        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.DoesNotContain("StatusCodes", File.ReadAllText(file)));
    }
}
