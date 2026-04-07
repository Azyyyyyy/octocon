using System.Net.WebSockets;
using System.Text.Json;
using Interfold.Domain.Accounts;
using Interfold.Domain.Alters;
using Interfold.Domain.Fronting;
using Interfold.Domain.Settings;
using Interfold.Domain.Tags;

namespace Interfold.Api.Socket;

public class WebSocketInitialization
{
public static async Task SendBatchedInitAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    bool replyAsArrayFrame,
    string initJson,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    using var initDoc = JsonDocument.Parse(initJson);
    var root = initDoc.RootElement;

    var alters = root.TryGetProperty("alters", out var altersEl) && altersEl.ValueKind == JsonValueKind.Array
        ? altersEl.EnumerateArray().Select(x => x.GetRawText()).ToList()
        : [];
    var tags = root.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array
        ? tagsEl.EnumerateArray().Select(x => x.GetRawText()).ToList()
        : [];
    var fronts = root.TryGetProperty("fronts", out var frontsEl) && frontsEl.ValueKind == JsonValueKind.Array
        ? frontsEl.EnumerateArray().Select(x => x.GetRawText()).ToList()
        : [];

    await SendBatchedDataAsync(socket, topic, joinReference, replyAsArrayFrame, SocketEventNames.BatchedInit.Alters, "alters", 3000, alters, cancellationToken, sendGate);
    await SendBatchedDataAsync(socket, topic, joinReference, replyAsArrayFrame, SocketEventNames.BatchedInit.Tags, "tags", 1000, tags, cancellationToken, sendGate);
    await SendBatchedDataAsync(socket, topic, joinReference, replyAsArrayFrame, SocketEventNames.BatchedInit.Fronts, "fronts", 50, fronts, cancellationToken, sendGate);

    await WebSocketEvents.SendPhoenixPushAsync(
        socket,
        topic,
        joinReference,
        eventName: SocketEventNames.BatchedInit.Complete,
        payloadJson: "{}",
        replyAsArrayFrame,
        cancellationToken,
        sendGate);
}

static async Task SendBatchedDataAsync(
    WebSocket socket,
    string topic,
    string? joinReference,
    bool replyAsArrayFrame,
    string eventName,
    string dataName,
    int batchSize,
    List<string> data,
    CancellationToken cancellationToken,
    SemaphoreSlim? sendGate = null)
{
    if (data.Count == 0)
    {
        return;
    }

    var totalBatches = (int)Math.Ceiling((double)data.Count / batchSize);
    for (var i = 0; i < totalBatches; i++)
    {
        if (i > 0)
        {
            await Task.Delay(50, cancellationToken);
        }

        var start = i * batchSize;
        var count = Math.Min(batchSize, data.Count - start);
        var batchItems = string.Join(",", data.GetRange(start, count));
        var payloadJson =
            "{" +
            "\"batch_index\":" + (i + 1) + "," +
            "\"total_batches\":" + totalBatches + "," +
            "\"" + dataName + "\":[" + batchItems + "]" +
            "}";

        await WebSocketEvents.SendPhoenixPushAsync(
            socket,
            topic,
            joinReference,
            eventName,
            payloadJson,
            replyAsArrayFrame,
            cancellationToken,
            sendGate);
    }
}
     
     static async Task<(
    AccountPublicProfileReadModel? profile,
    IReadOnlyList<AlterReadModel> alters,
    IReadOnlyList<FrontActiveReadModel> fronts,
    IReadOnlyList<TagReadModel> tags,
    IReadOnlyList<SettingsFieldReadModel> settingsFields,
    EncryptionState? encryptionState)>
FetchSocketInitDataAsync(
    string systemId,
    IAccountRepository accounts,
    IAlterRepository alters,
    IFrontingRepository fronting,
    ITagRepository tags,
    ISettingsFieldRepository settingsFields,
    IEncryptionStateRepository encryptionStates,
    CancellationToken ct)
{
    var profileTask = accounts.GetPublicProfileAsync(systemId, ct);
    var altersTask  = alters.ListAsync(systemId, ct);
    var frontsTask  = fronting.ListActiveAsync(systemId, ct);
    var tagsTask    = tags.ListAsync(systemId, ct);
    var fieldsTask  = settingsFields.ListAsync(systemId, ct);
    var encryptionTask = encryptionStates.GetAsync(systemId, ct);
    await Task.WhenAll(profileTask, altersTask, frontsTask, tagsTask, fieldsTask, encryptionTask);
    return (profileTask.Result, altersTask.Result, frontsTask.Result, tagsTask.Result, fieldsTask.Result, encryptionTask.Result);
}

public static async Task<string> BuildJoinInitJsonAsync(
    HttpContext context,
    string systemId,
    CancellationToken ct)
{
    await using var scope = context.RequestServices.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    var (profile, alters, fronts, tags, settingsFields, encryptionState) = await FetchSocketInitDataAsync(
        systemId,
        sp.GetRequiredService<IAccountRepository>(),
        sp.GetRequiredService<IAlterRepository>(),
        sp.GetRequiredService<IFrontingRepository>(),
        sp.GetRequiredService<ITagRepository>(),
        sp.GetRequiredService<ISettingsFieldRepository>(),
        sp.GetRequiredService<IEncryptionStateRepository>(),
        ct);

    var fields = settingsFields
        .OrderBy(x => x.Index)
        .Select(x => (object)new Dictionary<string, object?>
        {
            ["id"] = x.Id,
            ["name"] = x.Name,
            ["type"] = x.Type,
            ["security_level"] = x.SecurityLevel,
            ["locked"] = x.Locked,
            ["index"] = x.Index
        })
        .ToArray();

    var primaryFront = fronts.FirstOrDefault(x => x.Primary)?.Front.AlterId;

    var qualifyUrl = (string? url) =>
        AvatarUrlQualifier.Qualify(url, context.Request.Scheme, context.Request.Host);

    var system = new Dictionary<string, object?>
    {
        ["id"] = profile?.SystemId ?? systemId,
        ["username"] = profile?.Username,
        ["description"] = profile?.Description,
        ["avatar_url"] = qualifyUrl(profile?.AvatarUrl),
        ["discord_id"] = null,
        ["google_id"] = null,
        ["apple_id"] = null,
        ["email"] = null,
        // Keep parity with Elixir's SystemJSON.data_me defaults.
        ["autoproxy_mode"] = "off",
        ["show_system_tag"] = false,
        ["lifetime_alter_count"] = alters.Count,
        ["primary_front"] = primaryFront,
        ["fields"] = fields,
        ["encryption_initialized"] = encryptionState?.Initialized ?? false
    };

    foreach (var alter in alters)
        alter.AvatarUrl = qualifyUrl(alter.AvatarUrl);

    var initData = new
    {
        system,
        alters,
        fronts,
        tags
    };

    var opts = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
    return JsonSerializer.Serialize(initData, opts);
}

}