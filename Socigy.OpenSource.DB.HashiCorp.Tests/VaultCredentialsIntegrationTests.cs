using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Lease handling for Vault-issued database credentials, against a <b>live</b> Vault (or OpenBao) with the
/// Database secrets engine wired to a real PostgreSQL.
///
/// This is where the difference between "renew" and "re-lease" is actually observable, and the whole reason
/// the issue existed: <c>RefreshAsync</c> used to call <c>GetCredentialsAsync</c> unconditionally, so every
/// refresh minted a brand-new PostgreSQL role, the connection string changed under the connection factory,
/// and the superseded lease was left to age out at its TTL — up to <c>ceil(TTL / RefreshInterval) + 1</c>
/// live dynamic roles per database, each with an Npgsql pool behind it that was never reclaimed.
///
/// Marked <see cref="ExplicitAttribute"/>; CI configures the engine and runs it by filter.
/// Set up (dev server, and a reachable PostgreSQL):
/// <code>
///   vault secrets enable database
///   vault write database/config/socigypg plugin_name=postgresql-database-plugin \
///       allowed_roles=socigy-role username=postgres password=1234 \
///       connection_url='postgresql://{{username}}:{{password}}@postgres:5432/postgres?sslmode=disable'
///   vault write database/roles/socigy-role db_name=socigypg default_ttl=1h max_ttl=24h \
///       creation_statements="CREATE ROLE \"{{name}}\" WITH LOGIN PASSWORD '{{password}}' VALID UNTIL '{{expiration}}'; GRANT ALL PRIVILEGES ON DATABASE postgres TO \"{{name}}\";" \
///       revocation_statements="SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE usename = '{{name}}'; REVOKE ALL PRIVILEGES ON DATABASE postgres FROM \"{{name}}\"; DROP ROLE IF EXISTS \"{{name}}\";"
/// </code>
/// The <c>revocation_statements</c> are not optional: PostgreSQL refuses to drop a role that still owns or
/// is granted anything ("cannot be dropped because some objects depend on it", SQLSTATE 2BP01), so without
/// them every revoke fails and the leases the provider means to release simply accumulate — which looks
/// exactly like the bug this fixture exists to prove is gone.
/// Override the address/token via <c>VAULT_ADDR</c> / <c>VAULT_TOKEN</c>.
/// </summary>
[TestFixture, Explicit("Requires a live Vault dev server with the Database secrets engine wired to PostgreSQL.")]
public class VaultCredentialsIntegrationTests
{
    private static string Addr => Environment.GetEnvironmentVariable("VAULT_ADDR") ?? "http://127.0.0.1:8200";
    private static string Token => Environment.GetEnvironmentVariable("VAULT_TOKEN") ?? "root";
    private const string Database = "TestDb";
    private const string Role = "socigy-role";

    private static VaultCredentialsOptions Options() => new()
    {
        Address = Addr,
        Token = Token,
        DatabaseRoles = new Dictionary<string, string> { [Database] = Role },
        BaseConnectionString = "Host=127.0.0.1;Port=5432",
    };

    private static VaultDbCredentialsProvider Provider(VaultCredentialsOptions options)
        => new(new VaultClientProvider(options), options);

    private static string UsernameOf(string connectionString)
        => connectionString.Split(';')
            .Select(p => p.Split('=', 2))
            .First(p => p[0].Trim().Equals("Username", StringComparison.OrdinalIgnoreCase))[1];

    /// <summary>Counts the leases Vault currently holds for the role — the thing the leak was made of.</summary>
    private static async Task<int> LiveLeaseCountAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(Addr) };
        http.DefaultRequestHeaders.Add("X-Vault-Token", Token);

        using var request = new HttpRequestMessage(new HttpMethod("LIST"), $"/v1/sys/leases/lookup/database/creds/{Role}/");
        using var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return 0;   // 404 when none are outstanding

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("keys").GetArrayLength();
    }

    [Test]
    public async Task Refreshing_renews_in_place_so_the_credentials_do_not_change()
    {
        var options = Options();
        using var provider = Provider(options);

        await provider.RefreshAsync(Database, null);
        var first = provider.GetConnectionString(Database, null)!;

        await provider.RefreshAsync(Database, null);
        var second = provider.GetConnectionString(Database, null)!;

        Assert.That(second, Is.EqualTo(first),
            "a renewed lease keeps the same username and password, so the connection string is stable and " +
            "Npgsql keeps using the pool it already has instead of stranding it and opening another");
    }

    [Test]
    public async Task Renewal_leaves_exactly_one_live_lease_however_many_times_it_refreshes()
    {
        var options = Options();
        using var provider = Provider(options);

        int before = await LiveLeaseCountAsync();

        await provider.RefreshAsync(Database, null);
        for (int i = 0; i < 4; i++)
            await provider.RefreshAsync(Database, null);

        Assert.That(await LiveLeaseCountAsync() - before, Is.EqualTo(1),
            "five refreshes used to mean five live PostgreSQL roles; renewing means one");
    }

    [Test]
    public async Task Disabling_renewal_re_leases_and_revokes_the_previous_lease()
    {
        var options = Options();
        options.RenewLeases = false;   // force the re-lease path
        using var provider = Provider(options);

        int before = await LiveLeaseCountAsync();

        await provider.RefreshAsync(Database, null);
        var first = provider.GetConnectionString(Database, null)!;

        await provider.RefreshAsync(Database, null);
        var second = provider.GetConnectionString(Database, null)!;

        Assert.Multiple(async () =>
        {
            Assert.That(UsernameOf(second), Is.Not.EqualTo(UsernameOf(first)),
                "a re-lease genuinely mints a new role");
            Assert.That(await LiveLeaseCountAsync() - before, Is.EqualTo(1),
                "but the superseded one is revoked rather than left to age out at its TTL");
        });
    }

    [Test]
    public async Task Disposing_revokes_every_held_lease()
    {
        var options = Options();
        int before = await LiveLeaseCountAsync();

        using (var provider = Provider(options))
            await provider.RefreshAsync(Database, null);

        Assert.That(await LiveLeaseCountAsync(), Is.EqualTo(before),
            "a stopped process should not leave dynamic database roles alive for the rest of their TTL");
    }

    [Test]
    public async Task The_provider_serves_connections_from_a_pool_it_owns()
    {
        var options = Options();
        using var provider = Provider(options);
        await provider.RefreshAsync(Database, null);

        using var connection = provider.CreateConnection(Database, null);

        Assert.That(connection, Is.Not.Null,
            "owning the data source is what lets the old pool be disposed on rotation, rather than left " +
            "in Npgsql's static PoolManager for the life of the process");
    }
}
