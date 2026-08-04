// SPDX-License-Identifier: GPL-2.0-or-later
// Copyright (C) 2026 Douglas J. Cerrato (KB2UKA) and contributors.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Zeus.Hosting;

/// <summary>
/// Creates and caches the self-signed certificate used by Zeus LAN HTTPS
/// listeners. The certificate is shared by the full server and StationEngine;
/// ZeusProduct loads the completed PFX after the Engine readiness probe passes.
/// </summary>
public static class LanCertificate
{
    private const string CertFileName = "zeus-lan.pfx";
    private const int LockAttempts = 100;
    private const int LockRetryMilliseconds = 50;
    private const string SanExtensionOid = "2.5.29.17";
    private const string ServerAuthOid = "1.3.6.1.5.5.7.3.1";
    private static readonly TimeSpan Validity = TimeSpan.FromDays(1825);

    public static X509Certificate2 GetOrCreate(ILogger? log = null)
    {
        var path = ResolveCertPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var certificateLock = AcquireCertificateLock(path);

        var ips = GetLanIps().ToHashSet();

        if (File.Exists(path))
        {
            try
            {
                // The PFX already supplies persistence. PersistKeySet would
                // additionally route the key through the macOS login keychain,
                // which can require unavailable GUI interaction for service,
                // SSH, and CI launches.
                var existing = X509CertificateLoader.LoadPkcs12FromFile(
                    path,
                    string.Empty,
                    X509KeyStorageFlags.Exportable);
                if (CoversAllIps(existing, ips) && existing.NotAfter > DateTime.UtcNow.AddDays(30))
                {
                    log?.LogInformation(
                        "LAN certificate loaded from {Path} ({Subject}, expires {Expires:yyyy-MM-dd})",
                        path,
                        existing.Subject,
                        existing.NotAfter);
                    return existing;
                }

                log?.LogInformation("LAN certificate regenerating: SAN list out of date or near expiry");
                existing.Dispose();
            }
            catch (Exception ex)
            {
                log?.LogWarning(ex, "Existing LAN certificate at {Path} failed to load — regenerating", path);
            }
        }

        var fresh = Generate(ips);
        File.WriteAllBytes(path, fresh.Export(X509ContentType.Pfx, string.Empty));
        log?.LogInformation(
            "LAN certificate generated at {Path} for {IpCount} IP(s): {Ips}",
            path,
            ips.Count,
            string.Join(", ", ips));
        return fresh;
    }

    public static int GetHttpsPort()
        => int.TryParse(Environment.GetEnvironmentVariable("ZEUS_HTTPS_PORT"), out var port)
            ? port
            : 6443;

    public static IReadOnlyList<IPAddress> GetLanIps()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up
                && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(address.Address))
            .Select(address => address.Address)
            .Distinct()
            .ToArray();
    }

    private static X509Certificate2 Generate(HashSet<IPAddress> ips)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN=Zeus on {Environment.MachineName}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        if (!string.IsNullOrEmpty(Environment.MachineName))
            san.AddDnsName(Environment.MachineName);
        san.AddDnsName("zeus.local");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in ips)
            san.AddIpAddress(ip);
        request.CertificateExtensions.Add(san.Build());

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(ServerAuthOid) },
            false));

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.Add(Validity));
        var pfx = certificate.Export(X509ContentType.Pfx, string.Empty);
        return X509CertificateLoader.LoadPkcs12(
            pfx,
            string.Empty,
            X509KeyStorageFlags.Exportable);
    }

    private static bool CoversAllIps(X509Certificate2 certificate, HashSet<IPAddress> required)
    {
        var sanExtension = certificate.Extensions
            .FirstOrDefault(extension => extension.Oid?.Value == SanExtensionOid);
        if (sanExtension is null)
            return false;

        var rendered = sanExtension.Format(false);
        return required.All(ip => rendered.Contains(ip.ToString(), StringComparison.Ordinal));
    }

    private static string ResolveCertPath()
    {
        var directory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);
        return Path.Combine(directory, "Zeus", "certs", CertFileName);
    }

    private static FileStream AcquireCertificateLock(string certificatePath)
    {
        IOException? lastError = null;
        var lockPath = certificatePath + ".lock";
        for (var attempt = 0; attempt < LockAttempts; attempt++)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException ex)
            {
                lastError = ex;
                Thread.Sleep(LockRetryMilliseconds);
            }
        }

        throw new IOException(
            $"Timed out waiting for LAN certificate lock at {lockPath}",
            lastError);
    }
}
