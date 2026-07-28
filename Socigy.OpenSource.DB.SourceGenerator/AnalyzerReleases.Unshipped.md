; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SCGDB001 | Socigy.DB | Error | [AutoIncrement] on unsupported type
SCGDB002 | Socigy.DB | Error | [Encrypted] cannot be combined with [ValueConvertor]/[JsonColumn]
SCGDB003 | Socigy.DB | Warning | SQL procedure has no schema placeholder
SCGDB004 | Socigy.DB | Error | Unknown type in SQL placeholder
SCGDB005 | Socigy.DB | Error | Unknown property in SQL placeholder
SCGDB006 | Socigy.DB | Error | Malformed SQL placeholder
SCGDB007 | Socigy.DB | Error | SQL placeholder type is not a [Table]
SCGDB008 | Socigy.DB | Error | Ambiguous type in SQL placeholder
SCGDB009 | Socigy.DB | Warning | SQL parameter declared but unused
SCGDB010 | Socigy.DB | Warning | SQL parameter used without declaration
SCGDB011 | Socigy.DB | Warning | @returns type not resolvable
SCGDB012 | Socigy.DB | Warning | Malformed @param line
SCGDB013 | Socigy.DB | Warning | .sql file outside Procedures folder ignored
SCGDB014 | Socigy.DB | Warning | Empty SQL procedure body
SCGDB015 | Socigy.DB | Error | Duplicate generated procedure
SCGDB016 | Socigy.DB | Warning | [Table] class has no primary key
SCGDB017 | Socigy.DB | Warning | [Table] class has no columns
SCGDB018 | Socigy.DB | Error | [Column] name is empty
SCGDB019 | Socigy.DB | Error | @returns scalar type is not a supported scalar
SCGDB020 | Socigy.DB | Warning | Conflicting @returns directives
SCGDB021 | Socigy.DB | Error | @returns DTO type cannot be mapped
SCGDB022 | Socigy.DB | Error | Malformed @returns directive
SCGDB023 | Socigy.DB | Error | [Encrypted] cannot be applied to a key column
SCGDB024 | Socigy.DB | Error | Duplicate column name
SCGDB025 | Socigy.DB | Error | [Table] type must be a top-level, non-generic class
SCGDB026 | Socigy.DB | Error | [Index] references an unknown property
