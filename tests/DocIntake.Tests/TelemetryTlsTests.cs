using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DocIntake.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

namespace DocIntake.Tests;

/// <summary>
/// The services send telemetry to the collector over HTTPS and check its certificate against the release's private CA
/// (<c>OTEL_EXPORTER_OTLP_CERTIFICATE</c>). A collector stand-in with a certificate from a private CA proves both halves:
/// the export arrives when the CA is given, and does not when it is not (the exporter will not talk to a collector it
/// cannot verify).
/// </summary>
public sealed class TelemetryTlsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docintake-otlp-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (X509Certificate2 Server, string CaPath) PrivateCa()
    {
        var from = DateTimeOffset.UtcNow.AddMinutes(-5);
        var until = DateTimeOffset.UtcNow.AddHours(12);
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=test internal CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var ca = caRequest.CreateSelfSigned(from, until.AddHours(1));

        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafRequest = new CertificateRequest("CN=collector", leafKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        leafRequest.CertificateExtensions.Add(names.Build());
        leafRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var leaf = leafRequest.Create(ca, from, until, RandomNumberGenerator.GetBytes(8));
        using var withKey = leaf.CopyWithPrivateKey(leafKey);

        var caPath = Path.Combine(_dir, "ca.crt");
        File.WriteAllText(caPath, ca.ExportCertificatePem());
        return (X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null), caPath);
    }

    /// <summary>An OTLP/HTTP receiver over TLS that counts the trace exports it gets.</summary>
    private static async Task<(WebApplication App, int Port, Func<int> Received)> Collector(X509Certificate2 certificate)
    {
        var received = 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, l => l.UseHttps(certificate)));
        var app = builder.Build();
        app.MapPost("/v1/traces", () => { Interlocked.Increment(ref received); return Results.Bytes([], "application/x-protobuf"); });
        await app.StartAsync();
        return (app, new Uri(app.Urls.First()).Port, () => Volatile.Read(ref received));
    }

    private static async Task ExportOneSpan(int port, string? caPath)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"] = $"https://localhost:{port}";
        builder.Configuration["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        if (caPath is not null)
            builder.Configuration["OTEL_EXPORTER_OTLP_CERTIFICATE"] = caPath;
        builder.AddIntakeTelemetry("tls-test");
        await using var app = builder.Build();
        await app.StartAsync();
        using (var activity = IntakeTelemetry.Source.StartActivity("tls-test-span"))
            Assert.NotNull(activity);
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000);
        await app.StopAsync();
    }

    [Fact]
    public async Task Telemetry_reaches_a_collector_whose_certificate_the_private_ca_signed()
    {
        var (certificate, caPath) = PrivateCa();
        var (collector, port, received) = await Collector(certificate);
        await using var _ = collector;

        await ExportOneSpan(port, caPath);

        Assert.True(received() >= 1, "the collector stand-in never received the export");
    }

    [Fact]
    public async Task Telemetry_is_not_sent_to_a_collector_the_exporter_cannot_verify()
    {
        var (certificate, _) = PrivateCa();
        var (collector, port, received) = await Collector(certificate);
        await using var _ = collector;

        await ExportOneSpan(port, caPath: null);

        Assert.Equal(0, received());
    }
}
