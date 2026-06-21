using Interfold.Bootstrapper.Phases;
using TUnit.Core;

namespace Interfold.Bootstrapper.UnitTests;

/// <summary>
/// Drives <see cref="ConfigPhase.PromptForConfig(TextReader, TextWriter)"/> with in-memory
/// readers/writers so we can exercise the prompt parsing logic without standing up a real TTY.
/// Each test feeds a fixed answer sequence and asserts the resulting <see cref="Configuration.BootstrapConfig"/>.
///
/// The prompt order in <see cref="ConfigPhase.PromptForConfig"/> is:
///   1. Output directory
///   2. Domains (comma-separated)
///   3. Root CA subject
///   4. Scylla mode
///   5. Google OAuth client secret
///   6. Discord OAuth client secret
/// </summary>
public sealed class ConfigInteractivePromptTests
{
    /// <summary>Builds a <see cref="StringReader"/> that hands back the given lines in order.</summary>
    private static TextReader Lines(params string[] answers) =>
        new StringReader(string.Join('\n', answers) + '\n');

    [Test]
    public async Task PromptUsesDefaultsWhenAllAnswersAreBlank()
    {
        var reader = Lines("", "", "", "", "", "");
        var writer = new StringWriter();

        var config = ConfigPhase.PromptForConfig(reader, writer);

        // All defaults should be preserved from BootstrapConfig's property initialisers.
        await Assert.That(config.Deployment.OutputDir).IsEqualTo("./deploy");
        await Assert.That(config.Deployment.RootCaName).IsEqualTo("Interfold Root CA");
        await Assert.That(config.DatabaseMode).IsEqualTo("single");
        // Even with a blank answer to the domains prompt, the default seed survives.
        await Assert.That(config.Deployment.Domains.Count).IsGreaterThan(0);
        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo(string.Empty);
        await Assert.That(config.OAuth.DiscordClientSecret).IsEqualTo(string.Empty);
    }

    [Test]
    public async Task PromptParsesCommaSeparatedDomains()
    {
        var reader = Lines("./out", "api.example.com,admin.example.com,www.example.com", "", "", "", "");
        var writer = new StringWriter();

        var config = ConfigPhase.PromptForConfig(reader, writer);

        await Assert.That(config.Deployment.Domains.Count).IsEqualTo(3);
        await Assert.That(config.Deployment.Domains).Contains("api.example.com");
        await Assert.That(config.Deployment.Domains).Contains("admin.example.com");
        await Assert.That(config.Deployment.Domains).Contains("www.example.com");
    }

    [Test]
    public async Task PromptTrimsWhitespaceFromAnswers()
    {
        var reader = Lines("  ./trimmed  ", "  foo.example.com  ,  bar.example.com  ", "  My CA  ", "  multi  ", "  GS  ", "  DS  ");
        var writer = new StringWriter();

        var config = ConfigPhase.PromptForConfig(reader, writer);

        await Assert.That(config.Deployment.OutputDir).IsEqualTo("./trimmed");
        await Assert.That(config.Deployment.RootCaName).IsEqualTo("My CA");
        await Assert.That(config.DatabaseMode).IsEqualTo("multi");
        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo("GS");
        await Assert.That(config.OAuth.DiscordClientSecret).IsEqualTo("DS");
        // Comma-split with TrimEntries should drop the surrounding whitespace from each domain.
        await Assert.That(config.Deployment.Domains).Contains("foo.example.com");
        await Assert.That(config.Deployment.Domains).Contains("bar.example.com");
    }

    [Test]
    public async Task PromptCapturesOAuthSecrets()
    {
        var reader = Lines("", "api.example.com", "", "", "google-secret-xyz", "discord-secret-abc");
        var writer = new StringWriter();

        var config = ConfigPhase.PromptForConfig(reader, writer);

        await Assert.That(config.OAuth.GoogleClientSecret).IsEqualTo("google-secret-xyz");
        await Assert.That(config.OAuth.DiscordClientSecret).IsEqualTo("discord-secret-abc");
    }

    [Test]
    public async Task PromptWritesAllPromptsToWriter()
    {
        // The writer is the only side-effect channel the prompt has access to; in a TTY we'd
        // see each prompt rendered as the user types. The integration tests below use a
        // captured StringWriter and assert that every prompt appears, so future refactors that
        // accidentally drop a prompt line still get caught here.
        var reader = Lines("", "", "", "", "", "");
        var writer = new StringWriter();

        ConfigPhase.PromptForConfig(reader, writer);

        var output = writer.ToString();
        await Assert.That(output).Contains("Output directory");
        await Assert.That(output).Contains("domain");
        await Assert.That(output).Contains("Root CA");
        await Assert.That(output).Contains("Database mode");
        await Assert.That(output).Contains("Google OAuth");
        await Assert.That(output).Contains("Discord OAuth");
    }
}
