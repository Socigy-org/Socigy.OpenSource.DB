using Example.Auth.DB;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
});

builder.WebHost.UseKestrelHttpsConfiguration();

builder.AddAuthDb();
//builder.AddSharedDb();
//builder.AddUserDb();

var app = builder.Build();

await new User().TestAsync();

app.Run();

public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);

[JsonSerializable(typeof(Todo[]))]
internal partial class AppJsonSerializerContext : JsonSerializerContext
{

}

public static class BuildExtensions
{
    public static WebApplicationBuilder AddAuthDb(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<MigrationManager, AuthDbMigrationmanager>();
        return builder;
    }
}

public interface IMigrationManager
{
    string GetCurrentMigrationVersion();
}

public abstract class MigrationManager : IMigrationManager
{
    public string GetCurrentMigrationVersion()
    {
        throw new NotImplementedException();
    }
}

public class AuthDbMigrationmanager : MigrationManager
{

}

public class UserDbMigrationmanager : MigrationManager
{

}