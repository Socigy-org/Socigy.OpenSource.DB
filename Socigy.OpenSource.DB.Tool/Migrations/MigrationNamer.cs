using Socigy.OpenSource.DB.Tool.Structures.Analysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Socigy.OpenSource.DB.Tool.Migrations
{
    internal static class MigrationNamer
    {
        /// <summary>
        /// Generates a short, unique, and deterministic name based on a schema diff.
        /// </summary>
        /// <param name="diff">The DbSchemaDiff object representing the changes.</param>
        /// <returns>A unique name in the format: "DescriptiveName_Hash"</returns>
        public static string GenerateUniqueName(SchemaDiff diff)
        {
            if (diff.IsEmpty)
            {
                return "NoChanges";
            }

            // Step 1: Generate a deterministic, canonical string representation of the diff.
            string canonicalDiffString = GenerateCanonicalString(diff);

            // Step 2: Generate a short, descriptive prefix.
            string prefix = GeneratePrefix(diff);

            // Step 3: Hash the canonical string to create a unique identifier.
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonicalDiffString));
                string hash = ToHexString(hashBytes).Substring(0, 10); // Take the first 10 hex characters
                return $"{GetMigrationId()}_{prefix}_{hash}";
            }
        }


        /// <summary>
        /// Converts a byte array to a hexadecimal string.
        /// This method is compatible with netstandard2.0.
        /// </summary>
        private static string ToHexString(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.AppendFormat("{0:x2}", b);
            }
            return sb.ToString();
        }

        private static string GeneratePrefix(SchemaDiff diff)
        {
            if (diff.AddedTables?.Any() == true)
            {
                var addedTable = diff.AddedTables.First();
                return $"Add{addedTable.Name}";
            }
            if (diff.RemovedTables?.Any() == true)
            {
                var removedTable = diff.RemovedTables.First();
                return $"Remove{removedTable.Name}";
            }
            if (diff.AlteredTables?.Any() == true)
            {
                var alteredTable = diff.AlteredTables.First();
                return $"Alter{alteredTable.Table.Name}";
            }

            return "UpdateSchema";
        }

        public static string GetMigrationId()
        {
            var now = DateTime.UtcNow;
            return $"{now.Year}{now.Month}{now.Day}{now.Hour}{now.Minute}_";
        }

        /// <summary>
        /// Creates a deterministic string representation of the schema changes.
        /// The order of operations is crucial for a consistent hash.
        /// </summary>
        private static string GenerateCanonicalString(SchemaDiff diff)
        {
            var sb = new StringBuilder();

            // Sort tables by name to ensure consistent order
            foreach (var table in diff.AddedTables?.OrderBy(t => t.Name))
            {
                sb.AppendLine($"AddTable:{table.Name}");
            }

            if (diff.RemovedTables != null)
                foreach (var table in diff.RemovedTables?.OrderBy(t => t.Name))
                {
                    sb.AppendLine($"RemoveTable:{table.Name}");
                }

            if (diff.AlteredTables != null)
                foreach (var tableAlteration in diff.AlteredTables?.OrderBy(t => t.Table.Name))
                {
                    sb.AppendLine($"AlterTable:{tableAlteration.Table.Name}");

                    // Sort columns by name
                    if (tableAlteration.AddedColumns != null)
                        foreach (var col in tableAlteration.AddedColumns?.OrderBy(c => c.Name))
                        {
                            sb.AppendLine($"  AddColumn:{tableAlteration.Table.Name}.{col.Name}");
                        }

                    if (tableAlteration.RemovedColumns != null)
                        foreach (var col in tableAlteration.RemovedColumns?.OrderBy(c => c.Name))
                        {
                            sb.AppendLine($"  RemoveColumn:{tableAlteration.Table.Name}.{col.Name}");
                        }

                    // Sort altered columns by name
                    if (tableAlteration.ModifiedColumns != null)
                        foreach (var colAlt in tableAlteration.ModifiedColumns?.OrderBy(c => c.NewColumn.Name))
                        {
                            var changes = string.Join(",", colAlt.Changes.OrderBy(c => c));
                            sb.AppendLine($"  AlterColumn:{tableAlteration.Table.Name}.{colAlt.NewColumn.Name}:{changes}");
                        }

                    // Sort constraints by type and columns
                    if (tableAlteration.AddedConstraints != null)
                        foreach (var con in tableAlteration.AddedConstraints?.OrderBy(c => c.Type).ThenBy(c => string.Join(",", c.Columns.OrderBy(x => x))))
                        {
                            sb.AppendLine($"  AddConstraint:{tableAlteration.Table.Name}.{con.Type}");
                        }

                    if (tableAlteration.RemovedColumns != null)
                        foreach (var con in tableAlteration.RemovedConstraints?.OrderBy(c => c.Type).ThenBy(c => string.Join(",", c.Columns.OrderBy(x => x))))
                        {
                            sb.AppendLine($"  RemoveConstraint:{tableAlteration.Table.Name}.{con.Type}");
                        }
                }

            return sb.ToString();
        }
    }
}
