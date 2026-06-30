namespace Interfold.Contracts.Models.ImportOperations;

/// <summary>
/// The third-party platform an import operation is pulling data from. Stored as the
/// lowercase enum-name string in the <c>import_operations.kind</c> column so a future
/// integration only needs to add a value here (and a matching socket-frame constant
/// in <c>SocketEventNames.Imports</c>).
/// </summary>
public static class ImportOperationKinds
{
    /// <summary>Simply Plural — handled by <c>SimplyPluralImportService</c>.</summary>
    public const string SimplyPlural = "sp";

    /// <summary>PluralKit — handler is currently a stub but inherits the same dispatch model.</summary>
    public const string PluralKit = "pk";
}
