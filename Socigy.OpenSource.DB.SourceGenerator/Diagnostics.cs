using Microsoft.CodeAnalysis;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    /// <summary>
    /// Central catalog of every diagnostic the source generator can emit.
    /// IDs use the <c>SCGDB###</c> prefix and are stable — never renumber an existing one.
    /// All diagnostics share the <c>Socigy.DB</c> category, so any of them can be promoted or
    /// suppressed from a project's <c>.editorconfig</c> via
    /// <c>dotnet_diagnostic.SCGDB###.severity = error|warning|none</c>.
    ///
    /// next free id = SCGDB019
    /// </summary>
    internal static class Diagnostics
    {
        private const string Category = "Socigy.DB";

        // ── Table binding diagnostics ──────────────────────────────────────────────

        public static readonly DiagnosticDescriptor AutoIncrementTypeError = new(
            id: "SCGDB001",
            title: "[AutoIncrement] on unsupported type",
            messageFormat: "[AutoIncrement] can only be applied to short, int, or long — '{0}' is not supported",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor EncryptedComboError = new(
            id: "SCGDB002",
            title: "[Encrypted] cannot be combined with [ValueConvertor]/[JsonColumn]",
            messageFormat: "[Encrypted] on '{0}' cannot be combined with [ValueConvertor], [JsonColumn], or [RawJsonColumn] in this version",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // ── SQL procedure diagnostics ──────────────────────────────────────────────

        public static readonly DiagnosticDescriptor MissingPlaceholder = new(
            id: "SCGDB003",
            title: "SQL procedure has no schema placeholder",
            messageFormat: "SQL procedure '{0}' contains no {{Type.Property}} placeholder; hard-coded column names may drift from the schema. Use a {{Type.Property}} placeholder, or add '-- @ignore warning' to the header to suppress this.",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PlaceholderUnknownType = new(
            id: "SCGDB004",
            title: "Unknown type in SQL placeholder",
            messageFormat: "Placeholder '{0}' references type '{1}' which was not found in the compilation",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PlaceholderUnknownProperty = new(
            id: "SCGDB005",
            title: "Unknown property in SQL placeholder",
            messageFormat: "Placeholder '{0}' references property '{1}' which does not exist on type '{2}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PlaceholderMalformed = new(
            id: "SCGDB006",
            title: "Malformed SQL placeholder",
            messageFormat: "Placeholder '{0}' is malformed; expected {{TypeName}} (table name) or {{TypeName.PropertyName}} (column name)",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PlaceholderNotATable = new(
            id: "SCGDB007",
            title: "SQL placeholder type is not a [Table]",
            messageFormat: "Placeholder '{0}' references type '{1}' which is not annotated with [Table] or [FlagTable]",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor PlaceholderAmbiguousType = new(
            id: "SCGDB008",
            title: "Ambiguous type in SQL placeholder",
            messageFormat: "Placeholder '{0}' simple name '{1}' matches multiple types ({2}); use a fully-qualified name",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ParamDeclaredButUnused = new(
            id: "SCGDB009",
            title: "SQL parameter declared but unused",
            messageFormat: "Parameter '@{0}' is declared with '-- @param' but never referenced in the SQL body of procedure '{1}'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ParamUsedButUndeclared = new(
            id: "SCGDB010",
            title: "SQL parameter used without declaration",
            messageFormat: "SQL body of procedure '{0}' references '@{1}' but no matching '-- @param' declaration exists",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor ReturnTypeNotResolvable = new(
            id: "SCGDB011",
            title: "@returns type not resolvable",
            messageFormat: "'-- @returns' type '{0}' in procedure '{1}' could not be resolved in the compilation",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor MalformedParamLine = new(
            id: "SCGDB012",
            title: "Malformed @param line",
            messageFormat: "Malformed '-- @param' line in procedure '{0}': expected '-- @param name: CSharpType'",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor SqlFileOutsideProcedures = new(
            id: "SCGDB013",
            title: ".sql file outside Procedures folder ignored",
            messageFormat: "SQL file '{0}' is registered as an AdditionalFile but lives outside 'Socigy/Procedures' and was ignored",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor EmptySqlBody = new(
            id: "SCGDB014",
            title: "Empty SQL procedure body",
            messageFormat: "SQL procedure file '{0}' has an empty body after the header and produced no method",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor DuplicateProcedure = new(
            id: "SCGDB015",
            title: "Duplicate generated procedure",
            messageFormat: "Duplicate procedure '{0}' in namespace group '{1}'; file names collide after identifier normalization and only the first was emitted",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        // ── Table definition quality diagnostics ───────────────────────────────────

        public static readonly DiagnosticDescriptor TableNoPrimaryKey = new(
            id: "SCGDB016",
            title: "[Table] class has no primary key",
            messageFormat: "Table class '{0}' declares no [PrimaryKey] column; generated update and delete operations require a primary key",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor TableNoColumns = new(
            id: "SCGDB017",
            title: "[Table] class has no columns",
            messageFormat: "Table class '{0}' has no mapped columns",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public static readonly DiagnosticDescriptor EmptyColumnName = new(
            id: "SCGDB018",
            title: "[Column] name is empty",
            messageFormat: "[Column] on '{0}' has an empty or whitespace name",
            category: Category,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }
}
