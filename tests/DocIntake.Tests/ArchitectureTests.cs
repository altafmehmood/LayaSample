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
    [InlineData("src/DocIntake.ExtractionService")]
    public void The_services_below_the_endpoints_never_mention_http_status_codes(string folder)
    {
        var files = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), folder), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                // A service's entry point is its HTTP edge, where status codes belong.
                && Path.GetFileName(f) != "Program.cs").ToList();

        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.DoesNotContain("StatusCodes", File.ReadAllText(file)));
    }

    // The gateway never reads a document and never runs a model, so none of what does either may be in its build: not a PDF,
    // Office, image or OCR library, not ONNX Runtime or the library that runs Laya, and not the projects that wrap them.
    // The dependency manifest lists everything that is published.
    [Fact]
    public void The_gateway_carries_no_parser_and_no_native_library()
    {
        var manifests = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src/DocIntake.Api/bin"), "DocIntake.Api.deps.json", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(manifests);

        string[] forbidden =
        [
            "PdfPig", "ClosedXML", "DocumentFormat.OpenXml", "System.Security.Cryptography.Pkcs", "Magick.NET", "PDFtoImage",
            "SkiaSharp", "RapidOcrNet", "Microsoft.ML.OnnxRuntime", "ElBruno", "DocIntake.Rendering", "DocIntake.DocumentService"
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

    private static List<string> Libraries(string project)
    {
        var manifests = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), $"src/{project}/bin"), $"{project}.deps.json", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(manifests);
        using var json = JsonDocument.Parse(File.ReadAllText(manifests[0]));
        return json.RootElement.GetProperty("libraries").EnumerateObject().Select(l => l.Name).ToList();
    }

    // The gateway has no model provider to call, so it carries neither the library for one nor the extraction service.
    [Fact]
    public void The_gateway_carries_no_model_provider_library()
    {
        var libraries = Libraries("DocIntake.Api");

        foreach (var name in new[] { "OpenAI", "Microsoft.Extensions.AI.OpenAI", "DocIntake.ExtractionService" })
            Assert.DoesNotContain(libraries, l => l.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_extraction_service_has_the_model_libraries_and_no_document_parser()
    {
        var libraries = Libraries("DocIntake.ExtractionService");

        foreach (var name in new[] { "OpenAI", "Microsoft.Extensions.AI" })
            Assert.Contains(libraries, l => l.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase));
        foreach (var name in new[] { "PdfPig", "ClosedXML", "DocumentFormat.OpenXml", "Magick.NET", "PDFtoImage", "SkiaSharp", "RapidOcrNet",
                     "Microsoft.ML.OnnxRuntime", "Microsoft.Data.Sqlite", "DocIntake.Api", "DocIntake.Rendering", "DocIntake.DocumentService" })
            Assert.DoesNotContain(libraries, l => l.StartsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    // The key is only ever handed to the extraction service: not read by the gateway's code, and not in its Deployment.
    [Fact]
    public void The_gateway_never_reads_or_is_given_the_model_provider_key()
    {
        var root = RepositoryRoot();
        var gatewaySource = Directory.EnumerateFiles(Path.Combine(root, "src/DocIntake.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
        Assert.All(gatewaySource, file => Assert.DoesNotContain("OPENAI_API_KEY", File.ReadAllText(file)));

        var deployment = File.ReadAllText(Path.Combine(root, "deploy/helm/docintake/templates/api-deployment.yaml"));
        Assert.DoesNotContain("Extraction__ApiKey", deployment);
        Assert.DoesNotContain("OPENAI_API_KEY", deployment);
        Assert.DoesNotContain("api.extraction", deployment);

        var compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));
        var gatewayService = compose[compose.IndexOf("\n  api:", StringComparison.Ordinal)..compose.IndexOf("\n  clamav:", StringComparison.Ordinal)];
        Assert.DoesNotContain("OPENAI_API_KEY", gatewayService);
        Assert.DoesNotContain("Extraction__ApiKey", gatewayService);
    }

    private static IEnumerable<string> GatewaySource() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src/DocIntake.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    // The scan service is the only thing that speaks clamd's plaintext socket (and only on the pod's loopback); the gateway reaches
    // ClamAV over HTTP through it, so it carries no raw TCP client and no clamd protocol.
    [Fact]
    public void The_gateway_never_speaks_to_clamd_itself()
    {
        Assert.All(GatewaySource(), file =>
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("TcpClient", text);
            Assert.DoesNotContain("INSTREAM", text);
            Assert.DoesNotContain("System.Net.Sockets", text);
        });
    }

    // Program.cs only wires the gateway together: its endpoints, filters, rate limits and OpenAPI setup are in their own files.
    [Fact]
    public void The_gateways_entry_point_only_wires()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src/DocIntake.Api/Program.cs"));

        Assert.DoesNotContain(".MapPost(", program);
        Assert.DoesNotContain(".MapGet(", program);
        Assert.DoesNotContain("AddConcurrencyLimiter", program);
        Assert.DoesNotContain("AddDocumentTransformer", program);
        Assert.True(program.Split('\n').Length < 100, "Program.cs has grown: move what it does into the endpoint, setup or filter files");
    }

    // Endpoints and filters throw, and ProblemMapping (the exception handler) answers: nothing under Endpoints catches a failure.
    [Fact]
    public void The_endpoints_never_catch_failures_themselves()
    {
        var endpoints = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src/DocIntake.Api/Endpoints"), "*.cs").ToList();

        Assert.NotEmpty(endpoints);
        // A catch clause, not the word: "catch (...)" or "catch {".
        Assert.All(endpoints, file => Assert.DoesNotMatch(@"\bcatch\s*[({]", File.ReadAllText(file)));
    }

    // The services find, the gateway decides: the rules for what is instruction-like text are not in the gateway.
    [Fact]
    public void The_gateway_applies_the_risk_policy_but_never_looks_for_findings()
    {
        Assert.All(GatewaySource(), file => Assert.DoesNotContain("InjectionPatterns", File.ReadAllText(file)));
        Assert.All(GatewaySource(), file => Assert.DoesNotContain("RiskScanner", File.ReadAllText(file)));
    }
}
