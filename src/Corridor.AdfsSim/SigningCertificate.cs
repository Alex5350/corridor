using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace Corridor.AdfsSim;

/// <summary>Loads the dev signing certificate from the repo certs/ directory. The
/// certificate and key are committed on purpose: they sign synthetic demo tokens only.</summary>
public sealed class SigningCertificate
{
    public SigningCertificate(IOptions<AdfsSimOptions> options, IHostEnvironment environment)
    {
        var opts = options.Value;
        CertificatePath = ResolvePath(opts.CertificatePath, environment.ContentRootPath);
        KeyPath = ResolvePath(opts.KeyPath, environment.ContentRootPath);
        Certificate = X509Certificate2.CreateFromPemFile(CertificatePath, KeyPath);
    }

    public string CertificatePath { get; }

    public string KeyPath { get; }

    public X509Certificate2 Certificate { get; }

    /// <summary>Resolves a configured relative path against a list of sensible roots:
    /// the content root, the current base directory, and the repo root above both.</summary>
    private static string ResolvePath(string configured, string contentRootPath)
    {
        var candidates = new List<string>();

        void AddRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return;
            }

            candidates.Add(Path.GetFullPath(Path.Combine(root, configured)));
            // When running from bin/Debug/net10.0 the repo root sits a few levels up.
            candidates.Add(Path.GetFullPath(Path.Combine(root, "..", "..", "..", "..", configured)));
        }

        AddRoot(contentRootPath);
        AddRoot(AppContext.BaseDirectory);

        var found = candidates.FirstOrDefault(File.Exists);
        return found ?? throw new FileNotFoundException(
            $"Signing material not found. Looked for '{configured}' under the content root and the binary directory. " +
            "Checked: " + string.Join(", ", candidates));
    }
}
