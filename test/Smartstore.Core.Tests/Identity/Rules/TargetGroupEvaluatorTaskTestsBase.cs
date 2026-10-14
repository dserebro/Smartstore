using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Data;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Localization;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Rendering;
using Smartstore.Core.Stores;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Scheduling;
using Smartstore.Test.Common;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Identity.Rules;

/// <summary>
/// Base fixture for <see cref="TargetGroupEvaluatorTask"/> tests.
/// </summary>
/// <remarks>
/// <see cref="TargetGroupEvaluatorTask.Run"/> starts with <c>ExecuteDeleteAsync</c>, a bulk operation that
/// the EF Core InMemory provider (used by <see cref="ServiceTestBase"/>) does not support. This fixture
/// therefore backs its own <see cref="SmartDbContext"/> instances with a dedicated SQLite in-memory database
/// (via a single, externally-owned, already-open <see cref="SqliteConnection"/> shared across every
/// <see cref="SmartDbContext"/> created for a test) so that bulk delete/update statements, chunked commits
/// and real EF Core query execution all behave exactly as they do against a real relational database.
/// </remarks>
[TestFixture]
public abstract class TargetGroupEvaluatorTaskTestsBase : ServiceTestBase
{
    private SqliteConnection _sqliteConnection;
    private SqliteTestDbFactory _sqliteDbFactory;

    protected Mock<IStoreContext> StoreContextMock { get; private set; }
    protected Mock<ILocalizationService> LocalizationServiceMock { get; private set; }
    protected Mock<ICurrencyService> CurrencyServiceMock { get; private set; }

    [OneTimeSetUp]
    public void SqliteSetUp()
    {
        // A single, already-open connection is reused for every SmartDbContext created by this fixture.
        // SQLite's ":memory:" database only exists for the lifetime of the connection that created it, so
        // sharing the connection (rather than the connection string) is what lets separate SmartDbContext
        // instances (e.g. one for seeding, one for the task-under-test, one for assertions) see the same data.
        _sqliteConnection = new SqliteConnection("DataSource=:memory:");
        _sqliteConnection.Open();

        _sqliteDbFactory = new SqliteTestDbFactory(_sqliteConnection);

        DataSettings.Instance = new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "Test",
            TenantName = "Default",
            TenantRoot = null,
            DbFactory = _sqliteDbFactory
        };
        DataSettings.SetTestMode(true);

        StoreContextMock = new Mock<IStoreContext>();
        StoreContextMock
            .Setup(x => x.GetCachedStores())
            .Returns(new StoreEntityCache(new List<Store>()));

        // Loose mock: only ILocalizationService.GetLocalizedEnum<T> is touched (indirectly, by
        // TargetGroupService.LoadDescriptorsAsync), and that is a real interface member Moq can
        // intercept safely without explicit setup.
        LocalizationServiceMock = new Mock<ILocalizationService>();

        CurrencyServiceMock = new Mock<ICurrencyService>();
        CurrencyServiceMock
            .Setup(x => x.PrimaryCurrency)
            .Returns(new Currency { Id = 1, CurrencyCode = "USD" });

        using var db = BuildDbContext(null);
        db.Database.EnsureCreated();
    }

    [OneTimeTearDown]
    public void SqliteTearDown()
    {
        _sqliteConnection?.Dispose();
    }

    [TearDown]
    public async Task CleanupSeedDataAsync()
    {
        // Each test seeds its own data. Wipe everything between tests so that leftover rows from one
        // test cannot influence the next (the SQLite in-memory database persists for the whole fixture,
        // because it is backed by a single, never-closed connection).
        using var db = BuildDbContext(null);

        await db.CustomerRoleMappings.ExecuteDeleteAsync();
        await db.Rules.ExecuteDeleteAsync();
        await db.RuleSets.ExecuteDeleteAsync();
        await db.CustomerRoles.ExecuteDeleteAsync();
        await db.Customers.ExecuteDeleteAsync();
    }

    /// <summary>
    /// Creates a fresh <see cref="SmartDbContext"/> backed by the shared SQLite in-memory connection.
    /// Callers own the returned instance and must dispose it (e.g. via <c>using</c>).
    /// </summary>
    protected SmartDbContext CreateSmartDbContext(IInterceptor interceptor = null)
    {
        return BuildDbContext(interceptor);
    }

    /// <summary>
    /// Builds the <see cref="TargetGroupEvaluatorTask"/> under test, wired up with real
    /// <see cref="IRuleService"/>/<see cref="ITargetGroupService"/> instances (no mocking of rule
    /// evaluation) plus a mocked <see cref="IRuleProviderFactory"/> (which only hands out the real
    /// <see cref="ITargetGroupService"/> instance, exactly as production code does) and a mocked
    /// <see cref="ICacheManager"/> so that cache-invalidation calls can be asserted.
    /// </summary>
    protected (TargetGroupEvaluatorTask Task, Mock<ICacheManager> CacheManager) CreateSut(SmartDbContext db)
    {
        var ruleService = new RuleService(
            db,
            Mock.Of<IWorkContext>(),
            new Lazy<IEnumerable<IRuleOptionsProvider>>(() => Array.Empty<IRuleOptionsProvider>()));

        var targetGroupService = new TargetGroupService(
            db,
            ruleService,
            StoreContextMock.Object,
            LocalizationServiceMock.Object,
            CurrencyServiceMock.Object);

        var ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, It.IsAny<object>()))
            .Returns(targetGroupService);

        var cacheManagerMock = new Mock<ICacheManager>();

        var task = new TargetGroupEvaluatorTask(db, cacheManagerMock.Object, ruleService, ruleProviderFactoryMock.Object);

        return (task, cacheManagerMock);
    }

    /// <summary>
    /// Creates a <see cref="TaskExecutionContext"/> for running <see cref="TargetGroupEvaluatorTask.Run"/>.
    /// </summary>
    /// <param name="parameters">Task parameters, e.g. "CustomerRoleIds".</param>
    /// <param name="onProgressReported">
    /// Invoked every time <see cref="TaskExecutionContext.SetProgressAsync"/> calls through to
    /// <see cref="ITaskStore.UpdateExecutionInfoAsync"/> (once per processed role). Used by the
    /// cancellation test to cancel mid-loop.
    /// </param>
    protected TaskExecutionContext CreateExecutionContext(
        IDictionary<string, string> parameters = null,
        Action onProgressReported = null)
    {
        var taskStoreMock = new Mock<ITaskStore>();
        var setup = taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        if (onProgressReported != null)
        {
            setup.Callback(onProgressReported);
        }

        var taskDescriptor = new TaskDescriptor
        {
            Name = nameof(TargetGroupEvaluatorTask),
            Type = typeof(TargetGroupEvaluatorTask).AssemblyQualifiedName,
            Enabled = true
        };

        var executionInfo = new TaskExecutionInfo
        {
            Task = taskDescriptor,
            IsRunning = true,
            StartedOnUtc = DateTime.UtcNow
        };

        return new TaskExecutionContext(
            taskStoreMock.Object,
            Mock.Of<IAsyncState>(),
            new DefaultHttpContext(),
            LifetimeScope,
            executionInfo,
            parameters);
    }

    private SmartDbContext BuildDbContext(IInterceptor interceptor)
    {
        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder.AddModelAssemblies(new[]
                {
                    typeof(SmartDbContext).Assembly
                });
            });

        if (interceptor != null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
    }

    /// <summary>
    /// Counts how many times <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> (or its sync
    /// counterpart) actually reaches the database, i.e. how many chunk commits
    /// <see cref="Smartstore.Data.DbContextScope.CommitAsync"/> performed. Used to verify the
    /// 500-row chunking behavior of <see cref="TargetGroupEvaluatorTask.Run"/>.
    /// </summary>
    protected sealed class SaveChangesCountingInterceptor : SaveChangesInterceptor
    {
        public int SaveChangesCount { get; private set; }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            SaveChangesCount++;
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveChangesCount++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Minimal <see cref="DbFactory"/> implementation that backs every <see cref="SmartDbContext"/> it
    /// configures with the SAME already-open <see cref="SqliteConnection"/>, so data seeded through one
    /// context instance is immediately visible to any other context instance created by this factory.
    /// </summary>
    /// <remarks>
    /// This intentionally mirrors only the parts of the production <c>Smartstore.Data.Sqlite.SqliteDbFactory</c>
    /// that are reachable with public EF Core APIs (plain <c>UseSqlite</c>, the decimal-to-double conversion
    /// and the built-in "NOCASE" collation). It deliberately does NOT replace
    /// <c>ISqliteRelationalConnection</c>/<c>IQueryTranslationPostprocessorFactory</c>/<c>IMethodCallTranslatorProvider</c>
    /// the way the production factory does, because those replacement types live in the internal
    /// <c>Smartstore.Data.Sqlite</c> project, which is not referenced by this test project (only the plain
    /// <c>Microsoft.EntityFrameworkCore.Sqlite</c> NuGet package is). None of the scenarios exercised by
    /// these tests depend on those Smartstore-specific SQLite translator services.
    /// </remarks>
    private sealed class SqliteTestDbFactory : DbFactory
    {
        private readonly SqliteConnection _connection;

        public SqliteTestDbFactory(SqliteConnection connection)
        {
            _connection = connection;
        }

        public override DbSystemType DbSystem { get; } = DbSystemType.SQLite;

        public override DbConnectionStringBuilder CreateConnectionStringBuilder(string connectionString)
            => throw new NotImplementedException();

        public override DbConnectionStringBuilder CreateConnectionStringBuilder(
            string server,
            string database,
            string userName,
            string password)
            => throw new NotImplementedException();

        public override DataProvider CreateDataProvider(DatabaseFacade database)
            => new TestDataProvider(database);

        public override TContext CreateDbContext<TContext>(string connectionString, int? commandTimeout = null)
        {
            var optionsBuilder = new DbContextOptionsBuilder<TContext>()
                .UseSqlite(_connection);

            return (TContext)Activator.CreateInstance(typeof(TContext), new object[] { optionsBuilder.Options });
        }

        public override DbContextOptionsBuilder ConfigureDbContext(DbContextOptionsBuilder builder, string connectionString)
        {
            return builder.UseSqlite(_connection);
        }

        public override void ConfigureModelConventions(ModelConfigurationBuilder configurationBuilder)
        {
            configurationBuilder.Properties<decimal>().HaveConversion<double>();
            configurationBuilder.Properties<decimal?>().HaveConversion<double?>();
            configurationBuilder.Properties<string>().UseCollation("NOCASE");
        }

        public override void CreateModel(ModelBuilder modelBuilder)
        {
            modelBuilder.UseCollation("NOCASE");
        }
    }
}
