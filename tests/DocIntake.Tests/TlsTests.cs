using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DocIntake.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace DocIntake.Tests;

/// <summary>The gateway serving HTTPS from a certificate on disk, and picking up the one cert-manager renews it with.</summary>
public sealed class TlsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("docintake-tls-").FullName;
    private string CertPath => Path.Combine(_dir, "tls.crt");
    private string KeyPath => Path.Combine(_dir, "tls.key");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Writes a new self-signed certificate and key as PEM, as cert-manager's Secret mount does, and returns its thumbprint.</summary>
    private string Issue(string name, int writtenSecondsAgo = 0)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new SubjectAlternativeNameExtension(name));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(CertPath, cert.ExportCertificatePem());
        File.WriteAllText(KeyPath, key.ExportPkcs8PrivateKeyPem());
        File.SetLastWriteTimeUtc(CertPath, DateTime.UtcNow.AddSeconds(-writtenSecondsAgo));
        File.SetLastWriteTimeUtc(KeyPath, DateTime.UtcNow.AddSeconds(-writtenSecondsAgo));
        return cert.Thumbprint;
    }

    private sealed class SubjectAlternativeNameExtension : X509Extension
    {
        public SubjectAlternativeNameExtension(string dns) : base(new Oid("2.5.29.17"), Encode(dns), false) { }

        private static byte[] Encode(string dns)
        {
            var builder = new SubjectAlternativeNameBuilder();
            builder.AddDnsName(dns);
            return builder.Build().RawData;
        }
    }

    [Fact]
    public void A_renewed_certificate_is_served_without_a_restart()
    {
        var first = Issue("api.test");
        var clock = new Clock(DateTimeOffset.UtcNow);
        var certificate = new ReloadingCertificate(CertPath, KeyPath, clock);
        Assert.Equal(first, certificate.Current.Thumbprint);

        var second = Issue("api.test");
        // Not yet time to look: the files are only checked every 30 seconds.
        Assert.Equal(first, certificate.Current.Thumbprint);

        clock.Now += TimeSpan.FromSeconds(31);
        Assert.Equal(second, certificate.Current.Thumbprint);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_half_written_renewal_keeps_the_last_good_certificate()
    {
        var first = Issue("api.test");
        var clock = new Clock(DateTimeOffset.UtcNow);
        var certificate = new ReloadingCertificate(CertPath, KeyPath, clock);

        File.WriteAllText(CertPath, "-----BEGIN CERTIFICATE-----\ntruncated");
        clock.Now += TimeSpan.FromSeconds(31);

        Assert.Equal(first, certificate.Current.Thumbprint);
    }

    [Fact]
    public async Task The_gateway_listens_on_https_only_and_presents_the_mounted_certificate()
    {
        var thumbprint = Issue("api.test");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration["Tls:CertificatePath"] = CertPath;
        builder.Configuration["Tls:KeyPath"] = KeyPath;
        builder.Configuration["Tls:Port"] = "0";
        builder.AddInternalTls();
        await using var app = builder.Build();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync(TestContext.Current.CancellationToken);

        var addresses = app.Urls.ToList();
        Assert.All(addresses, a => Assert.StartsWith("https://", a));
        var port = new Uri(addresses[0]).Port;

        string? presented = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
            {
                presented = cert?.Thumbprint;
                // Self-signed and dialled by IP: only the chain and the name may be wrong; the thumbprint below is the real check.
                return (errors & ~(SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch)) == 0;
            }
        };
        using var client = new HttpClient(handler);
        var response = await client.GetAsync($"https://127.0.0.1:{port}/ping", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(thumbprint, presented);
        // Plain HTTP on that port is refused: the hop from the Gateway cannot be downgraded.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => new HttpClient().GetAsync($"http://127.0.0.1:{port}/ping", TestContext.Current.CancellationToken));
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Without_a_certificate_path_the_gateway_keeps_serving_plain_http()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.Configuration["urls"] = "http://127.0.0.1:0";
        builder.AddInternalTls();
        await using var app = builder.Build();
        app.MapGet("/ping", () => "pong");
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient();
        var response = await client.GetAsync($"{app.Urls.Single()}/ping", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_certificate_without_a_key_is_a_start_up_error()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Tls:CertificatePath"] = CertPath;
        Assert.Throws<InvalidOperationException>(() => builder.AddInternalTls());
    }
}
