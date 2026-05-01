using Example.Auth.DB;
using Example.Auth.DB.Socigy.Generated;
using Socigy.OpenSource.DB.AuthDb.Extensions;
using Socigy.OpenSource.DB.Core;
using Socigy.OpenSource.DB.SharedDb.Extensions;
using Socigy.OpenSource.DB.UserDb.Extensions;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddLogging(logging => logging.AddConsole());

builder.WebHost.UseKestrelHttpsConfiguration();
builder.Configuration.AddJsonFile("appsettings.json");

builder.AddSharedDb();
builder.AddAuthDb();
builder.AddUserDb();

var app = builder.Build();

await app.EnsureLatestSharedDbMigration();
await app.EnsureLatestAuthDbMigration();
await app.EnsureLatestUserDbMigration();

var connectionFactory = app.Services.GetRequiredKeyedService<IDbConnectionFactory>("AuthDb");

// Seed: insert a UserLogin, a Course, and a UserCourse linking them
app.MapPost("/auth/seed", async () =>
{
    await using var conn = connectionFactory.Create();
    await conn.OpenAsync();

    var userId = Guid.NewGuid();
    var courseId = Guid.NewGuid();

    // Username "Alice" → stored as "alice" via LowerCaseConvertor
    var login = new UserLogin { Id = userId, Username = "Alice", PasswordHash = "demo-hash" };
    await login.Insert().WithConnection(conn).WithAllFields().ExecuteAsync();

    var course = new Course { Id = courseId, Name = "Intro to Socigy DB" };
    await course.Insert().WithConnection(conn).ExcludeAutoFields().ExecuteAsync();

    var enroll = new UserCourse { UserId = userId, CourseId = courseId, RegisteredAt = DateTime.UtcNow };
    await enroll.Insert().WithConnection(conn).WithAllFields().ExecuteAsync();

    return Results.Ok(new { userId, courseId });
});

// Procedure + Value Convertor: lookup is case-insensitive because usernames are stored lowercase
app.MapGet("/auth/login/{username}", async (string username) =>
{
    await using var conn = connectionFactory.Create();
    await conn.OpenAsync();

    UserLogin? found = null;
    await foreach (var row in Procedures.GetUserLoginByUsername(conn, username.ToLowerInvariant()))
        found = row;

    return found is null ? Results.NotFound() : Results.Ok(found);
});

// JOIN: courses enrolled by a given user
app.MapGet("/auth/users/{userId:guid}/courses", async (Guid userId) =>
{
    await using var conn = connectionFactory.Create();
    await conn.OpenAsync();

    var results = new List<object>();
    await foreach (var (uc, c) in UserCourse.Query()
        .Join<Course>((uc, c) => uc.CourseId == c.Id)
        .Where((uc, c) => uc.UserId == userId)
        .WithConnection(conn)
        .ExecuteAsync())
    {
        results.Add(new { c.Id, c.Name, uc.RegisteredAt });
    }

    return Results.Ok(results);
});

// Set Operation (UNION): all distinct courses
app.MapGet("/auth/courses", async () =>
{
    await using var conn = connectionFactory.Create();
    await conn.OpenAsync();

    var results = new List<Course>();
    await foreach (var c in Course.Query().Where(c => c.Name != null)
        .Union(Course.Query().Where(c => c.CreatedAt < DateTime.UtcNow))
        .WithConnection(conn)
        .ExecuteAsync())
    {
        results.Add(c);
    }

    return Results.Ok(results);
});

await app.RunAsync();
