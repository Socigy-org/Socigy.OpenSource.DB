using Example.Auth.DB;
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
var authConnection = connectionFactory.Create();

await app.RunAsync();
