using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Corridor.Legacy.Security;

/// <summary>
/// Locates the adfs-sim signing certificate. Tries the configured path, then
/// the usual repo layouts (content root, bin output dir) because the certs/
/// folder sits at the repository root, outside this project.
/// </summary>
public static class AdfsCertLoader
{
    public static X509Certificate2? Load(string? configuredPath)
    {
        foreach (string candidate in CandidatePaths(configuredPath))
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                return X509CertificateLoader.LoadCertificateFromFile(candidate);
            }
            catch (CryptographicException)
            {
                // Not a parseable certificate; try the next candidate.
            }
        }

        return null;
    }

    private static IEnumerable<string> CandidatePaths(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        // dotnet run: current directory is src/Corridor.Legacy, repo root is two levels up.
        yield return Path.Combine(Directory.GetCurrentDirectory(), "certs", "adfs-sim-cert.pem");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "certs", "adfs-sim-cert.pem");

        // Published/binned runs: AppContext.BaseDirectory is src/Corridor.Legacy/bin/Debug/net10.0.
        yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "certs", "adfs-sim-cert.pem");
    }
}
