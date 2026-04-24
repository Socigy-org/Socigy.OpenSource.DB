using Socigy.OpenSource.DB.Attributes;
using Socigy.OpenSource.DB.Tool.Generators;
using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;

// TODO: Make the code more readable, clearer

namespace Socigy.OpenSource.DB.Tool
{
    public static class AssemblyAnalyzer
    {
        private static DbSchema GeneratedSchema { get; set; }
        private static ISqlGenerator DbGenerator { get; set; }

        // Loaded in a collectible AssemblyLoadContext so that IDbCheckExpression.Build() can be invoked.
        private static AssemblyLoadContext? _checkContext;
        private static Assembly? _checkAssembly;

        // FK constraints whose TargetColumns could not be resolved yet (target table not processed yet).
        // Resolved in a second pass after all tables are processed.
        private static readonly List<(DbConstraint Foreign, string Location)> _pendingForeignKeys = [];

        /// <summary>
        /// Attempts to invoke <c>IDbCheckExpression.Build(columnName)</c> on a type loaded
        /// from the user's assembly using reflection (no cast — avoids type-identity issues).
        /// Returns <see langword="null"/> when the type is not found or not an IDbCheckExpression.
        /// </summary>
        private static string? InvokeCheckExpression(string typeFullName, string? columnName)
        {
            if (_checkAssembly == null) return null;
            try
            {
                var type = _checkAssembly.GetType(typeFullName);
                if (type == null) return null;

                var instance = Activator.CreateInstance(type);
                var method = type.GetMethod("Build", new[] { typeof(string) });
                if (method == null) return null;

                var result = method.Invoke(instance, new object?[] { columnName });
                if (result == null) return null;

                var sqlProp = result.GetType().GetProperty("Sql");
                return sqlProp?.GetValue(result) as string;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to invoke IDbCheckExpression '{typeFullName}': {ex.Message}");
                return null;
            }
        }

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

            if (!distinctPaths.Any(p => Path.GetFileName(p).Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase)))
                Logger.Error("CRITICAL WARNING: System.Private.CoreLib.dll was not found in search paths!");

            // Load assembly in a collectible execution context so IDbCheckExpression.Build() can be invoked.
            _checkContext = new AssemblyLoadContext("DbCheckBuilders", isCollectible: true);
            _checkContext.Resolving += (ctx, name) =>
            {
                var match = distinctPaths.FirstOrDefault(p =>
                    Path.GetFileNameWithoutExtension(p).Equals(name.Name, StringComparison.OrdinalIgnoreCase));
                return match != null ? ctx.LoadFromAssemblyPath(match) : null;
            };
            try { _checkAssembly = _checkContext.LoadFromAssemblyPath(assemblyPath.FullName); }
            catch (Exception ex) { Logger.Error($"Could not load assembly for check-expression execution: {ex.Message}"); }

            var resolver = new PathAssemblyResolver(paths);

            using var context = new MetadataLoadContext(resolver);
            var assembly = context.LoadFromAssemblyPath(assemblyPath.FullName);

            var tableAttributeFullName = typeof(TableAttribute).FullName;
            var flagTableAttributeFullName = typeof(FlagTableAttribute).FullName;

            var tables = assembly.GetTypes()
                .Where(t => (t.IsClass && !t.IsAbstract) || t.IsEnum)
                .Where(t => t.GetCustomAttributesData()
                             .Any(a => a.AttributeType.FullName == tableAttributeFullName
                                    || a.AttributeType.FullName == flagTableAttributeFullName))
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
            }

            // Second pass: resolve FK TargetColumns that were deferred because the target table
            // had not been processed yet when the referencing table was analyzed.
            foreach (var (foreign, location) in _pendingForeignKeys)
            {
                var targetTable = GeneratedSchema.Tables.FirstOrDefault(x => x.SourceName == foreign.TargetTable);
                if (targetTable == null)
                {
                    Logger.Error($"Please specify the target table column using 'nameof()' in [ForeignKey] attribute on {location} property");
                    Environment.Exit(-1);
                }

                var primaryKeys = targetTable.Columns.Where(x => x.IsPrimaryKey == true).ToList();
                if (primaryKeys.Count > 1)
                {
                    Logger.Error($"The target table has more than 1 primary key and thus we cannot find the target key matching only 1 primary key... At [ForeignKey] attribute on {location} property");
                    Environment.Exit(-1);
                }

                foreign.TargetColumns = [primaryKeys.First().SourceName.Split('.').Last()];

                if (foreign.TargetColumns.Count() != foreign.Columns.Count())
                {
                    Logger.Error($"'Keys x TargetKeys' count does not match in [ForeignKey] attribute on {location} property");
                    Environment.Exit(-1);
                }
            }
            _pendingForeignKeys.Clear();

            _checkAssembly = null;
            _checkContext?.Unload();
            _checkContext = null;

            return GeneratedSchema;
        }

        /// <summary>
        /// Resolves the SQL string for a <c>[Check]</c> attribute argument —
        /// either a raw SQL <see cref="string"/> or a <see cref="Type"/> reference
        /// to an <c>IDbCheckExpression</c> implementation.
        /// <paramref name="columnDbName"/> is passed to <c>Build()</c> for property-level checks;
        /// pass <see langword="null"/> for class-level checks.
        /// </summary>
        private static string? ResolveCheckSql(CustomAttributeData attribute, string? columnDbName)
        {
            if (attribute.ConstructorArguments.Count == 0) return null;

            var arg = attribute.ConstructorArguments[0];

            // [Check(string sql)]
            if (arg.ArgumentType.FullName == typeof(string).FullName)
                return arg.Value as string;

            // [Check(typeof(T))]
            if (arg.ArgumentType.FullName == typeof(Type).FullName || arg.Value is Type)
            {
                // MetadataLoadContext returns the type-handle as a string or as a Type
                var typeName = (arg.Value as Type)?.FullName ?? arg.Value?.ToString();
                if (typeName != null)
                    return InvokeCheckExpression(typeName, columnDbName);
            }

            return null;
        }

        private static readonly string TableAttributeFullName = typeof(TableAttribute).FullName!;
        private static readonly string FlagTableAttributeFullName = typeof(FlagTableAttribute).FullName!;
        private static readonly string RenamedAttributeFullName = typeof(RenamedAttribute).FullName!;
        private static readonly string ForeignKeyAttributeFullName = typeof(ForeignKeyAttribute).FullName!;
        private static readonly string CheckAttributeFullName = typeof(CheckAttribute).FullName!;
        private static readonly string IgnoreAttributeFullName = typeof(IgnoreAttribute).FullName!;
        private static readonly string PrimaryKeyAttributeFullName = typeof(PrimaryKeyAttribute).FullName!;
        private static readonly string UniqueAttributeFullName = typeof(UniqueAttribute).FullName!;
        private static readonly string ColumnAttributeFullName = typeof(ColumnAttribute).FullName!;
        private static readonly string DefaultAttributeFullName = typeof(DefaultAttribute).FullName!;
        private static readonly string AutoIncrementAttributeFullName = typeof(AutoIncrementAttribute).FullName!;
        private static readonly string StringLengthAttributeFullName = typeof(StringLengthAttribute).FullName!;
        private static readonly string MinAttributeFullName = typeof(MinAttribute).FullName!;
        private static readonly string MaxAttributeFullName = typeof(MaxAttribute).FullName!;
        private static readonly string LowerAttributeFullName = typeof(LowerAttribute).FullName!;
        private static readonly string BiggerAttributeFullName = typeof(BiggerAttribute).FullName!;
        private static readonly string EqualAttributeFullName = typeof(EqualAttribute).FullName!;
        private static readonly string LowerOrEqualAttributeFullName = typeof(LowerOrEqualAttribute).FullName!;
        private static readonly string BiggerOrEqualAttributeFullName = typeof(BiggerOrEqualAttribute).FullName!;
        private static readonly string FlaggedEnumAttributeFullName = typeof(FlaggedEnumAttribute).FullName!;
        private static readonly string FlaggedEnumTableAttributeFullName = typeof(FlaggedEnumTableAttribute).FullName!;
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
                    resultTable.Name = GetFirstAttributeArgumentValue(attribute)!;
                else if (attribute.AttributeType.FullName == RenamedAttributeFullName)
                    resultTable.RenamedFrom = (attribute.ConstructorArguments.First().Value as string)!;
                else if (attribute.AttributeType.FullName == FlagsAttributeFullName)
                    resultTable.IsBitfield = true;
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

            // Check for [FlagTable] (junction table class)
            var flagTableAttr = tableType.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == FlagTableAttributeFullName);
            if (flagTableAttr != null)
            {
                table.Name = GetFirstAttributeArgumentValue(flagTableAttr)!;
                table.IsFlagTable = true;
            }

            foreach (var attribute in tableType.CustomAttributes)
            {
                if (attribute.AttributeType.FullName == TableAttributeFullName)
                    table.Name = GetFirstAttributeArgumentValue(attribute)!;
                else if (attribute.AttributeType.FullName == RenamedAttributeFullName)
                    table.RenamedFrom = (attribute.ConstructorArguments.First().Value as string)!;
                else if (attribute.AttributeType.FullName == ForeignKeyAttributeFullName)
                {
                    var foreign = new DbConstraint()
                    {
                        TargetTable = GetFirstAttributeArgumentValue(attribute)!,
                        Type = DbConstraint.Types.ForeignKey
                    };

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
                            case nameof(ForeignKeyAttribute.OnDelete):
                                foreign.OnDelete = namedArg.TypedValue.Value as string;
                                break;
                            case nameof(ForeignKeyAttribute.OnUpdate):
                                foreign.OnUpdate = namedArg.TypedValue.Value as string;
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
                    var sql = ResolveCheckSql(attribute, null);
                    if (string.IsNullOrWhiteSpace(sql))
                    {
                        Logger.Error($"[Check] on class '{tableType.FullName}' produced an empty SQL expression. Skipping.");
                        continue;
                    }

                    string? name = null;
                    foreach (var namedArg in attribute.NamedArguments)
                        if (namedArg.MemberName == nameof(CheckAttribute.Name))
                            name = namedArg.TypedValue.Value as string;

                    table.Constraints ??= [];
                    table.Constraints.Add(new DbConstraint
                    {
                        Name = name,
                        Value = sql,
                        Type = DbConstraint.Types.Check,
                    });
                }
            }

            // Two-pass: first collect regular columns, then handle flagged-enum properties
            var flaggedEnumProperties = new List<(PropertyInfo Property, CustomAttributeData Attr, bool IsExplicit)>();

            foreach (var member in tableType.GetProperties())
            {
                try
                {
                    // Detect FlaggedEnum properties — exclude from columns, handle separately
                    var flaggedAttr = member.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == FlaggedEnumAttributeFullName);
                    var flaggedTableAttr = member.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == FlaggedEnumTableAttributeFullName);

                    if (flaggedAttr != null)
                    {
                        flaggedEnumProperties.Add((member, flaggedAttr, false));
                        continue;
                    }
                    if (flaggedTableAttr != null)
                    {
                        flaggedEnumProperties.Add((member, flaggedTableAttr, true));
                        continue;
                    }

                    var (column, constraints) = ProcessColumn(member, table.Name);

                    if (column == null)
                        continue;

                    table.Columns ??= [];
                    table.Columns.Add(column);
                    if (constraints.Any())
                    {
                        table.Constraints ??= [];
                        (table.Constraints as List<DbConstraint>).AddRange(constraints);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to process {table.SourceName}.{member.Name}: {ex}");
                }
            }

            // Now generate junction tables for flagged-enum properties
            foreach (var (property, attr, isExplicit) in flaggedEnumProperties)
            {
                try
                {
                    var junctionTable = isExplicit
                        ? ProcessExplicitFlaggedEnumTable(table, property, attr)
                        : ProcessAutoFlaggedEnumTable(table, property, attr);

                    if (junctionTable != null)
                        GeneratedSchema.Tables.Add(junctionTable);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Failed to generate junction table for {table.SourceName}.{property.Name}: {ex}");
                }
            }

            return table;
        }

        private static DbTable? ProcessAutoFlaggedEnumTable(DbTable mainTable, PropertyInfo property, CustomAttributeData flaggedAttr)
        {
            var enumType = property.PropertyType;
            EnsureEnumIsTable(enumType);

            var enumTableAttr = enumType.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == TableAttributeFullName);
            var enumTableName = GetFirstAttributeArgumentValue(enumTableAttr)!;

            // Custom table name from attribute or auto-derive
            string? customTableName = null;
            foreach (var namedArg in flaggedAttr.NamedArguments)
            {
                if (namedArg.MemberName == nameof(FlaggedEnumAttribute.TableName))
                    customTableName = namedArg.TypedValue.Value as string;
            }
            var junctionTableName = customTableName ?? $"{mainTable.Name}_{enumTableName}";

            // Key mappings from params string[] (alternating localPropName, junctionColName)
            var keyMappings = (flaggedAttr.ConstructorArguments.FirstOrDefault().Value
                as ReadOnlyCollection<CustomAttributeTypedArgument>)
                ?.Select(x => x.Value?.ToString())
                .ToList() ?? new List<string?>();

            // Build main-table PK → junction FK mappings
            var mainPks = mainTable.Columns?.Where(c => c.IsPrimaryKey == true).ToList() ?? [];
            var junctionColumns = new List<DbColumn>();
            var junctionConstraints = new List<DbConstraint>();

            for (int i = 0; i < mainPks.Count; i++)
            {
                var pk = mainPks[i];
                // Check if explicit mapping provided for this PK
                string? junctionColName = null;
                for (int k = 0; k + 1 < keyMappings.Count; k += 2)
                {
                    if (keyMappings[k] == pk.SourceName)
                    {
                        junctionColName = keyMappings[k + 1];
                        break;
                    }
                }
                junctionColName ??= $"{mainTable.Name}_{pk.Name}";

                junctionColumns.Add(new DbColumn
                {
                    Name = junctionColName,
                    SourceName = junctionColName,
                    DotnetType = pk.DotnetType,
                    DatabaseType = pk.DatabaseType,
                    IsPrimaryKey = true,
                    Nullable = false
                });
                junctionConstraints.Add(new DbConstraint
                {
                    Type = DbConstraint.Types.ForeignKey,
                    Columns = [junctionColName],
                    TargetTable = mainTable.SourceName,
                    TargetColumns = [pk.Name],
                    OnDelete = "CASCADE"
                });
            }

            // Enum FK column
            var enumPkType = FindEnumTableValueType(enumType);
            string enumJunctionColName = $"{enumTableName}_id";
            // Check if explicit mapping given for enum
            for (int k = 0; k + 1 < keyMappings.Count; k += 2)
            {
                if (keyMappings[k] == enumType.Name)
                {
                    enumJunctionColName = keyMappings[k + 1]!;
                    break;
                }
            }

            junctionColumns.Add(new DbColumn
            {
                Name = enumJunctionColName,
                SourceName = enumJunctionColName,
                DotnetType = enumPkType?.FullName ?? typeof(int).FullName!,
                DatabaseType = DbGenerator.GetDatabaseType(enumPkType?.FullName ?? typeof(int).FullName!),
                IsPrimaryKey = true,
                Nullable = false
            });
            junctionConstraints.Add(new DbConstraint
            {
                Type = DbConstraint.Types.ForeignKey,
                Columns = [enumJunctionColName],
                TargetTable = enumType.FullName!,
                TargetColumns = ["id"],
                OnDelete = "CASCADE"
            });

            return new DbTable
            {
                Name = junctionTableName,
                SourceName = junctionTableName,
                IsFlagTable = true,
                Columns = junctionColumns,
                Constraints = junctionConstraints
            };
        }

        private static DbTable? ProcessExplicitFlaggedEnumTable(DbTable mainTable, PropertyInfo property, CustomAttributeData flaggedTableAttr)
        {
            // The junction table class is user-defined with [FlagTable]; it will be picked up by LoadAndAnalyze
            // as a separate table via the FlagTableAttribute scan. Nothing to auto-generate here.
            return null;
        }

        private static readonly string NullableTypeFullName = typeof(Nullable).FullName!;
        private static bool IsNullable(PropertyInfo property)
        {
            NullabilityInfoContext nullabilityInfoContext = new NullabilityInfoContext();
            var info = nullabilityInfoContext.Create(property);
            if (info.WriteState == NullabilityState.Nullable || info.ReadState == NullabilityState.Nullable || property.PropertyType.FullName?.StartsWith(NullableTypeFullName) == true)
                return true;
            return false;
        }

        private static string? GetFirstAttributeArgumentValue(CustomAttributeData? attribute)
        {
            return attribute?.ConstructorArguments.FirstOrDefault().Value?.ToString();
        }

        private static string GetAttributeNumericOrStringValue(CustomAttributeData attribute)
        {
            var val = attribute.ConstructorArguments.FirstOrDefault().Value;
            if (val is double d) return d.ToString(CultureInfo.InvariantCulture);
            if (val is float f) return f.ToString(CultureInfo.InvariantCulture);
            return val?.ToString() ?? "";
        }

        private static void EnsureEnumIsTable(Type enumType, Type? parentType = null)
        {
            if (enumType.CustomAttributes.FirstOrDefault(x => x.AttributeType.FullName == TableAttributeFullName) != null)
                return;

            Logger.Error($"Enum {enumType.FullName} needs to be marked with [Table] attribute");
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

        private static (DbColumn, IEnumerable<DbConstraint>) ProcessColumn(PropertyInfo property, string? tableName = null)
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

            column.DatabaseType = DbGenerator.GetDatabaseType(column.DotnetType) ?? "INVALID";

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
                    column.IsPrimaryKey = true;
                // [Renamed]
                else if (attribute.AttributeType.FullName == RenamedAttributeFullName)
                    column.RenamedFrom = GetFirstAttributeArgumentValue(attribute);
                // [Default]
                else if (attribute.AttributeType.FullName == DefaultAttributeFullName)
                    column.DefaultValue = GetFirstAttributeArgumentValue(attribute);
                // [AutoIncrement]
                else if (attribute.AttributeType.FullName == AutoIncrementAttributeFullName)
                {
                    column.IsAutoIncrement = true;
                    var customName = GetFirstAttributeArgumentValue(attribute);
                    column.SequenceName = !string.IsNullOrEmpty(customName) ? customName : null;
                }
                // [StringLength]
                else if (attribute.AttributeType.FullName == StringLengthAttributeFullName)
                {
                    var args = attribute.ConstructorArguments;
                    if (args.Count == 1)
                    {
                        column.MaxLength = (int)args[0].Value!;
                    }
                    else if (args.Count == 2)
                    {
                        column.MinLength = (int)args[0].Value!;
                        column.MaxLength = (int)args[1].Value!;
                    }

                    // Override database type to VARCHAR(n)
                    if (column.MaxLength.HasValue)
                        column.DatabaseType = $"character varying({column.MaxLength})";

                    // Emit CHECK for minimum length
                    if (column.MinLength > 0)
                    {
                        constraints.Add(new DbConstraint
                        {
                            Type = DbConstraint.Types.Check,
                            Value = $"length(\"{column.Name}\") >= {column.MinLength}",
                            Columns = [property.Name]
                        });
                    }
                }
                // Comparison constraints
                else if (attribute.AttributeType.FullName == MinAttributeFullName)
                    constraints.Add(MakeCheckConstraint(column.Name, ">=", GetAttributeNumericOrStringValue(attribute), property.Name));
                else if (attribute.AttributeType.FullName == MaxAttributeFullName)
                    constraints.Add(MakeCheckConstraint(column.Name, "<=", GetAttributeNumericOrStringValue(attribute), property.Name));
                else if (attribute.AttributeType.FullName == LowerAttributeFullName)
                    constraints.Add(MakeCheckConstraint(column.Name, "<", GetAttributeNumericOrStringValue(attribute), property.Name));
                else if (attribute.AttributeType.FullName == BiggerAttributeFullName)
                    constraints.Add(MakeCheckConstraint(column.Name, ">", GetAttributeNumericOrStringValue(attribute), property.Name));
                else if (attribute.AttributeType.FullName == EqualAttributeFullName)
                    constraints.Add(MakeCheckConstraint(column.Name, "=", GetAttributeNumericOrStringValue(attribute), property.Name));
                else if (attribute.AttributeType.FullName == LowerOrEqualAttributeFullName)
                    constraints.Add(MakeCheckConstraint(column.Name, "<=", GetAttributeNumericOrStringValue(attribute), property.Name));
                else if (attribute.AttributeType.FullName == BiggerOrEqualAttributeFullName)
                    constraints.Add(MakeCheckConstraint(column.Name, ">=", GetAttributeNumericOrStringValue(attribute), property.Name));
                // [Unique]
                else if (attribute.AttributeType.FullName == UniqueAttributeFullName)
                {
                    string name = null;
                    foreach (var namedArg in attribute.NamedArguments)
                    {
                        if (namedArg.MemberName == nameof(UniqueAttribute.Name))
                            name = namedArg.TypedValue.Value as string;
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
                    var sql = ResolveCheckSql(attribute, column.Name);
                    if (string.IsNullOrWhiteSpace(sql))
                    {
                        Logger.Error($"[Check] on property '{property.DeclaringType?.FullName}.{property.Name}' produced an empty SQL expression. Skipping.");
                        continue;
                    }

                    string? name = null;
                    foreach (var namedArg in attribute.NamedArguments)
                        if (namedArg.MemberName == nameof(CheckAttribute.Name))
                            name = namedArg.TypedValue.Value as string;

                    constraints.Add(new DbConstraint
                    {
                        Name = name,
                        Value = sql,
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
                            case nameof(ForeignKeyAttribute.OnDelete):
                                foreign.OnDelete = namedArg.TypedValue.Value as string;
                                break;
                            case nameof(ForeignKeyAttribute.OnUpdate):
                                foreign.OnUpdate = namedArg.TypedValue.Value as string;
                                break;
                        }
                    }

                    if (foreign.TargetColumns == null)
                    {
                        var targetTable = GeneratedSchema.Tables.FirstOrDefault(x => x.SourceName == foreign.TargetTable);
                        if (targetTable == null)
                        {
                            // Target table not yet processed — defer resolution to a second pass.
                            _pendingForeignKeys.Add((foreign, $"{property.DeclaringType!.FullName}.{property.Name}"));
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

                    if (foreign.TargetColumns != null && foreign.TargetColumns.Count() != foreign.Columns.Count())
                    {
                        Logger.Error($"'Keys x TargetKeys' count does not match in [ForeignKey] attribute on {property.DeclaringType!.FullName}.{property.Name} property");
                        Environment.Exit(-1);
                    }

                    constraints.Add(foreign);
                }
            }

            // If no [Default] attribute was found, check for a C# property initializer (e.g. = "DEFAULT NAME").
            // We instantiate the class via the execution-context assembly and read the value.
            if (column.DefaultValue == null && column.IsAutoIncrement != true)
                column.DefaultValue = ReadInitializerDefault(property);

            return (column, constraints);
        }

        private static string? ReadInitializerDefault(PropertyInfo metadataProperty)
        {
            if (_checkAssembly == null) return null;
            try
            {
                var runtimeType = _checkAssembly.GetType(metadataProperty.DeclaringType!.FullName!);
                if (runtimeType == null) return null;

                var instance = Activator.CreateInstance(runtimeType);
                if (instance == null) return null;

                var runtimeProp = runtimeType.GetProperty(metadataProperty.Name);
                if (runtimeProp == null || !runtimeProp.CanRead) return null;

                var value = runtimeProp.GetValue(instance);

                // Treat "type zero" as no initializer (null for reference types, 0/false/Guid.Empty etc. for value types)
                var propType = runtimeProp.PropertyType;
                var typeDefault = propType.IsValueType ? Activator.CreateInstance(propType) : null;
                if (Equals(value, typeDefault)) return null;

                return FormatInitializerAsSql(value);
            }
            catch
            {
                return null;
            }
        }

        private static string? FormatInitializerAsSql(object? value)
        {
            if (value == null) return null;
            return value switch
            {
                string s  => $"'{s.Replace("'", "''")}'",
                bool b    => b ? "TRUE" : "FALSE",
                Guid g    => $"'{g}'",
                DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
                _         => Convert.ToString(value, CultureInfo.InvariantCulture)
            };
        }

        private static DbConstraint MakeCheckConstraint(string colName, string op, string value, string propertyName)
        {
            // Auto-quote non-numeric values (e.g. date strings)
            string sqlValue = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _)
                ? value
                : $"'{value.Replace("'", "''")}'";

            return new DbConstraint
            {
                Type = DbConstraint.Types.Check,
                Value = $"\"{colName}\" {op} {sqlValue}",
                Columns = [propertyName]
            };
        }
    }
}
