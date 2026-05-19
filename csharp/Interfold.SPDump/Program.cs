using System.Security.Cryptography;
using Interfold.Api.Services;
using Interfold.Api.Services.Http;
using Interfold.Domain.Abstractions;
using Interfold.Infrastructure.InMemory;
using Interfold.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

string spToken = "";
if (args.Length >= 1)
{
	spToken = args[0];
}
else
{
	while (string.IsNullOrWhiteSpace(spToken))
	{
		Console.Write("Please put in your SP token:");
		spToken = Console.ReadLine()!;
	}
}

var providedKey = args.Length >= 2 ? args[1] : null;

var services = new ServiceCollection();

// Basic logging
services.AddLogging(builder => builder.AddConsole());
services.AddSingleton<IConfiguration>(new ConfigurationManager());

// Register InMemory persistence support
InMemoryServiceCollectionExtensions.Register();
services.AddInterfoldPersistence(Interfold.Contracts.PersistenceMode.InMemory, cfg =>
{
	cfg.DefaultRegion = "nam";
	cfg.CompatibilityMode = true; // safer defaults for a utility
});

// Domain handlers (some repositories expect handlers registered)
services.AddInterfoldDomainHandlers();

// Register a minimal avatar storage that writes to a temp folder
services.AddSingleton<IAvatarStorage, TempAvatarStorage>();

services.AddTransient<HttpLoggingHandler>();

// HttpClient used by SimplyPluralImportService
services.AddHttpClient("SimplyPlural").AddHttpMessageHandler<HttpLoggingHandler>();

// Register the import service itself (it will resolve repositories from InMemory registration)
services.AddSingleton<ISimplyPluralImportService, SimplyPluralImportService>();

var provider = services.BuildServiceProvider();

using var scope = provider.CreateScope();
var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("SPDump");

// Determine encryption key to use
string encryptionKeyBase64;
if (!string.IsNullOrWhiteSpace(providedKey))
{
	encryptionKeyBase64 = providedKey;
}
else
{
	// Generate a random 32-byte key and base64 encode
	var key = new byte[32];
	RandomNumberGenerator.Fill(key);
	encryptionKeyBase64 = Convert.ToBase64String(key);
	logger.LogInformation("Using generated random encryption key (base64): {KeyPreview}", encryptionKeyBase64[..8] + "...");
}

try
{
	var importService = scope.ServiceProvider.GetRequiredService<ISimplyPluralImportService>();
	importService.WaitForAvatars = true;

	var result = await importService.ImportAsync("", spToken, encryptionKeyBase64);
	if (result.Success)
	{
		logger.LogInformation("Import succeeded. Alters imported: {Count}", result.AlterCount);
		return 0;
	}

	logger.LogError("Import failed: {Reason}", result.Error);
	return 2;
}
catch (Exception ex)
{
	logger.LogError(ex, "Unhandled exception during SP import");
	return 3;
}

// Minimal temp avatar storage used by the dump utility
internal sealed class TempAvatarStorage : IAvatarStorage
{
	public Task<string> SaveSystemAvatarAsync(string systemId, Stream stream, CancellationToken cancellationToken = default)
	{
		return Task.FromResult("");
	}

	public Task<string> SaveAlterAvatarAsync(string systemId, int alterId, Stream stream, CancellationToken cancellationToken = default)
	{
		return Task.FromResult("");
	}

	public Task<bool> DeleteByUrlAsync(string? avatarUrl, CancellationToken cancellationToken = default)
	{
		// no-op for the utility
		return Task.FromResult(false);
	}
}

