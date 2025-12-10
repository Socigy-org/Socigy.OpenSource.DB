using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

// TODO: Make the code more readable, clearer

namespace Socigy.OpenSource.DB.Tool
{
    public static class AssemblyAnalyzer
    {
        private static DbSchema GeneratedSchema { get; set; }
        private static ISqlGenerator DbGenerator { get; set; }

        public static DbSchema LoadAndAnalyze(FileInfo assemblyPath)
        {
            Logger.Log($"Scanning '{Path.GetFileNameWithoutExtension(assemblyPath.Name)}' project for DB classes...");

            var paths = new List<string>();
            paths.AddRange(Directory.GetFiles(assemblyPath.DirectoryName!, "*.dll"));
            paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
            paths.AddRange(Directory.GetFiles(AppContext.BaseDirectory, "*.dll"));

            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrEmpty(trustedAssemblies))
                paths.AddRange(trustedAssemblies.Split(Path.PathSeparator));
            else
                paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));

            var distinctPaths = paths.Distinct().ToList();

            // --- DEBUG CHECK --- 
            if (!distinctPaths.Any(p => Path.GetFileName(p).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase)))
            {
                Logger.Error("CRITICAL WARNING: System.Private.CoreLib.dll was not found in search paths!");
            }

            var resolver = new PathAssemblyResolver(paths);

            using var context = new MetadataLoadContext(resolver);
            var assembly = context.LoadFromAssemblyPath(assemblyPath.FullName);

            var tableAttributeFullName = typeof(TableAttribute).FullName;
            var tables = assembly.GetTypes()
                .Where(t => (t.IsClass && !t.IsAbstract) || t.IsEnum)
                .Where(t => t.GetCustomAttributesData()
                             .Any(a => a.AttributeType.FullName == tableAttributeFullName))
                .OrderByDescending(x => x.IsEnum ? 1 : -1)
                .ToList();

            Logger.Log($"Found {tables.Count} units for processing...");

            Configuration.BaseNamespace = assembly.GetName().Name!;

            GeneratedSchema = new()
            {
                PreviousId = Configuration.SavedSchema?.Id
            };

            DbGenerator = Configuration.GetSqlGenerator() ?? throw new InvalidDataException("Failed to get target DB platform");

            foreach (var table in tables)
            {
                try
                {
                    DbTable resTable;
                    if (table.IsEnum)
                        resTable = ProcessEnumTable(table)!;
                    else
                        resTable = ProcessTable(table)!;

                    if (resTable == null)
                        continue;

                    GeneratedSchema.Tables.Add(resTable);
                }
                catch (TypeLoadException ex)
                {
                    Logger.Error($"Failed to properly load type from the project assembly! ... {ex}");
                }
                catch (InvalidDataException ex)
                {
                    Logger.Error(ex.Message);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Unexpected error: {ex}");
                }
                //Logger.Log($"Done processing {table}");
            }

            return GeneratedSchema;
        }

        private static readonly string TableAttributeFullName = typeof(TableAttribute).FullName!;
        private static readonly string RenamedAttributeFullName = typeof(RenamedAttribute).FullName!;
        private static readonly string ForeignKeyAttributeFullName = typeof(ForeignKeyAttribute).FullName!;
        private static readonly string CheckAttributeFullName = typeof(CheckAttribute).FullName!;
        private static readonly string IgnoreAttributeFullName = typeof(IgnoreAttribute).FullName!;
        private static readonly string PrimaryKeyAttributeFullName = typeof(PrimaryKeyAttribute).FullName!;
        private static readonly string UniqueAttributeFullName = typeof(UniqueAttribute).FullName!;
        private static readonly string ColumnAttributeFullName = typeof(ColumnAttribute).FullName!;
        private static readonly string DefaultAttributeFullName = typeof(DefaultAttribute).FullName!;

        private static readonly string FlagsAttributeFullName = typeof(FlagsAttribute).FullName!;
        private static readonly string DescriptionAttributeFullName = typeof(DescriptionAttribute).FullName!;
        private static DbTable? ProcessEnumTable(Type enumTableType)
        {
            var resultTable = new DbTable()
            {
                SourceName = enumTableType.FullName!,
                IsEnum = true,
            };

            foreach (var attribute in enumTableType.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == TableAttributeFullName)
                {
                    resultTable.Name = GetFirstAttributeArgumentValue(attribute)!;
                }
                else if (attribute.AttributeType.FullName == RenamedAttributeFullName)
                {
                    resultTable.RenamedFrom = (attribute.ConstructorArguments.First().Value as string)!;
                }
                else if (attribute.AttributeType.FullName == FlagsAttributeFullName)
                {
                    resultTable.IsBitfield = true;
                }
            }

            resultTable.InstantiatedValues = [];
            foreach (var field in enumTableType.GetFields())
            {
                if (field.FieldType == enumTableType)
                {
                    var descriptionAttribute = field.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == DescriptionAttributeFullName);
                    resultTable.InstantiatedValues.Add(new Dictionary<string, object?>()
                    {
                        { "id", field.GetRawConstantValue()! },
                        { "value", field.Name },
                        { "description", GetFirstAttributeArgumentValue(descriptionAttribute) }
                    });
                }
                else
                {
                    string stringFullName = typeof(string).FullName!;
                    string dbStringType = DbGenerator.GetDatabaseType(stringFullName);

                    resultTable.Columns = [
                        new DbColumn()
                        {
                            Name = "id",
                            SourceName = "Id",
                            DotnetType = field.FieldType.FullName!,
                            DatabaseType = DbGenerator.GetDatabaseType(field.FieldType.FullName!),
                            IsPrimaryKey = true,
                            ValueConvertor = $"EnumConvertor<{resultTable.SourceName}>",
                        },
                        new DbColumn()
                        {
                            Name = "value",
                            DotnetType = stringFullName,
                            DatabaseType = dbStringType,
                        },
                        new DbColumn()
                        {
                            Name = "description",
                            DotnetType = stringFullName,
                            DatabaseType = dbStringType,
                        }
                    ];
                }
            }

            return resultTable;
        }

        private static DbTable? ProcessTable(Type tableType)
        {
            DbTable table = new()
            {
                SourceName = tableType.FullName!,
            };

            foreach (var attribute in tableType.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == TableAttributeFullName)
                {
                    table.Name = GetFirstAttributeArgumentValue(attribute)!;
                }
                else if (attribute.AttributeType.FullName == RenamedAttributeFullName)
                {
                    table.RenamedFrom = (attribute.ConstructorArguments.First().Value as string)!;
                }
                else if (attribute.AttributeType.FullName == ForeignKeyAttributeFullName)
                {
                    var foreign = new DbConstraint()
                    {
                        TargetTable = GetFirstAttributeArgumentValue(attribute)!,
                        Type = DbConstraint.Types.ForeignKey
                    };

                    // TODO: Add column checks, if they really exists on the target table...

                    foreach (var namedArg in attribute.NamedArguments)
                    {
                        switch (namedArg.MemberName)
                        {
                            case nameof(ForeignKeyAttribute.Keys):
                                foreign.Columns = (namedArg.TypedValue.Value as ReadOnlyCollection<CustomAttributeTypedArgument>).Select(x => x.Value as string);
                                break;

                            case nameof(ForeignKeyAttribute.TargetKeys):
                                foreign.TargetColumns = (namedArg.TypedValue.Value as ReadOnlyCollection<CustomAttributeTypedArgument>).Select(x => x.Value as string);
                                break;

                            case nameof(ForeignKeyAttribute.Name):
                                foreign.Name = namedArg.TypedValue.Value as string;
                                break;
                        }
                    }

                    if (foreign.TargetColumns == null)
                    {
                        Logger.Error($"Missing 'TargetKeys' parameter in [ForeignKey] attribute on {table.SourceName} class");
                        Environment.Exit(-1);
                    }
                    else if (foreign.TargetColumns.Count() != foreign.Columns.Count())
                    {
                        Logger.Error($"'Keys x TargetKeys' count does not match in [ForeignKey] attribute on {table.SourceName} class");
                        Environment.Exit(-1);
                    }

                    table.Constraints ??= [];
                    table.Constraints.Add(foreign);
                }
                else if (attribute.AttributeType.FullName == CheckAttributeFullName)
                {
                    // FIXME: [Check] is broken on class level... prob. create something to be able to define the checks normally without this...
                    continue;

                    string name = null!;
                    foreach (var namedArg in attribute.NamedArguments)
                    {
                        switch (namedArg.MemberName)
                        {
                            case nameof(CheckAttribute.Name):
                                name = namedArg.TypedValue.Value as string;
                                break;
                        }
                    }

                    table.Constraints ??= [];
                    table.Constraints.Add(new DbConstraint()
                    {
                        Name = name!,
                        Value = GetFirstAttributeArgumentValue(attribute)!,
                        Type = DbConstraint.Types.Check,
                    });
                }
            }

            foreach (var member in tableType.GetProperties())
            {
                try
                {
                    var (column, constraints) = ProcessColumn(member);

                    if (column == null)
                        continue;

                    table.Columns ??= [];
                    table.Columns.Add(column);
                    if (constraints.Any())
                    {
                        table.Constraints ??= [];
                        (table.Constraints as List<DbConstraint>).AddRange(constraints); // FIXME: Maybe... :shrugging_man:
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to process {table.SourceName}.{member.Name}: {ex}");
                }
            }

            return table;
        }

        private static readonly string NullableTypeFullName = typeof(Nullable).FullName!;
        private static bool IsNullable(PropertyInfo property)
        {
            NullabilityInfoContext nullabilityInfoContext = new NullabilityInfoContext();
            var info = nullabilityInfoContext.Create(property);
            if (info.WriteState == NullabilityState.Nullable || info.ReadState == NullabilityState.Nullable || property.PropertyType.FullName?.StartsWith(NullableTypeFullName) == true)
            {
                return true;
            }

            return false;
        }

        private static string? GetFirstAttributeArgumentValue(CustomAttributeData? attribute)
        {
            return attribute?.ConstructorArguments.FirstOrDefault().Value?.ToString();
        }

        private static void EnsureEnumIsTable(Type enumType, Type? parentType = null)
        {
            if (enumType.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == TableAttributeFullName) != null)
                return;

            Logger.Error($"Enum {enumType.FullName} needs to be be marked with [Table] attribute");
            Environment.Exit(-1);
        }
        private static Type? FindEnumTableValueType(Type enumType, Type? parentType = null)
        {
            EnsureEnumIsTable(enumType, parentType);

            foreach (var field in enumType.GetFields())
            {
                if (field.FieldType != enumType)
                    return field.FieldType;
            }

            return null;
        }

        private static (DbColumn, IEnumerable<DbConstraint>) ProcessColumn(PropertyInfo property)
        {
            // [Ignore] attribute
            if (property.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == IgnoreAttributeFullName) != null)
                return default;

            var underlayingType = Nullable.GetUnderlyingType(property.PropertyType);
            var constraints = new List<DbConstraint>();
            var column = new DbColumn()
            {
                Name = JsonNamingPolicy.SnakeCaseLower.ConvertName(property.Name),
                SourceName = property.Name,

                Nullable = IsNullable(property) ? true : null
            };

            // Nullable<System.DateTime>
            bool isEnum = false;
            if (column.Nullable == true && property.PropertyType.FullName!.StartsWith(NullableTypeFullName))
            {
                var realType = property.PropertyType.GenericTypeArguments.First();
                isEnum = realType.IsEnum;
                column.DotnetType = realType.FullName!;
            }
            else
            {
                var realType = (underlayingType ?? property.PropertyType);
                isEnum = realType.IsEnum;
                column.DotnetType = realType.FullName!;
            }

            if (isEnum)
            {
                constraints.Add(new DbConstraint()
                {
                    Type = DbConstraint.Types.ForeignKey,
                    TargetTable = column.DotnetType,

                    Columns = [property.Name],
                    TargetColumns = ["Id"],
                });

                column.DotnetType = FindEnumTableValueType(property.PropertyType, property.DeclaringType!)?.FullName!;
            }

            column.DatabaseType = DbGenerator.GetDatabaseType(column.DotnetType) ?? "INVALID"; // FIXME: There's probably a better way to handle this...

            foreach (var attribute in property.CustomAttributes)
            {
                // [Column]
                if (attribute.AttributeType.FullName == ColumnAttributeFullName)
                {
                    var nameOverride = GetFirstAttributeArgumentValue(attribute);
                    if (!string.IsNullOrEmpty(nameOverride))
                        column.Name = nameOverride!;

                    foreach (var namedArg in attribute.NamedArguments)
                    {
                        switch (namedArg.MemberName)
                        {
                            case nameof(ColumnAttribute.Type):
                                column.DatabaseType = namedArg.TypedValue.Value!.ToString();
                                break;

                            case nameof(ColumnAttribute.ValueConvertor):
                                column.ValueConvertor = namedArg.TypedValue.Value!.ToString();
                                break;
                        }
                    }
                }
                // [PrimaryKey]
                else if (attribute.AttributeType.FullName == PrimaryKeyAttributeFullName)
                {
                    column.IsPrimaryKey = true;
                }
                // [Renamed]
                else if (attribute.AttributeType.FullName == RenamedAttributeFullName)
                {
                    column.RenamedFrom = GetFirstAttributeArgumentValue(attribute);
                }
                // [Default]
                else if (attribute.AttributeType.FullName == DefaultAttributeFullName)
                {
                    // TODO: Make an API for this to be able to have it cross-platform "timezone('utc', now())" for example..
                    column.DefaultValue = GetFirstAttributeArgumentValue(attribute);
                }
                // [Unique]
                else if (attribute.AttributeType.FullName == UniqueAttributeFullName)
                {
                    string name = null;
                    foreach (var namedArg in attribute.NamedArguments)
                    {
                        switch (namedArg.MemberName)
                        {
                            case nameof(UniqueAttribute.Name):
                                name = namedArg.TypedValue.Value as string;
                                break;
                        }
                    }

                    constraints.Add(new DbConstraint()
                    {
                        Name = name,
                        Columns = [property.Name],
                        Type = DbConstraint.Types.Unique
                    });
                }
                // [Check]
                else if (attribute.AttributeType.FullName == CheckAttributeFullName)
                {
                    string name = null;
                    foreach (var namedArg in attribute.NamedArguments)
                    {
                        switch (namedArg.MemberName)
                        {
                            case nameof(CheckAttribute.Name):
                                name = namedArg.TypedValue.Value as string;
                                break;
                        }
                    }

                    constraints.Add(new DbConstraint()
                    {
                        Name = name,
                        Value = GetFirstAttributeArgumentValue(attribute),

                        Columns = [property.Name],
                        Type = DbConstraint.Types.Check,
                    });
                }
                // [ForeignKey]
                else if (attribute.AttributeType.FullName == ForeignKeyAttributeFullName)
                {
                    var foreign = new DbConstraint()
                    {
                        TargetTable = GetFirstAttributeArgumentValue(attribute),
                        Columns = [property.Name],
                        Type = DbConstraint.Types.ForeignKey
                    };

                    // TODO: Add column checks, if they really exists on the target table...
                    foreach (var namedArg in attribute.NamedArguments)
                    {
                        switch (namedArg.MemberName)
                        {
                            case nameof(ForeignKeyAttribute.TargetKeys):
                                foreign.TargetColumns = (namedArg.TypedValue.Value as IEnumerable<string>);
                                break;

                            case nameof(ForeignKeyAttribute.Name):
                                foreign.Name = namedArg.TypedValue.Value as string;
                                break;
                        }
                    }

                    if (foreign.TargetColumns == null)
                    {
                        var targetTable = GeneratedSchema.Tables.FirstOrDefault(x => x.SourceName == foreign.TargetTable);
                        if (targetTable == null)
                        {
                            Logger.Error($"Please specify the target table column using 'nameof()' in [ForeignKey] attribute on {property.DeclaringType!.FullName}.{property.Name} property");
                            Environment.Exit(-1);
                        }
                        else
                        {
                            var primaryKeys = targetTable.Columns.Where(x => x.IsPrimaryKey == true);
                            if (primaryKeys.Count() > 1)
                            {
                                Logger.Error($"The target table has more than 1 primary key and thus we cannot find the target key matching only 1 primary key... At [ForeignKey] attribute on {property.DeclaringType!.FullName}.{property.Name} property");
                                Environment.Exit(-1);
                            }
                            else
                                foreign.TargetColumns = [primaryKeys.First().SourceName.Split('.').Last()];
                        }
                    }

                    if (foreign.TargetColumns.Count() != foreign.Columns.Count())
                    {
                        Logger.Error($"'Keys x TargetKeys' count does not match in [ForeignKey] attribute on {property.DeclaringType!.FullName}.{property.Name} property");
                        Environment.Exit(-1);
                    }

                    constraints.Add(foreign);
                }
            }

            return (column, constraints);
        }
    }
}
