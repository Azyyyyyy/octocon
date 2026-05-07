using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Interfold.Api.Helpers;

public static class RecoveryCodeResolver
{
    public static bool TryResolve(string candidate, out string recoveryCode, out string errorCode)
    {
        recoveryCode = "";
        if (string.IsNullOrWhiteSpace(candidate))
        {
            errorCode = "recovery_code_not_provided";
            return false;
        }
        if (!LooksLikeCompactJwe(candidate))
        {
            errorCode = "recovery_code_not_jwe";
            return false;
        }

        if (!TryLoadEncryptionPrivateKey(out var privateKeyPem)
            || !TryDecryptJwe(candidate, privateKeyPem, out recoveryCode))
        {
            errorCode = "decryption_error";
            return false;
        }

        errorCode = "";
        return !string.IsNullOrWhiteSpace(recoveryCode);
    }

    private static bool LooksLikeCompactJwe(string token) => token.Count(ch => ch == '.') == 4;

    private static bool TryLoadEncryptionPrivateKey(out string privateKeyPem)
    {
        privateKeyPem = string.Empty;
        var raw = Environment.GetEnvironmentVariable("ENCRYPTION_PRIVATE_KEY");

        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var normalized = raw.Trim();
        if (normalized.Contains("BEGIN", StringComparison.Ordinal))
        {
            privateKeyPem = normalized.Replace("\\n", "\n", StringComparison.Ordinal);
            return true;
        }

        try
        {
            var bytes = Convert.FromBase64String(normalized);
            privateKeyPem = Encoding.UTF8.GetString(bytes);
            return privateKeyPem.Contains("BEGIN", StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecryptJwe(string compactJwe, string privateKeyPem, out string plaintext)
    {
        plaintext = string.Empty;

        try
        {
            var parts = compactJwe.Split('.');
            if (parts.Length != 5)
                return false;

            var headerJson = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(parts[0]));
            using var header = JsonDocument.Parse(headerJson);
            var alg = header.RootElement.TryGetProperty("alg", out var algProp) ? algProp.GetString() : null;
            var enc = header.RootElement.TryGetProperty("enc", out var encProp) ? encProp.GetString() : null;

            if (!string.Equals(alg, "RSA-OAEP-256", StringComparison.Ordinal)
                || !string.Equals(enc, "A256GCM", StringComparison.Ordinal))
            {
                return false;
            }

            var encryptedKey = WebEncoders.Base64UrlDecode(parts[1]);
            var iv = WebEncoders.Base64UrlDecode(parts[2]);
            var ciphertext = WebEncoders.Base64UrlDecode(parts[3]);
            var tag = WebEncoders.Base64UrlDecode(parts[4]);
            var aad = Encoding.ASCII.GetBytes(parts[0]);

            using var rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            var cek = rsa.Decrypt(encryptedKey, RSAEncryptionPadding.OaepSHA256);

            var decrypted = new byte[ciphertext.Length];
            using var aes = new AesGcm(cek, tag.Length);
            aes.Decrypt(iv, ciphertext, tag, decrypted, aad);

            plaintext = Encoding.UTF8.GetString(decrypted);
            return !string.IsNullOrWhiteSpace(plaintext);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
