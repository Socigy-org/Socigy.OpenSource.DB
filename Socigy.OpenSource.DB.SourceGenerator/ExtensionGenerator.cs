using Microsoft.CodeAnalysis;
using Socigy.OpenSource.DB.SourceGenerator.Templates;
using Socigy.OpenSource.DB.SourceGenerator.Templates.CommandBuilders;
using System;
using System.Collections.Generic;
using System.Text;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public static class ExtensionGenerator
    {
        public static void Execute(SourceProductionContext ctx, Compilation compilation, Program program)
        {
            // typeName = identifier base (clean even for a lowercase databaseName); serviceKey = raw databaseName
            // used as the DI keyed-service / connection-string lookup key.
            string typeName = program.DatabaseTypeName;
            string serviceKey = program.Settings?.Database?.DatabaseName ?? "UnnamedDb";

            // No configured provider for this project (e.g. API project without socigy.json)
            if (string.IsNullOrWhiteSpace(program.DatabasePrefix))
                return;

            bool includeConnectionFactory = program.Settings?.Database?.GenerateDbConnectionFactory ?? true;

            if (program.Settings?.Database?.GenerateWebAppExtensions ?? true)
            {
                ctx.AddSource($"{typeName}Extensions.g.cs", new ClassExtensionsTemplate()
                {
                    AssemblyBaseNamespace = compilation.AssemblyName,
                    BaseNamespace = $"Socigy.OpenSource.DB.{typeName}.Extensions",
                    DatabaseName = typeName,
                    ServiceKey = serviceKey,
                    DatabasePrefix = program.DatabasePrefix,
                    IncludeConnectionFactory = includeConnectionFactory
                }.TransformText());
            }

            if (includeConnectionFactory)
            {
                switch (program.DatabasePrefix)
                {
                    case DatabasePrefixes.Postgresql:
                        ctx.AddSource($"{program.DatabasePrefix}DbConnectionFactory.g.cs", new DbConnectionFactoryTemplate()
                        {
                            BaseNamespace = $"Socigy.OpenSource.DB.{typeName}.Factory",
                            ConnectionClassName = "NpgsqlConnection",
                            Usings = ["Npgsql"],
                            DatabasePrefix = program.DatabasePrefix,
                        }.TransformText());
                        break;
                }
            }

            switch (program.DatabasePrefix)
            {
                case DatabasePrefixes.Postgresql:
                    ctx.AddSource($"{program.DatabasePrefix}InsertCommandBuilder.g.cs", new PostgresqlInsertCommandBuilder().TransformText());
                    break;
            }
        }
    }
}
