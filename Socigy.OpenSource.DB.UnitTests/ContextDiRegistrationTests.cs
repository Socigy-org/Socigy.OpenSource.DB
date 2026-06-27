using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Socigy.OpenSource.DB.Core;
using Socigy.OpenSource.DB.Core.Context;
using Socigy.OpenSource.DB.TestDb.Context;
using Socigy.OpenSource.DB.TestDb.Extensions;

namespace Socigy.OpenSource.DB.UnitTests
{
    /// <summary>
    /// Regression: the generated DI registration registered <see cref="SocigyDbContextOptions"/> as a single
    /// non-keyed singleton and the factory resolved that shared instance, so in a modular monolith every
    /// <c>Add{Db}Context</c> after the first silently lost its own options (ConnectionKey / lifetime). The factory
    /// must capture the options configured for its own registration.
    /// </summary>
    [TestFixture]
    public class ContextDiRegistrationTests
    {
        private static string? ConnectionKeyOf(object factory)
        {
            FieldInfo? field = null;
            for (var t = factory.GetType(); t != null && field == null; t = t.BaseType)
                field = t.GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance);
            var options = (SocigyDbContextOptions)field!.GetValue(factory)!;
            return options.ConnectionKey;
        }

        [Test]
        public void Each_context_registration_keeps_its_own_options()
        {
            var services = new ServiceCollection();
            // The factory closure resolves the keyed connection factory; a stub is enough (we never open it).
            services.AddKeyedSingleton<IDbConnectionFactory>("TestDb", new Mock<IDbConnectionFactory>().Object);

            services.AddTestDbContext(o => o.ConnectionKey = "db-a");
            services.AddTestDbContext(o => o.ConnectionKey = "db-b");

            using var sp = services.BuildServiceProvider();
            var factories = sp.GetServices<ISocigyDatabaseFactory<ITestDb>>().ToList();

            Assert.That(factories, Has.Count.EqualTo(2));
            Assert.That(factories.Select(ConnectionKeyOf), Is.EquivalentTo(new[] { "db-a", "db-b" }),
                "both factories must not collapse to the first registration's options");
        }
    }
}
