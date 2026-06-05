using Microsoft.CodeAnalysis;
using System.Linq;
using System.Text.Json;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    /// <summary>
    /// Single source of truth for converting a C# property into its database column name.
    /// Both <see cref="TableBindingsGenerator"/> (which emits the <c>{Prop}ColumnName</c> constants)
    /// and <see cref="PlaceholderResolver"/> (which expands <c>{{Type.Property}}</c> placeholders)
    /// resolve through here so a placeholder always matches the constant the table generator emits.
    /// </summary>
    internal static class ColumnNaming
    {
        /// <summary>
        /// Resolves the database column name for <paramref name="prop"/>:
        /// the first non-empty <c>[Column("name")]</c> constructor argument, otherwise the
        /// property name converted to <c>snake_case_lower</c>.
        /// </summary>
        public static string ResolveDbColumnName(IPropertySymbol prop, string columnAttributeFullName)
        {
            var columnAttribute = prop.GetAttributes()
                .FirstOrDefault(x => x.AttributeClass?.ToDisplayString() == columnAttributeFullName);

            if (columnAttribute != null &&
                columnAttribute.ConstructorArguments.Length > 0 &&
                columnAttribute.ConstructorArguments[0].Value is string overrideName &&
                !string.IsNullOrWhiteSpace(overrideName))
            {
                return overrideName;
            }

            return JsonNamingPolicy.SnakeCaseLower.ConvertName(prop.Name);
        }
    }
}
