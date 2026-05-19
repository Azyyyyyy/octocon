using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Interfold.Domain;

public class EncryptionKey
{
    public static string DeriveKey(string pepper, string systemId, string recoveryCode, string salt)
    {
        // Step 1: SHA256(pepper + user_id + recovery_code)
        var hashInput = Encoding.UTF8.GetBytes(pepper + systemId + recoveryCode);
        var sha256Hash = SHA256.HashData(hashInput);

        // Step 2: Argon2id(sha256_hash, salt, t_cost=12, m_cost=65536, parallelism=1, hash_len=32)
        var saltBytes = Convert.FromBase64String(salt);
        using var argon2 = new Argon2id(sha256Hash);
        argon2.Salt = saltBytes;
        argon2.DegreeOfParallelism = 1;
        argon2.MemorySize = 65536; // KB
        argon2.Iterations = 12;

        var keyBytes = argon2.GetBytes(32);
        return Convert.ToBase64String(keyBytes);
    }

    public static string DeriveChecksum(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hash)[..9];
    }
}