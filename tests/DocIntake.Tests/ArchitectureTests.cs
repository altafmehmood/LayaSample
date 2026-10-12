using System.Reflection;
using System.Text.Json;
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
    [InlineData("src/DocIntake.DocumentService/Classification")]
    [InlineData("src/DocIntake.DocumentService/Preparation")]
    [InlineData("src/DocIntake.DocumentService/Pdf")]
    [InlineData("src/DocIntake.DocumentService/Inspection")]
    public void The_services_below_the_endpoints_never_mention_http_status_codes(string folder)
    {
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).ToList();

        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.DoesNotContain("StatusCodes", File.ReadAllText(file)));
    }

    // The gateway never reads a document, so none of what reads one may be in its build: not a PDF, Office, image or OCR
    // library, and not the rendering project that wraps them. The dependency manifest lists everything that is published.
    // ONNX Runtime is deliberately not on the list: the Laya feedback model runs on it, in the gateway, until it moves to
    // its own service (Phase 5).
    [Fact]
    public void The_gateway_carries_no_parser_and_no_native_library()
    {
        var manifests = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src/DocIntake.Api/bin"), "DocIntake.Api.deps.json", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(manifests);

        string[] forbidden =
        [
            "PdfPig", "ClosedXML", "DocumentFormat.OpenXml", "System.Security.Cryptography.Pkcs", "Magick.NET", "PDFtoImage",
            "SkiaSharp", "RapidOcrNet", "DocIntake.Rendering", "DocIntake.DocumentService"
        ];
        foreach (var manifest in manifests)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var libraries = json.RootElement.GetProperty("libraries").EnumerateObject().Select(l => l.Name).ToList();
            Assert.Contains(libraries, l => l.StartsWith("DocIntake.Contracts/", StringComparison.Ordinal));
            foreach (var name in forbidden)
                Assert.DoesNotContain(libraries, l => l.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void The_document_service_is_where_the_parsers_are()
    {
        var manifests = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src/DocIntake.DocumentService/bin"), "DocIntake.DocumentService.deps.json", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(manifests);

        using var json = JsonDocument.Parse(File.ReadAllText(manifests[0]));
        var libraries = json.RootElement.GetProperty("libraries").EnumerateObject().Select(l => l.Name).ToList();
        foreach (var name in new[] { "PdfPig", "ClosedXML", "DocumentFormat.OpenXml", "Magick.NET", "PDFtoImage", "RapidOcrNet" })
            Assert.Contains(libraries, l => l.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }
}
