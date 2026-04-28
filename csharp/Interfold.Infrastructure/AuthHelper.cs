using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Interfold.Contracts.Configuration;

namespace Interfold.Infrastructure;

public class AuthHelper
{
    public static string CreateToken(AuthenticationConfiguration authConfig, DateTimeOffset expiresAt, DateTimeOffset now, string jti, string systemId)
    {
        var headerJson = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["alg"] = "ES256",
            ["typ"] = "JWT"
        });

        var payloadJson = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["iss"] = authConfig.JwtAuthority,
            ["sub"] = systemId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
            ["exp"] = expiresAt.ToUnixTimeSeconds(),
            ["jti"] = jti,
            ["scope"] = "octocon:deeplink"
        });

        var encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        var encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        var signingInput = $"{encodedHeader}.{encodedPayload}";

        // ES256 signing with ECDSA P-256
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(NormalizePem(authConfig.JwtEs256PrivateKeyPem!).AsSpan());
        var signature = ecdsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var encodedSignature = Base64UrlEncode(signature);

        var token = $"{signingInput}.{encodedSignature}";
        return token;
    }
    
    public static void EnsureEs256KeyMaterial(AuthenticationConfiguration opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.JwtEs256PrivateKeyPem))
        {
            return;
        }

        var privateKeyPath = opts.JwtEs256PrivateKeyFile;
        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            privateKeyPath = Path.Combine(AppContext.BaseDirectory, "keys", "octocon-es256-private.pem");
            opts.JwtEs256PrivateKeyFile = privateKeyPath;
        }

        var publicKeyPath = opts.JwtEs256PublicKeyFile;
        if (string.IsNullOrWhiteSpace(publicKeyPath))
        {
            var keyDir = Path.GetDirectoryName(privateKeyPath) ?? AppContext.BaseDirectory;
            publicKeyPath = Path.Combine(keyDir, "octocon-es256-public.pem");
            opts.JwtEs256PublicKeyFile = publicKeyPath;
        }

        if (File.Exists(privateKeyPath))
        {
            opts.JwtEs256PrivateKeyPem = File.ReadAllText(privateKeyPath);
            return;
        }

        var privateKeyDirectory = Path.GetDirectoryName(privateKeyPath);
        if (!string.IsNullOrWhiteSpace(privateKeyDirectory))
        {
            Directory.CreateDirectory(privateKeyDirectory);
        }

        var publicKeyDirectory = Path.GetDirectoryName(publicKeyPath);
        if (!string.IsNullOrWhiteSpace(publicKeyDirectory))
        {
            Directory.CreateDirectory(publicKeyDirectory);
        }

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privatePem = ecdsa.ExportECPrivateKeyPem();
        var publicPem = ecdsa.ExportSubjectPublicKeyInfoPem();

        File.WriteAllText(privateKeyPath, privatePem);
        File.WriteAllText(publicKeyPath, publicPem);

        opts.JwtEs256PrivateKeyPem = privatePem;
    }
    
    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string NormalizePem(string pem)
        => pem.Replace("\\n", "\n", StringComparison.Ordinal);
}