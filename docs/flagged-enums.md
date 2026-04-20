# Flagged Enums

`[FlaggedEnum]` maps a bit-flag enum property to an automatically generated N:M junction table. Instead of storing a combined bitmask integer, each active flag gets its own row — making it easy to query, index, and join.

## Defining a flagged enum

The enum must use `[Flags]` with power-of-two values and carry `[Table]`:

```csharp
[Flags]
[Table("roles")]
public enum Role
{
    User      = 1,
    Admin     = 2,
    Developer = 4,
    Reviewer  = 8,
    SuperAdmin = 16,
}
```

Apply `[FlaggedEnum]` to the property on the owning table:

```csharp
[Table("users")]
public partial class User
{
    [PrimaryKey, Default(DbDefaults.Guid.Random)]
    public Guid Id { get; set; }

    public string Username { get; set; } = "";

    [FlaggedEnum]
    public Role Role { get; set; }
}
```

The generator creates a junction table named `{owner_table}_{enum_table}` (e.g. `users_roles`) with two FK columns and a composite PK. The `roles` reference table is seeded automatically from the enum values.

### Custom junction table name

```csharp
[FlaggedEnum(TableName = "user_role_assignments")]
public Role Role { get; set; }
```

### Custom junction column names

Pass alternating `(localPropertyName, junctionColumnName)` pairs:

```csharp
[FlaggedEnum(nameof(Id), "user_id")]
public Role Role { get; set; }
```

---

## Custom junction table with extra columns — `[FlaggedEnumTable]` + `[FlagTable]`

When the junction table needs extra columns beyond the two FK columns (e.g. `AssignedAt`, `AssignedBy`), define an explicit class instead:

### 1. Define the junction class

```csharp
[FlagTable("users_roles")]
public partial class UserRoleAssignment
{
    [PrimaryKey, ForeignKey(typeof(User), OnDelete = DbValues.ForeignKey.Cascade)]
    public Guid UserId { get; set; }

    [PrimaryKey, ForeignKey(typeof(Role))]
    public short RoleId { get; set; }

    [Default(DbDefaults.Time.Now)]
    public DateTime AssignedAt { get; set; }

    public string AssignedBy { get; set; } = "";
}
```

`[FlagTable("sql_name")]` marks the class as an explicit junction table. All normal column attributes apply.

### 2. Reference it from the owning property

```csharp
[FlaggedEnumTable(typeof(UserRoleAssignment))]
public Role Role { get; set; }
```

The migration tool generates the full table DDL from the class definition. The generated static/instance helpers work the same way as with `[FlaggedEnum]`.

### 3. CRUD methods on the junction class

Because `[FlagTable]` classes go through the same code generation pipeline as `[Table]` classes, `UserRoleAssignment` gets the full set of CRUD builders:

```csharp
// Insert a new assignment row
await UserRoleAssignment.Insert(new UserRoleAssignment
{
    UserId = user.Id,
    RoleId = (short)Role.Admin,
    AssignedBy = "system"
}).WithConnection(conn).ExecuteAsync();

// Query assignments
var assignments = await UserRoleAssignment.Query(x => x.UserId == user.Id)
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();

// Update an assignment
await UserRoleAssignment.Update(assignment)
    .WithAllFields()
    .WithConnection(conn)
    .ExecuteAsync();

// Delete an assignment
await UserRoleAssignment.Delete(x => x.UserId == user.Id && x.RoleId == (short)Role.Admin)
    .WithConnection(conn)
    .ExecuteAsync();
```

Column name constants are also generated, so you can reference columns safely:

```csharp
UserRoleAssignment.Columns.AssignedAt  // "assigned_at"
UserRoleAssignment.Columns.AssignedBy  // "assigned_by"
```

---

## Generated junction table DDL (auto mode)

```sql
CREATE TABLE IF NOT EXISTS "users_roles" (
    "users_id"  UUID    NOT NULL,
    "roles_id"  INTEGER NOT NULL,
    PRIMARY KEY ("users_id", "roles_id"),
    FOREIGN KEY ("users_id") REFERENCES "users"("id") ON DELETE CASCADE,
    FOREIGN KEY ("roles_id") REFERENCES "roles"("id")
);
```

---

## Static methods

```csharp
// Add a single flag
await User.InsertRoleAsync(user, Role.Admin, conn);

// Remove a single flag
await User.DeleteRoleAsync(user, Role.Admin, conn);

// Check whether a flag is present
bool isAdmin = await User.HasRoleFlagAsync(user, Role.Admin, conn);

// Get all active flags as a combined value
Role current = await User.GetRolesAsync(user, conn);

// Atomically sync: removes flags not in newValue, adds flags in newValue
await User.SyncRolesAsync(user, Role.User | Role.Admin, conn);
```

## Instance wrappers

```csharp
await user.InsertRoleAsync(Role.Admin, conn);
await user.DeleteRoleAsync(Role.Admin, conn);
bool has  = await user.HasRoleFlagAsync(Role.Admin, conn);
Role all  = await user.GetRolesAsync(conn);
```

## In-memory cache

Load all active flags once from the database, modify them locally, then commit in a single sync:

```csharp
await user.LoadRolesAsync(conn);

user.AddRoleFlag(Role.Developer);
user.RemoveRoleFlag(Role.User);

await user.CommitRolesAsync(conn);
```

If `CommitRolesAsync` is called before `LoadRolesAsync`, it does nothing.

## `EditRole` fluent builder

Combine add/remove in one call:

```csharp
await user.EditRole()
    .WithConnection(conn)
    .AddFlags(Role.Admin | Role.Developer)
    .RemoveFlags(Role.User)
    .ExecuteAsync();
```

## Querying by flag

```csharp
// All users who have the Admin flag
var admins = await User.Query(x => x.Role.HasFlag(Role.Admin))
    .WithConnection(conn)
    .ExecuteAsync()
    .ToListAsync();
```

This generates an `EXISTS` sub-query against the junction table.
