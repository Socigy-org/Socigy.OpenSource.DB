using Example.Auth.DB;
using Example.Auth.DB.Socigy.Generated;
using Npgsql;
using NpgsqlTypes;
using Socigy.OpenSource.DB.AuthDb.Extensions;
using Socigy.OpenSource.DB.CommandBuilders.Postgresql;
using Socigy.OpenSource.DB.Core;
using Socigy.OpenSource.DB.Core.CommandBuilders;
using Socigy.OpenSource.DB.Core.Delegates;
using Socigy.OpenSource.DB.Core.Interfaces;
using Socigy.OpenSource.DB.Core.Parsers;
using Socigy.OpenSource.DB.Core.Parsers.Postgresql;
using Socigy.OpenSource.DB.Migrations;
using Socigy.OpenSource.DB.SharedDb.Extensions;
using Socigy.OpenSource.DB.UserDb.Extensions;
using System.Data.Common;
using System.Linq.Expressions;
using System.Text;
using static Socigy.OpenSource.DB.Core.SyntaxHelper.DB;

async Task TestAsync(DbConnection connection)
{
    string username = "wailed";
    // TODO: SQL Procedures mapping with conversions out
    // CREATE PROCEDURE GetUsersByEmail(IN email_param VARCHAR(255))

    // TODO: JOINs in SQL -> LATER LATER LATER
    //users = User.JoinCourses((x, y) => x.Email == y.TeacherEmail, JoinType.None)
    //    .Where()
    //    .WithConnection(connection)
    //    .ExecuteAsync();

    //await user.Update()
    //    .WithConnection(connection)
    //    .Set(x => x.EmailVerified, true)
    //    .Where(x => x.Email == "")
    //    .ExecuteAsync();

    //await user.Update()
    //    .WithConnection(connection)
    //    .WithAllFields()
    //    .WithFields(x => new object?[] { x.Usernameme })
    //    .ExceptFields(x => new object?[] { x.ID, x.Email })
    //    .Where()
    //    .ExecuteAsync();

    //User.Delete()
    //    .Where(x => x.Email.Contains("@gmail.com") || x.Usernameme == username)
    //    .ExecuteAsync();

    await User.DeleteNonInstance()
        .WithConnection(connection)
        .ExecuteAsync();

    var buser = new User()
    {
        ID = Guid.NewGuid(),
        IsChild = true,
        Email = "patrik.stohanzl@gmail.com",
        Username = "Patrik Stohanzl",
        EmailVerified = true,
    };

    await buser.Insert()
        .WithConnection(connection)
        .ExecuteAsync();

    buser.Email = "stohanzlp@gmail.com";
    buser.Username = username;
    buser.Visibility = UserVisibility.CirclesOnly;

    await buser.Update()
        .WithConnection(connection)
        //.WithAllFields()
        .WithFields(x => new object?[] { x.Email, x.Username, x.Visibility })
        //.ExceptFields(x => new object?[] { x.Email, x.Username })
        .ExecuteAsync();

    //User.Delete();
    //await buser.Delete()
    //    .ExecuteAsync();

    var users = User.Query()
        .WithConnection(connection)
        .Limit(25)
        .Offset(25)
        .ExecuteAsync();

    // WHERE birth_date < @p0 AND ((SELECT 1 FROM ... custom sql) = 11 OR email IS NOT NULL)
    users = User.Query(x => x.BirthDate < DateTime.Today.AddYears(-18) && (Query.Custom("(SELECT 1 FROM ... custom sql) = 11") || x.Email != null))
        .Top(100)
        .WithConnection(connection)
        .ExecuteAsync();

    users = User.Query(x => x.ParentId == Guid.NewGuid() || x.IsChild && x.Visibility == UserVisibility.Public)
        // SELECT id,email,username ....
        .Select(x => new object?[] { x.ID, x.Email, x.Username })

        // SELECT id, emailVerified, username ...
        .Select(x => new object?[] { x.ID, username == "wailed" ? x.Email : x.EmailVerified, x.Username })

        // SELECT email AS "emailer", username = 'this_value' ... 
        .Select(x => new object?[] { Select.Custom($"{User.EmailColumnName} AS \"emailer\", {User.UsernameColumnName} = 'this_value'"), Select.All() })

        // SELECT id, CASE WHEN email = @p0 OR username LIKE ('%Example%') OR username LIKE ('Example%') OR username LIKE ('%Example') THEN true ELSE false END AS "is_example", username ...
        // p0 = 'example@example.com'
        // p1 = 'Example'
        .Select(x => new object?[] {
                    x.ID,
                    Select.Case()
                        .When(x.Email == "exampl@example.com" ||
                              x.Username.Contains("Example") /* LIKE ("%Example%") */ ||
                              x.Username.StartsWith("Example") /* LIKE ("Example%") */ ||
                              x.Username.EndsWith("Example") /* LIKE ("%Example") */)
                        .Then(true)
                        .Else(false)
                        .As("is_example"),
                    x.Username
        })
        .Select(x => new object?[]
        {
            Select.Case()
                .When(x.IsChild == true && x.IconUrl != null && x.IconUrl.StartsWith("https://images.socigy.com"))
                .Then("test")
                .Else(x.Email)
                .As(User.EmailColumnName)
        })

        // @pemail = 'example@example.com'
        // ORDER BY creation_date, birth_date DESC, CASE WHEN email == @pemail THEN username DESC ELSE email
        .OrderBy(x => new object?[] {
            x.Email,
            OrderBy.Desc(x.BirthDate),
            Select.Case()
                .When(x.Email == "example@example.com")
                .Then(OrderBy.Desc(x.Username))
                .Else(x.Email)
        })

        // @pemail = 'example@example.com'
        // ORDER BY creation_date, birth_date DESC, CASE WHEN email == @pemail THEN username DESC ELSE email
        .OrderByDesc(x => new object?[] {
            OrderBy.Asc(x.Email),
            x.BirthDate,
            Select.Case()
                .When(x.Email == "example@example.com")
                .Then(x.Username)
                .Else(OrderBy.Asc(x.Email))
        })
        .WithConnection(connection)
        .ExecuteAsync();

    await foreach (var user in users)
    {
        var isExample = user.GetCustomValue<bool>("is_example");
        if (!isExample)
            Console.WriteLine(user.Email);
    }
}

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.AddLogging(logging => logging.AddConsole());

builder.WebHost.UseKestrelHttpsConfiguration();
builder.Configuration.AddJsonFile("appsettings.json");

builder.AddSharedDb();
builder.AddAuthDb();
builder.AddUserDb();

var app = builder.Build();

// TODO: Bit flags in roles

await app.EnsureLatestSharedDbMigration();
await app.EnsureLatestAuthDbMigration();
await app.EnsureLatestUserDbMigration();

var databaseFactory = app.Services.GetRequiredKeyedService<IDbConnectionFactory>("AuthDb");

await TestAsync(databaseFactory.Create());

await app.RunAsync();

#nullable enable
