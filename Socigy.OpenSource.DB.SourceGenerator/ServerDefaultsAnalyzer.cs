using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Socigy.OpenSource.DB.SourceGenerator
{
#nullable enable
    /// <summary>
    /// Reports <see cref="Diagnostics.ServerDefaultsOmitsSetColumn"/> (SCGDB027) at each insert that lets the
    /// server fill a <c>[Default]</c> column the row may carry a value for.
    ///
    /// <para>
    /// <b>Why an analyzer and not the generator.</b> The insert methods this describes are emitted by the
    /// source generator, so from inside the generator they do not exist yet and cannot be bound — a check
    /// living there has to guess at call shapes from syntax. Analyzers run <i>after</i> generation, over a
    /// compilation that already contains the generated code, so every one of those guesses becomes a symbol
    /// lookup: the bound method tells us the row type, whether an <c>InsertFields</c> argument was supplied,
    /// and which parameter is <c>keep</c>.
    /// </para>
    /// <para>
    /// That distinction is not academic. The syntax-based version this replaces recognised only
    /// <c>row.Insert()…</c> and <c>Row.InsertMultipleAsync(…)</c>, and so fired on none of the call sites in a
    /// real application, which reach the same methods through the generated context's table-set property
    /// (<c>db.Rows.InsertAsync(row, …)</c>). It also mistook the <i>rows</i> argument for the <c>keep</c> list
    /// whenever it was an array of variables, and missed any <c>InsertFields</c> value held in a constant.
    /// None of those failure modes are expressible here: there is nothing left to pattern-match.
    /// </para>
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ServerDefaultsAnalyzer : DiagnosticAnalyzer
    {
        private const string InsertFieldsMetadataName = "Socigy.OpenSource.DB.Core.CommandBuilders.InsertFields";
        private const string TableAttributeMetadataName = "Socigy.OpenSource.DB.Attributes.TableAttribute";
        private const string DefaultAttributeMetadataName = "Socigy.OpenSource.DB.Attributes.DefaultAttribute";
        private const string AutoIncrementAttributeMetadataName = "Socigy.OpenSource.DB.Attributes.AutoIncrementAttribute";

        /// <summary>The <c>InsertFields.ServerDefaults</c> member value. Stable: the enum is part of the public API.</summary>
        private const int ServerDefaultsValue = 2;

        /// <summary>The builder method that *is* the ServerDefaults behaviour, and takes no InsertFields.</summary>
        private const string ExcludeAutoFieldsName = "ExcludeAutoFields";

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(Diagnostics.ServerDefaultsOmitsSetColumn);

        public override void Initialize(AnalysisContext context)
        {
            // Never analyse generated files: the library's own emitted code calls these methods internally.
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(start =>
            {
                var insertFields = start.Compilation.GetTypeByMetadataName(InsertFieldsMetadataName);
                var tableAttribute = start.Compilation.GetTypeByMetadataName(TableAttributeMetadataName);
                var defaultAttribute = start.Compilation.GetTypeByMetadataName(DefaultAttributeMetadataName);
                var autoIncrementAttribute = start.Compilation.GetTypeByMetadataName(AutoIncrementAttributeMetadataName);

                // Not a consumer of this library.
                if (insertFields == null || tableAttribute == null || defaultAttribute == null)
                    return;

                var model = new TableModel(tableAttribute, defaultAttribute, autoIncrementAttribute);

                start.RegisterOperationAction(
                    ctx => Analyze(ctx, (IInvocationOperation)ctx.Operation, insertFields, model),
                    OperationKind.Invocation);
            });
        }

        private static void Analyze(
            OperationAnalysisContext ctx, IInvocationOperation invocation,
            INamedTypeSymbol insertFields, TableModel model)
        {
            // 1. Does this call let the server fill [Default] columns?
            if (!LetsServerFillDefaults(invocation, insertFields))
                return;

            // 2. Which row type is being inserted? Read from the bound method rather than guessed from the
            //    receiver, so every call shape that reaches these methods is covered by construction.
            var rowType = ResolveRowType(invocation, model);
            if (rowType == null)
                return;

            var defaultColumns = model.DefaultColumnsOf(rowType);
            if (defaultColumns.IsEmpty)
                return;

            // 3. Which of them does the call name in keep? Unknown means "already handled" — a diagnostic
            //    that guesses wrong in an audit costs more than one that stays quiet.
            var kept = ResolveKeptColumns(invocation, out bool keepIsUnknown);
            if (keepIsUnknown)
                return;

            var omitted = defaultColumns.Where(c => !kept.Contains(c.PropertyName) && !kept.Contains(c.ColumnName))
                                        .Select(c => c.PropertyName)
                                        .ToImmutableArray();
            if (omitted.IsEmpty)
                return;

            ctx.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ServerDefaultsOmitsSetColumn,
                invocation.Syntax.GetLocation(),
                string.Join(", ", omitted),
                rowType.ToDisplayString()));
        }

        // ── 1. does the call omit [Default] columns? ────────────────────────────────────────────────────

        private static bool LetsServerFillDefaults(IInvocationOperation invocation, INamedTypeSymbol insertFields)
        {
            // ExcludeAutoFields() with no include list is the fluent spelling of ServerDefaults.
            if (invocation.TargetMethod.Name == ExcludeAutoFieldsName)
                return true;

            // Any method taking an InsertFields is an insert path by construction — no method-name list
            // needed, so an overload added later is covered without touching this analyzer.
            foreach (var argument in invocation.Arguments)
            {
                if (!SymbolEqualityComparer.Default.Equals(argument.Parameter?.Type, insertFields))
                    continue;

                // An omitted argument still has a value — the parameter's own default, which is NOT always
                // InsertFields.Default: the keepColumns overloads default it to ServerDefaults, because
                // supplying a keep list implies the server fills what the list does not name. Read the
                // declared default rather than assuming.
                if (argument.ArgumentKind == ArgumentKind.DefaultValue)
                {
                    return argument.Parameter is { HasExplicitDefaultValue: true, ExplicitDefaultValue: int declared }
                        && declared == ServerDefaultsValue;
                }

                return TryResolveEnumValue(argument.Value, out int value) && value == ServerDefaultsValue;
            }

            // Supplying keepColumns implies ServerDefaults for the columns it does not name.
            return invocation.Arguments.Any(a => a.Parameter?.Name == "keepColumns"
                                                 && a.ArgumentKind != ArgumentKind.DefaultValue);
        }

        /// <summary>
        /// The integer value behind an <c>InsertFields</c> argument, following a constant or a field/local
        /// initialised from one — the "shared constant" spelling, which is how most call sites in a real
        /// codebase name it rather than repeating the enum member inline.
        /// </summary>
        private static bool TryResolveEnumValue(IOperation operation, out int value)
        {
            value = 0;

            // Unwrap the implicit conversion an enum argument may carry.
            while (operation is IConversionOperation conversion)
                operation = conversion.Operand;

            if (operation.ConstantValue is { HasValue: true, Value: int direct })
            {
                value = direct;
                return true;
            }

            // A `static readonly` field, or a local, holding the value: follow it to its initializer.
            ISymbol? referenced = operation switch
            {
                IFieldReferenceOperation field => field.Field,
                ILocalReferenceOperation local => local.Local,
                IPropertyReferenceOperation property => property.Property,
                _ => null,
            };
            if (referenced == null)
                return false;

            if (referenced is IFieldSymbol { HasConstantValue: true, ConstantValue: int constant })
            {
                value = constant;
                return true;
            }

            return TryResolveInitializer(referenced, out value);
        }

        private static bool TryResolveInitializer(ISymbol symbol, out int value)
        {
            value = 0;
            foreach (var reference in symbol.DeclaringSyntaxReferences)
            {
                var initializer = reference.GetSyntax() switch
                {
                    VariableDeclaratorSyntax declarator => declarator.Initializer?.Value,
                    PropertyDeclarationSyntax property => property.Initializer?.Value ?? property.ExpressionBody?.Expression,
                    _ => null,
                };

                if (initializer != null && TryGetEnumMemberValue(initializer) is { } resolved)
                {
                    value = resolved;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Reads <c>InsertFields.ServerDefaults</c> written out in an initializer. Syntax-level on purpose:
        /// the initializer may live in another document, where this analyzer has no semantic model, and the
        /// only shape worth recognising is the enum member spelled literally.
        /// </summary>
        private static int? TryGetEnumMemberValue(ExpressionSyntax expression)
            => expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var member }
                ? member switch
                {
                    "ServerDefaults" => ServerDefaultsValue,
                    "Default" => 0,
                    "IncludeAutoIncrement" => 1,
                    "ServerDefaultsWhenUnset" => 3,
                    _ => null,
                }
                : null;

        // ── 2. which row type? ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds the inserted row type among everything the bound invocation exposes: the parameters (an
        /// entity, or a sequence of them), the containing type (a generic command builder, or the row class
        /// itself for a static method), and the receiver's type arguments. The first that is a
        /// <c>[Table]</c> class wins, so no call shape has to be enumerated.
        /// </summary>
        private static INamedTypeSymbol? ResolveRowType(IInvocationOperation invocation, TableModel model)
        {
            foreach (var candidate in Candidates(invocation))
            {
                var named = Unwrap(candidate);
                if (named != null && model.IsTable(named))
                    return named;
            }
            return null;

            static IEnumerable<ITypeSymbol?> Candidates(IInvocationOperation invocation)
            {
                foreach (var parameter in invocation.TargetMethod.Parameters)
                    yield return parameter.Type;

                yield return invocation.TargetMethod.ContainingType;
                foreach (var argument in invocation.TargetMethod.ContainingType?.TypeArguments ?? ImmutableArray<ITypeSymbol>.Empty)
                    yield return argument;

                foreach (var argument in invocation.TargetMethod.TypeArguments)
                    yield return argument;

                yield return invocation.Instance?.Type;
                if (invocation.Instance?.Type is INamedTypeSymbol { TypeArguments: { IsEmpty: false } args })
                    foreach (var argument in args)
                        yield return argument;
            }
        }

        /// <summary>The type itself, or its element type when it is an array or an <c>IEnumerable&lt;T&gt;</c>.</summary>
        private static INamedTypeSymbol? Unwrap(ITypeSymbol? type)
        {
            if (type is IArrayTypeSymbol array)
                return array.ElementType as INamedTypeSymbol;

            if (type is not INamedTypeSymbol named)
                return null;

            if (named.TypeKind == TypeKind.Class && named.TypeArguments.IsEmpty)
                return named;

            // IEnumerable<T> and friends: the row type is the element.
            var enumerable = named.AllInterfaces
                .Concat(named.TypeKind == TypeKind.Interface ? new[] { named } : Array.Empty<INamedTypeSymbol>())
                .FirstOrDefault(i => i.MetadataName == "IEnumerable`1");

            return enumerable?.TypeArguments.FirstOrDefault() as INamedTypeSymbol ?? named;
        }

        // ── 3. what does keep name? ────────────────────────────────────────────────────────────────────

        private static ImmutableHashSet<string> ResolveKeptColumns(IInvocationOperation invocation, out bool unknown)
        {
            unknown = false;

            foreach (var argument in invocation.Arguments)
            {
                var name = argument.Parameter?.Name;
                if (name is not ("keep" or "keepColumns" and not null) && name != "include")
                    continue;
                if (argument.ArgumentKind == ArgumentKind.DefaultValue)
                    continue;

                var value = argument.Value;
                while (value is IConversionOperation conversion)
                    value = conversion.Operand;

                // keepColumns: string[] — read the literal names, or give up if any element is not constant.
                if (value is IArrayCreationOperation { Initializer: { } initializer })
                {
                    var builder = ImmutableHashSet.CreateBuilder(StringComparer.Ordinal);
                    foreach (var element in initializer.ElementValues)
                    {
                        if (element.ConstantValue is { HasValue: true, Value: string literal })
                            builder.Add(literal);
                        else
                        {
                            unknown = true;
                            return ImmutableHashSet<string>.Empty;
                        }
                    }
                    return builder.ToImmutable();
                }

                // keep: r => new object?[] { r.Id, r.OccurredAt } — the members bind to real properties.
                if (value is IDelegateCreationOperation { Target: IAnonymousFunctionOperation lambda })
                    return PropertiesReferencedIn(lambda);
                if (value is IAnonymousFunctionOperation direct)
                    return PropertiesReferencedIn(direct);

                // Anything else (a variable, a method call) cannot be read here.
                unknown = true;
                return ImmutableHashSet<string>.Empty;
            }

            return ImmutableHashSet<string>.Empty;
        }

        private static ImmutableHashSet<string> PropertiesReferencedIn(IOperation lambda)
        {
            var builder = ImmutableHashSet.CreateBuilder(StringComparer.Ordinal);
            Walk(lambda);
            return builder.ToImmutable();

            void Walk(IOperation operation)
            {
                if (operation is IPropertyReferenceOperation property)
                    builder.Add(property.Property.Name);
                foreach (var child in operation.ChildOperations)
                    Walk(child);
            }
        }

        // ── the [Table] model ──────────────────────────────────────────────────────────────────────────

        private sealed class TableModel
        {
            private readonly INamedTypeSymbol _tableAttribute;
            private readonly INamedTypeSymbol _defaultAttribute;
            private readonly INamedTypeSymbol? _autoIncrementAttribute;

            public TableModel(INamedTypeSymbol tableAttribute, INamedTypeSymbol defaultAttribute, INamedTypeSymbol? autoIncrementAttribute)
            {
                _tableAttribute = tableAttribute;
                _defaultAttribute = defaultAttribute;
                _autoIncrementAttribute = autoIncrementAttribute;
            }

            public bool IsTable(INamedTypeSymbol type)
                => type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _tableAttribute));

            /// <summary>
            /// The <c>[Default]</c>, non-auto-increment columns of a row type, with both names a <c>keep</c>
            /// list may use. Auto-increment columns are excluded deliberately: the database generating an
            /// identity is the point of the column, not a surprise, and flagging it would bury the signal.
            /// </summary>
            public ImmutableArray<(string PropertyName, string ColumnName)> DefaultColumnsOf(INamedTypeSymbol type)
            {
                var builder = ImmutableArray.CreateBuilder<(string, string)>();
                foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
                {
                    var attributes = property.GetAttributes();
                    bool hasDefault = attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _defaultAttribute));
                    if (!hasDefault) continue;

                    bool isAutoIncrement = _autoIncrementAttribute != null
                        && attributes.Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, _autoIncrementAttribute));
                    if (isAutoIncrement) continue;

                    builder.Add((property.Name, ColumnNaming.ResolveDbColumnName(property, "Socigy.OpenSource.DB.Attributes.ColumnAttribute")));
                }
                return builder.ToImmutable();
            }
        }
    }
#nullable disable
}
