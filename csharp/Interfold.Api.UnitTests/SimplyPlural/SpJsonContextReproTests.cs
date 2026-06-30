using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Interfold.Api.Services.SimplyPlural;

namespace Interfold.Api.UnitTests.SimplyPlural;

/// <summary>
/// Regression tests for SP JSON parsing edge cases discovered in real captured
/// /v1/customFields and /v1/notes responses. SP's update300 migration copies legacy
/// field/note schemas into their modern collections via `insertOne({ ..., supportMarkdown:
/// legacy.supportMarkdown })`; the legacy schema predates `supportMarkdown`, so the value
/// is undefined, Mongo persists it as null, and the SP API serialises `"supportMarkdown":null`.
///
/// Before the fix, `SpCustomFieldContent.SupportMarkdown` was a non-nullable `bool`, so the
/// entire customFields response failed to deserialise on every legacy-migrated SP system.
/// FetchAsync's catch-all swallowed the JsonException, the importer silently no-op'd the
/// custom-fields step, and the user reported "doesn't seem to get past the custom field
/// step". These tests pin the fix down so the regression cannot return.
/// </summary>
public sealed class SpJsonContextReproTests
{
    // All ids and free-text values in the fixtures below are synthetic placeholders that
    // match SP's wire shape (24-hex ObjectIds, short rank strings, BSON-flavoured oids) but
    // are not captured from any real SP system. The deserialiser only cares that the JSON
    // is structurally valid for the target type, so the byte values are arbitrary.

    [Test]
    public async Task CustomFields_WithNullSupportMarkdown_ShouldDeserializeWithoutThrowing()
    {
        // SP's update300 migration copies legacy custom-field rows into the modern
        // collection without backfilling supportMarkdown, so any field touched by the
        // migration serialises as `"supportMarkdown":null`. The fixture below is the
        // smallest shape that exercises that path through SpJsonContext.
        var json = """[{"exists":true,"id":"000000000000000000000001","content":{"uid":"synthetic-system","name":"SyntheticField","order":"0|aaaaaa:","type":0,"supportMarkdown":null,"buckets":["000000000000000000000002","000000000000000000000003"],"oid":"synthetic-oid-placeholder"}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpCustomFieldContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpCustomFieldContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.Count).IsEqualTo(1);
        await Assert.That(result[0].Content.Name).IsEqualTo("SyntheticField");
        // The whole point of the fix: a null in the JSON binds to a null on the model,
        // and the importer's MapFieldType call site applies SP's documented `?? true`
        // default. We don't assert the importer here (it lives in another assembly), but
        // we confirm the binding stays null so the call-site default path runs.
        await Assert.That(result[0].Content.SupportMarkdown).IsNull();
    }

    [Test]
    public async Task CustomFields_WithExplicitTrueSupportMarkdown_StillDeserializes()
    {
        var json = """[{"exists":true,"id":"000000000000000000000001","content":{"name":"x","type":0,"supportMarkdown":true}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpCustomFieldContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpCustomFieldContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result![0].Content.SupportMarkdown).IsTrue();
    }

    [Test]
    public async Task CustomFields_WithExplicitFalseSupportMarkdown_StillDeserializes()
    {
        var json = """[{"exists":true,"id":"000000000000000000000001","content":{"name":"x","type":0,"supportMarkdown":false}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpCustomFieldContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpCustomFieldContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result![0].Content.SupportMarkdown).IsFalse();
    }

    [Test]
    public async Task Notes_WithNullSupportMarkdown_ShouldDeserializeWithoutThrowing()
    {
        // Defensive coverage: notes from pre-supportMarkdown SP versions can carry the same
        // null shape. We don't read this field today, but parsing the whole notes response
        // would throw if any single note in the list had null - silently emptying the
        // per-alter notes page for any affected system.
        var json = """[{"exists":true,"id":"000000000000000000000004","content":{"title":"t","note":"n","color":"#000","member":"m","date":0,"supportMarkdown":null,"lastOperationTime":0}}]""";

        var typeInfo = (JsonTypeInfo<List<SpEntity<SpNoteContent>>>)
            SpJsonContext.Default.GetTypeInfo(typeof(List<SpEntity<SpNoteContent>>))!;

        var result = JsonSerializer.Deserialize(json, typeInfo);

        await Assert.That(result![0].Content.SupportMarkdown).IsNull();
    }
}
