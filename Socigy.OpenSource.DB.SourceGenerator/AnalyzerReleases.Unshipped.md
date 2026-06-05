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
