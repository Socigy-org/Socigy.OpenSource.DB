using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Socigy.OpenSource.DB.SourceGenerator.Templates;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;

namespace Socigy.OpenSource.DB.SourceGenerator
{
    public static class MigrationGenerator
    {
        public static void Execute(SourceProductionContext ctx, Compilation compilation)
        {
            // Migrations table, needed everytime
            ctx.AddSource("Migrations.table.g.cs", new MigrationTableTemplate() { BaseNamespace = compilation.AssemblyName }.TransformText());

            // TODO: Generate bidnings for Generated.Migrations table too...
        }
    }
}
