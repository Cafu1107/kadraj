using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kadraj.Server;

/// <summary>
/// Per-install secrets kept in %LocalAppData%\Kadraj: the self-signed HTTPS certificate
/// (phone browsers only allow camera access on HTTPS) and the pairing key carried in the QR code.
/// Both are encrypted with DPAPI for the current Windows user, so other accounts and copies of the
/// files on another PC can't use them.
/// </summary>
public static class Credentials
{
    static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kadraj");

    static readonly byte[] Entropy = "Kadraj credentials v1"u8.ToArray();

    public static string LoadOrCreatePairingKey()
    {
        if (ReadProtected("pairing.bin", legacyPlainFile: "pairing.key") is { } stored)
        {
            var existing = Encoding.ASCII.GetString(stored).Trim();
            if (existing.Length == 32) return existing;
        }
        return NewPairingKey();
    }

    /// <summary>Makes a new key; phones paired with the old QR code can no longer connect.</summary>
    public static string NewPairingKey()
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        WriteProtected("pairing.bin", Encoding.ASCII.GetBytes(key));
        return key;
    }

    /// <summary>
    /// Reuses the saved certificate while it still covers every current LAN address, so the phone's
    /// "accept this certificate" choice survives restarts. A new address means a new certificate.
    /// </summary>
    public static X509Certificate2 LoadOrCreateCertificate(IEnumerable<IPAddress> addresses)
    {
        var wanted = addresses.Append(IPAddress.Loopback).Distinct().ToList();

        if (ReadProtected("https.bin", legacyPlainFile: "https.pfx") is { } stored)
        {
            try
            {
                var cert = X509CertificateLoader.LoadPkcs12(stored, null);
                var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
                var covered = san?.EnumerateIPAddresses().ToHashSet() ?? [];
                if (cert.NotAfter > DateTime.Now.AddDays(30) && wanted.All(covered.Contains))
                    return cert;
                cert.Dispose();
            }
            catch (CryptographicException)
            {
                // Unreadable: fall through and replace it.
            }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Kadraj", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        foreach (var ip in wanted) sanBuilder.AddIpAddress(ip);
        request.CertificateExtensions.Add(sanBuilder.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication

        // iOS rejects TLS certificates valid for longer than ~825 days; stay well under.
        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(365));
        var pfx = created.Export(X509ContentType.Pfx);
        WriteProtected("https.bin", pfx);

        // Reload from PFX: SslStream on Windows cannot use the ephemeral key CreateSelfSigned returns.
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    /// <summary>Reads a DPAPI-protected file; a plaintext file from an older version is encrypted and removed.</summary>
    static byte[]? ReadProtected(string name, string legacyPlainFile)
    {
        var path = Path.Combine(Folder, name);
        try
        {
            if (File.Exists(path))
                return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);

            var legacy = Path.Combine(Folder, legacyPlainFile);
            if (!File.Exists(legacy)) return null;
            var plain = File.ReadAllBytes(legacy);
            WriteProtected(name, plain);
            File.Delete(legacy);
            return plain;
        }
        catch (Exception e) when (e is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return null; // e.g. copied from another account: start fresh
        }
    }

    static void WriteProtected(string name, byte[] data)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllBytes(Path.Combine(Folder, name), ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
    }
}
