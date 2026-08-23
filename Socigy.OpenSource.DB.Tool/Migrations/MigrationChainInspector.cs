using Socigy.OpenSource.DB.Core.Migrations;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Socigy.OpenSource.DB.Tool.Migrations
{
#nullable enable
    /// <summary>
    /// Reads the migrations already on disk and checks that a new one can be added without forking the chain.
    ///
    /// A new migration takes its <c>PreviousId</c> from the schema snapshot, and the snapshot only advances
    /// after the file has been written. So two attempts at the same change — one abandoned, one kept — both
    /// claim the same parent, and the file name carries a fresh timestamp, so the tool cannot even overwrite
    /// its own prior emission. <see cref="MigrationHistory.OrderByChain"/> does detect the fork, but only at
    /// APPLY time, by which point it takes the whole module's schema offline and names neither file as the
    /// cause. The generator is the only place that knows a fork is being <i>created</i>; everywhere downstream
    /// can only report that one exists.
    /// </summary>
    internal static class MigrationChainInspector
    {
        // The T4 template emits both as literal constants, so a regex over the file recovers them without
        // compiling or loading anything.
        private static readonly Regex IdPattern =
            new Regex(@"public\s+const\s+string\s+_Id\s*=\s*""(?<id>[^""]*)""", RegexOptions.Compiled);

        private static readonly Regex PreviousIdPattern =
            new Regex(@"public\s+const\s+string\s+_PreviousId\s*=\s*""(?<id>[^""]*)""", RegexOptions.Compiled);

        internal sealed class ExistingMigration
        {
            public string FileName { get; set; } = "";
            public string Id { get; set; } = "";

            /// <summary>Null for the root migration, which declares <c>PreviousId =&gt; null</c> instead of a constant.</summary>
            public string? PreviousId { get; set; }
        }

        /// <summary>
        /// Reads every generated migration in <paramref name="migrationsFolder"/>. A file that does not
        /// declare an <c>_Id</c> is ignored rather than treated as an error: the folder is the developer's,
        /// and an unrelated file there is not this tool's business.
        /// </summary>
        public static List<ExistingMigration> Read(string migrationsFolder)
        {
            var result = new List<ExistingMigration>();
            if (!Directory.Exists(migrationsFolder)) return result;

            foreach (var path in Directory.GetFiles(migrationsFolder, "*.g.cs").OrderBy(p => p, StringComparer.Ordinal))
            {
                string text;
                try { text = File.ReadAllText(path); }
                catch (IOException) { continue; }

                var idMatch = IdPattern.Match(text);
                if (!idMatch.Success) continue;

                var previousMatch = PreviousIdPattern.Match(text);
                result.Add(new ExistingMigration
                {
                    FileName = Path.GetFileName(path),
                    Id = idMatch.Groups["id"].Value,
                    PreviousId = previousMatch.Success ? previousMatch.Groups["id"].Value : null,
                });
            }

            return result;
        }

        /// <summary>
        /// Returns the reasons a new migration whose parent is <paramref name="newParentId"/> must not be
        /// written, or an empty list when it is safe to write.
        /// </summary>
        /// <param name="existing">Migrations already on disk, from <see cref="Read"/>.</param>
        /// <param name="newParentId">
        /// The <c>PreviousId</c> the new migration would declare — i.e. the saved snapshot's id, or null when
        /// this would be the root migration.
        /// </param>
        public static List<string> DetectConflicts(IReadOnlyList<ExistingMigration> existing, string? newParentId)
        {
            var issues = new List<string>();
            if (existing == null || existing.Count == 0) return issues;

            // 1. Is the chain already forked, independently of what we are about to add? Report it here
            //    rather than letting it surface at apply time inside a shared test fixture.
            try
            {
                MigrationHistory.OrderByChain(existing.Select(m => (m.Id, m.PreviousId)));
            }
            catch (InvalidOperationException ex)
            {
                issues.Add(
                    $"The migrations already in this folder do not form a single chain: {ex.Message} " +
                    "Fix that before generating another migration — a new one would be appended to a chain " +
                    "that cannot be applied.");
            }

            // 2. Would the new migration create a fork? A sibling already claiming this parent is almost
            //    always an abandoned attempt at the same change: the snapshot was reverted (or never
            //    advanced) after the file was written.
            var siblings = existing
                .Where(m => string.Equals(m.PreviousId, newParentId, StringComparison.Ordinal))
                .ToList();

            if (siblings.Count > 0)
            {
                string parent = newParentId == null ? "the start of the chain" : $"'{newParentId}'";
                issues.Add(
                    $"A migration already follows {parent}: " +
                    $"{string.Join(", ", siblings.Select(s => s.FileName))}. Writing another would fork the " +
                    "chain, which fails at apply time and takes the whole module's schema with it. " +
                    "If that file was an abandoned attempt at this same change, delete it and re-run. " +
                    "If it was applied, the schema snapshot (Socigy/structure.json) has fallen behind it — " +
                    "restore the snapshot that file was generated against rather than regenerating.");
            }

            return issues;
        }
    }
#nullable disable
}
