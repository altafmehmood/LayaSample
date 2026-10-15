using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace DocIntake.Api;

/// <summary>
/// Settings for serving HTTPS from a certificate on disk, bound from the <c>Tls</c> section. In Kubernetes the certificate
/// is issued by the release's private CA (cert-manager) and mounted from a Secret; the Gateway checks it against that CA.
/// Without a <see cref="CertificatePath"/> the gateway keeps serving plain HTTP (docker-compose and <c>dotnet run</c>).
/// </summary>
internal sealed class TlsOptions
{
    public const string Section = "Tls";

    public string? CertificatePath { get; set; }
    public string? KeyPath { get; set; }
    public int Port { get; set; } = 8443;

    public bool Enabled => !string.IsNullOrWhiteSpace(CertificatePath);
}

/// <summary>
/// The server certificate, re-read when its files change. cert-manager renews it in place well before it expires and the
/// kubelet updates the mounted files; Kestrel on its own keeps serving the certificate it loaded at start-up, so without this
/// the pod would present an expired certificate until it was restarted.
/// </summary>
internal sealed class ReloadingCertificate
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(30);

    private readonly string _certificatePath;
    private readonly string _keyPath;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private X509Certificate2 _current;
    private DateTime _writtenUtc;
    private DateTimeOffset _nextCheck;

    public ReloadingCertificate(string certificatePath, string keyPath, TimeProvider? time = null)
    {
        _certificatePath = certificatePath;
        _keyPath = keyPath;
        _time = time ?? TimeProvider.System;
        (_current, _writtenUtc) = Load();
        _nextCheck = _time.GetUtcNow() + CheckEvery;
    }

    /// <summary>The certificate to present now. A reload that fails (a half-written file) keeps the last good one.</summary>
    public X509Certificate2 Current
    {
        get
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                if (now < _nextCheck)
                    return _current;
                _nextCheck = now + CheckEvery;
                try
                {
                    if (WrittenUtc() != _writtenUtc)
                        (_current, _writtenUtc) = Load();
                }
                catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException)
                {
                    // Try again at the next check; the old certificate is still being served.
                }
                return _current;
            }
        }
    }

    private DateTime WrittenUtc() => new[] { File.GetLastWriteTimeUtc(_certificatePath), File.GetLastWriteTimeUtc(_keyPath) }.Max();

    private (X509Certificate2, DateTime) Load()
    {
        var written = WrittenUtc();
        using var pem = X509Certificate2.CreateFromPemFile(_certificatePath, _keyPath);
        // Re-imported from PKCS#12 so the private key is usable by the TLS stack on every platform.
        return (X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx), null), written);
    }
}

internal static class TlsSetup
{
    /// <summary>
    /// When <c>Tls:CertificatePath</c> is set, the gateway serves HTTPS on <c>Tls:Port</c> and no plain HTTP at all, so the
    /// hop from the Gateway to the pod is encrypted. Otherwise Kestrel keeps its default (HTTP on 8080).
    /// </summary>
    public static WebApplicationBuilder AddInternalTls(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(TlsOptions.Section).Get<TlsOptions>() ?? new();
        if (!options.Enabled)
            return builder;
        if (string.IsNullOrWhiteSpace(options.KeyPath))
            throw new InvalidOperationException("Tls:KeyPath is required when Tls:CertificatePath is set.");

        var certificate = new ReloadingCertificate(options.CertificatePath!, options.KeyPath);
        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(options.Port, listen =>
            listen.UseHttps(new HttpsConnectionAdapterOptions { ServerCertificateSelector = (_, _) => certificate.Current })));
        return builder;
    }
}
