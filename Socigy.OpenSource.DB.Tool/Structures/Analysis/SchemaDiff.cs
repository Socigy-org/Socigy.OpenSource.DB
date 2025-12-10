using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Policy;
using System.Text.Json.Serialization;

namespace Socigy.OpenSource.DB.Tool.Structures.Analysis
{
    public class SchemaDiff
    {
        public List<DbTable> AddedTables { get; set; } = [];
        public List<DbTable> RemovedTables { get; set; } = [];
        public List<(DbTable Old, DbTable New)> RenamedTables { get; set; } = [];
        public List<TableAlteration> AlteredTables { get; set; } = [];

        [JsonIgnore]
        public bool IsEmpty => AddedTables.Count == 0 && RemovedTables.Count == 0
                            && RenamedTables.Count == 0 && AlteredTables.Count == 0;

        // TODO: ClearOutEmpty/ProvideDefaults should be handled better
        public void ClearOutEmpty()
        {
            if (AlteredTables.Count == 0)
                AlteredTables = null!;
            else
            {
                foreach (var altered in AlteredTables)
                    altered.ClearOutEmpty();
            }

            if (AddedTables.Count == 0)
                AddedTables = null!;

            if (RemovedTables.Count == 0)
                RemovedTables = null!;

            if (RenamedTables.Count == 0)
                RenamedTables = null!;
        }
        public void ProvideDefaults()
        {
            if (AlteredTables == null)
                AlteredTables = [];
            else
            {
                foreach (var altered in AlteredTables)
                    altered.ProvideDefaults();
            }

            AddedTables ??= [];
            RemovedTables ??= [];
            RenamedTables ??= [];
        }
    }

    public class TableAlteration
    {
        public DbTable Table { get; set; }

        public List<DbColumn> AddedColumns { get; set; } = [];
        public List<DbColumn> RemovedColumns { get; set; } = [];
        public List<ColumnAlteration> ModifiedColumns { get; set; } = [];
        public List<ColumnRename> RenamedColumns { get; set; } = [];

        public List<DbConstraint> AddedConstraints { get; set; } = [];
        public List<DbConstraint> RemovedConstraints { get; set; } = [];

        [JsonIgnore]
        public List<Dictionary<string, object?>> RawAddedRows { get; set; } = [];
        public IEnumerable<Dictionary<string, string?>>? AddedRows => RawAddedRows?.Select(y => y.ToDictionary(x => x.Key, x => x.Value?.ToString()));

        [JsonIgnore]
        public List<Dictionary<string, object?>> RawRemovedRows { get; set; } = [];
        public IEnumerable<Dictionary<string, string?>>? RemovedRows => RawRemovedRows?.Select(y => y.ToDictionary(x => x.Key, x => x.Value?.ToString()));

        public List<RowAlteration> ModifiedRows { get; set; } = [];

        public void ClearOutEmpty()
        {
            if (AddedColumns?.Count == 0) AddedColumns = null;
            if (RemovedColumns?.Count == 0) RemovedColumns = null;
            if (ModifiedColumns?.Count == 0) ModifiedColumns = null;
            if (RenamedColumns?.Count == 0) RenamedColumns = null;

            if (AddedConstraints?.Count == 0) AddedConstraints = null;
            if (RemovedConstraints?.Count == 0) RemovedConstraints = null;

            if (RawAddedRows?.Count == 0) RawAddedRows = null;
            if (RawRemovedRows?.Count == 0) RawRemovedRows = null;
            if (ModifiedRows?.Count == 0) ModifiedRows = null;
        }

        public void ProvideDefaults()
        {
            // Columns
            AddedColumns ??= [];
            RemovedColumns ??= [];
            ModifiedColumns ??= [];
            RenamedColumns ??= [];

            // Constraints
            AddedConstraints ??= [];
            RemovedConstraints ??= [];

            // Rows
            RawAddedRows ??= [];
            RawRemovedRows ??= [];
            ModifiedRows ??= [];
        }
    }

    public class ColumnAlteration
    {
        public DbColumn OldColumn { get; set; }
        public DbColumn NewColumn { get; set; }
        public List<string> Changes { get; set; } = [];
    }

    public class ColumnRename
    {
        public DbColumn New { get; set; }
        public DbColumn Old { get; set; }
    }

    public class RowAlteration
    {
        [JsonIgnore]
        public Dictionary<string, object?>? RawOldRow { get; set; }
        public Dictionary<string, string?>? OldRow => RawOldRow?.ToDictionary(x => x.Key, x => x.Value?.ToString());

        [JsonIgnore]
        public Dictionary<string, object?>? RawNewRow { get; set; }
        public Dictionary<string, string?>? NewRow => RawNewRow?.ToDictionary(x => x.Key, x => x.Value?.ToString());
        /// <summary>
        /// List of column names where values differ
        /// </summary>
        public List<string> ChangedColumns { get; set; } = [];
    }
}