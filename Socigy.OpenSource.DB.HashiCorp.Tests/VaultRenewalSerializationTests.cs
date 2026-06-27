using System.Threading;
using System.Threading.Tasks;
using Socigy.OpenSource.DB.HashiCorp;

namespace Socigy.OpenSource.DB.HashiCorp.Tests;

/// <summary>
/// Regression: a timer-driven renewal racing a manual one used to both run, each performing a relogin and
/// overwriting the shared client (wasted Vault logins, nondeterministic active token). Renewal/relogin must be
/// serialized so only one runs at a time.
/// </summary>
[TestFixture]
public class VaultRenewalSerializationTests
{
    private sealed class CountingProvider : VaultClientProvider
    {
        private readonly object _gate = new();
        private int _active;
        public int MaxConcurrent;
        public int Calls;

        public CountingProvider() : base(new VaultCredentialsOptions
        {
            Address = "http://127.0.0.1:8200",
            AppRoleId = "role",
            AppRoleSecretId = "secret",
        })
        { }

        internal override async Task<double?> RenewOrReloginCoreAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                Calls++;
                _active++;
                if (_active > MaxConcurrent) MaxConcurrent = _active;
            }
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            lock (_gate) { _active--; }
            return 100;
        }
    }

    [Test]
    public async Task Concurrent_renewals_run_one_at_a_time()
    {
        var provider = new CountingProvider();

        var tasks = new Task[16];
        for (int i = 0; i < tasks.Length; i++)
            tasks[i] = provider.RenewOrReloginAsync();
        await Task.WhenAll(tasks);

        Assert.That(provider.Calls, Is.EqualTo(16), "every call still runs");
        Assert.That(provider.MaxConcurrent, Is.EqualTo(1), "renewal/relogin must be serialized by the lock");
    }
}
