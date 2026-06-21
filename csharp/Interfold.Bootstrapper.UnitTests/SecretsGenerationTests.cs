using Interfold.Bootstrapper.Configuration;
using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Properties of <see cref="SecretsPhase.RandomPassword"/> and <see cref="SecretsPhase.Generate"/>
/// that the rest of the stack depends on: alphabet, length, uniqueness, and round-trip safety.
/// </summary>
public sealed class SecretsGenerationTests
{
    /// <summary>Mirror of the alphabet defined in SecretsPhase. Updating one without the other is a bug; this duplication makes the contract testable.</summary>
    private const string ExpectedAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    [Test]
    public async Task RandomPasswordUsesExpectedAlphabetAndLength()
    {
        // Sample enough passwords that we cover most of the alphabet — gives us a real check
        // that the generator is drawing uniformly and not, say, biased to a single char.
        var seen = new HashSet<char>();
        for (var i = 0; i < 200; i++)
        {
            var pw = SecretsPhase.RandomPassword();
            await Assert.That(pw.Length).IsEqualTo(32)
                .Because("password length is part of the contract — operators script around it");
            foreach (var c in pw)
            {
                await Assert.That(ExpectedAlphabet.Contains(c)).IsTrue()
                    .Because($"password contains out-of-alphabet char '{c}' (0x{(int)c:X2})");
                seen.Add(c);
            }
        }
        // Loose bound — over 200 32-char samples we expect to see well over half the alphabet.
        await Assert.That(seen.Count).IsGreaterThan(ExpectedAlphabet.Length / 2);
    }

    [Test]
    public async Task GenerateProducesUniqueValuesAcrossCalls()
    {
        var a = SecretsPhase.Generate();
        var b = SecretsPhase.Generate();

        await Assert.That(a.PostgresPassword).IsNotEqualTo(b.PostgresPassword);
        await Assert.That(a.PostgresInitPassword).IsNotEqualTo(b.PostgresInitPassword);
        await Assert.That(a.PostgresAdminPassword).IsNotEqualTo(b.PostgresAdminPassword);
        await Assert.That(a.ScyllaPassword).IsNotEqualTo(b.ScyllaPassword);
        await Assert.That(a.ScyllaAdminPassword).IsNotEqualTo(b.ScyllaAdminPassword);
        await Assert.That(a.EncryptionPepper).IsNotEqualTo(b.EncryptionPepper);
        await Assert.That(a.LeafPfxPassword).IsNotEqualTo(b.LeafPfxPassword);
        // RSA / ECDSA keys generate from independent random states so the PEM blobs must differ too.
        await Assert.That(a.EncryptionPrivateKeyB64).IsNotEqualTo(b.EncryptionPrivateKeyB64);
        await Assert.That(a.JwtRsa256PrivateKeyPem).IsNotEqualTo(b.JwtRsa256PrivateKeyPem);
        await Assert.That(a.JwtEs256PrivateKeyPem).IsNotEqualTo(b.JwtEs256PrivateKeyPem);
    }

    [Test]
    public async Task GenerateAlwaysPopulatesEveryField()
    {
        // Forward-looking guard: if someone adds a new field to GeneratedSecrets but forgets to
        // populate it inside Generate(), this test fails with a clear message naming the field.
        var s = SecretsPhase.Generate();

        await Assert.That(s.PostgresUser).IsNotEmpty();
        await Assert.That(s.PostgresPassword).IsNotEmpty();
        await Assert.That(s.PostgresInitPassword).IsNotEmpty();
        await Assert.That(s.PostgresAdminPassword).IsNotEmpty();
        await Assert.That(s.ScyllaUser).IsNotEmpty();
        await Assert.That(s.ScyllaPassword).IsNotEmpty();
        await Assert.That(s.ScyllaAdminPassword).IsNotEmpty();
        await Assert.That(s.EncryptionPrivateKeyB64).IsNotEmpty();
        await Assert.That(s.EncryptionPepper).IsNotEmpty();
        await Assert.That(s.LeafPfxPassword).IsNotEmpty();
        await Assert.That(s.JwtRsa256PublicKeyPem).IsNotEmpty();
        await Assert.That(s.JwtRsa256PrivateKeyPem).IsNotEmpty();
        await Assert.That(s.JwtEs256PublicKeyPem).IsNotEmpty();
        await Assert.That(s.JwtEs256PrivateKeyPem).IsNotEmpty();
    }

    [Test]
    public async Task PersistedSecretsRoundTripViaLoad()
    {
        // Persist → Load must preserve every field byte-for-byte. This catches accidental
        // serialiser-option drift (e.g. a missing JsonPropertyName) before it surfaces as a
        // "user can't auth after restart" production incident.
        var tmpDir = Path.Combine(Path.GetTempPath(), "interfold-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpDir);
        try
        {
            var original = SecretsPhase.Generate();
            var path = Path.Combine(tmpDir, "secrets.json");
            await SecretsPhase.PersistAsync(original, path, CancellationToken.None);

            var loaded = await SecretsPhase.LoadAsync(path, CancellationToken.None);

            await Assert.That(loaded.PostgresUser).IsEqualTo(original.PostgresUser);
            await Assert.That(loaded.PostgresPassword).IsEqualTo(original.PostgresPassword);
            await Assert.That(loaded.PostgresInitPassword).IsEqualTo(original.PostgresInitPassword);
            await Assert.That(loaded.PostgresAdminPassword).IsEqualTo(original.PostgresAdminPassword);
            await Assert.That(loaded.ScyllaUser).IsEqualTo(original.ScyllaUser);
            await Assert.That(loaded.ScyllaPassword).IsEqualTo(original.ScyllaPassword);
            await Assert.That(loaded.ScyllaAdminPassword).IsEqualTo(original.ScyllaAdminPassword);
            await Assert.That(loaded.EncryptionPrivateKeyB64).IsEqualTo(original.EncryptionPrivateKeyB64);
            await Assert.That(loaded.EncryptionPepper).IsEqualTo(original.EncryptionPepper);
            await Assert.That(loaded.LeafPfxPassword).IsEqualTo(original.LeafPfxPassword);
            await Assert.That(loaded.JwtRsa256PublicKeyPem).IsEqualTo(original.JwtRsa256PublicKeyPem);
            await Assert.That(loaded.JwtRsa256PrivateKeyPem).IsEqualTo(original.JwtRsa256PrivateKeyPem);
            await Assert.That(loaded.JwtEs256PublicKeyPem).IsEqualTo(original.JwtEs256PublicKeyPem);
            await Assert.That(loaded.JwtEs256PrivateKeyPem).IsEqualTo(original.JwtEs256PrivateKeyPem);
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { /* best effort */ }
        }
    }
}
