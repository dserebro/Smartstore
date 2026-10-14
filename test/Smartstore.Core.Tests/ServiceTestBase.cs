using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Catalog.Attributes;
using Smartstore.Core.Catalog.Discounts;
using Smartstore.Core.Catalog.Pricing;
using Smartstore.Core.Catalog.Pricing.Calculators;
using Smartstore.Core.Common;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Content.Media.Storage;
using Smartstore.Core.Data;
using Smartstore.Core.Tests.Catalog.Pricing.Calculators;
using Smartstore.Core.Tests.Checkout.Payment;
using Smartstore.Core.Tests.Common;
using Smartstore.Core.Tests.Content.Media.Storage;
using Smartstore.Core.Tests.Shipping;
using Smartstore.Core.Tests.Tax;
using Smartstore.Core.Web;
using Smartstore.Data;
using Smartstore.Data.Providers;
using Smartstore.Engine;
using Smartstore.Engine.Modularity;
using Smartstore.Test.Common;
using Smartstore.Utilities;

namespace Smartstore.Core.Tests;

[TestFixture]
public abstract class ServiceTestBase
{
    private MockProviderManager _providerManager = new();
    private SmartDbContext _dbContext;
    private IEngine _engine;
    private ILifetimeScope _lifetimeScope;
    private IDisposable _lifetimeToken;

    protected MockProviderManager ProviderManager
        => _providerManager;

    protected SmartDbContext DbContext
        => _dbContext;

    protected IEngine Engine
        => _engine;

    protected ILifetimeScope LifetimeScope
        => _lifetimeScope;

    [OneTimeSetUp]
    public void SetUp()
    {
        CommonHelper.IsHosted = false;
        CommonHelper.IsDevEnvironment = false;

        var host = CreateHostBuilder().Build();

        var appContext = host.Services.GetRequiredService<IApplicationContext>();
        var providerContainer = (appContext as IServiceProviderContainer)
            ?? throw new ApplicationException($"The implementation of '${nameof(IApplicationContext)}' must also implement '${nameof(IServiceProviderContainer)}'.");
        providerContainer.ApplicationServices = host.Services;

        var engine = host.Services.GetRequiredService<IEngine>();
        var scopeAccessor = host.Services.GetRequiredService<ILifetimeScopeAccessor>();
        engine.Scope = new ScopedServiceContainer(
            scopeAccessor,
            host.Services.GetRequiredService<IHttpContextAccessor>(),
            host.Services.AsLifetimeScope());

        _engine = engine;

        // Initialize memory DbContext
        InitDbContext();

        InitProviders();

        _lifetimeToken = scopeAccessor.BeginContextAwareScope(out _lifetimeScope);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _lifetimeToken.Dispose();
        _dbContext.Dispose();

        // INFO: required for IApplicationContext.TenantRoot to be not null after subsequent setups.
        DataSettings.Reload();
    }

    private IHostBuilder CreateHostBuilder()
    {
        IEngineStarter starter = null;

        var builder = Host.CreateDefaultBuilder()
            .UseServiceProviderFactory(new AutofacServiceProviderFactory())
            .ConfigureAppConfiguration((context, builder) =>
            {
                builder.AddEnvironmentVariables();
            })
            .ConfigureHostOptions((context, options) =>
            {
                context.HostingEnvironment.EnvironmentName = Debugger.IsAttached ? Environments.Development : Environments.Production;
            })
            .ConfigureServices((context, services) =>
            {
                var appContext = new SmartApplicationContext(
                    context.HostingEnvironment,
                    context.Configuration,
                    NullLogger.Instance);

                appContext.AppConfiguration.EngineType = typeof(TestEngine).AssemblyQualifiedName;

                starter = EngineFactory
                    .Create(appContext.AppConfiguration)
                    .Start(appContext);

                starter.ConfigureServices(services);

            })
            .ConfigureContainer<ContainerBuilder>((context, builder) =>
            {
                starter.ConfigureContainer(builder);
                starter.Dispose();

                // Register mocked IGenericAttributeService, else generic attributes e.g. for customer will throw.
                var genericAttributeServiceMock = new Mock<IGenericAttributeService>();
                genericAttributeServiceMock
                    .Setup(x => x.GetAttributesForEntity(It.IsAny<string>(), It.IsAny<int>()))
                    .Returns<string, int>((name, id) =>
                    {
                        return new GenericAttributeCollection(_dbContext.GenericAttributes, name, id, 0);
                    });

                builder.RegisterInstance(genericAttributeServiceMock.Object).As<IGenericAttributeService>().SingleInstance();

                // Register some dependencies which will be resolved by Autofac during obtaining PriceCalculators.
                var productAttributeMaterializerMock = new Mock<IProductAttributeMaterializer>();
                builder.RegisterInstance(productAttributeMaterializerMock.Object).As<IProductAttributeMaterializer>().SingleInstance();

                // Register mocked IUserAgentParser, else DeviceRule.GetDefaultOptions() (called eagerly while
                // building the full RuleScope.Customer descriptor catalog) will throw ComponentNotRegisteredException.
                var userAgentParserMock = new Mock<IUserAgentParser>();
                userAgentParserMock
                    .Setup(x => x.GetDetectableAgents(It.IsAny<UserAgentSegment>()))
                    .Returns(Enumerable.Empty<string>());
                builder.RegisterInstance(userAgentParserMock.Object).As<IUserAgentParser>().SingleInstance();
            });

        return builder;
    }

    private void InitDbContext()
    {
        DataSettings.Instance = CreateTestDataSettings(new TestDbFactory());
        DataSettings.SetTestMode(true);

        var builder = CreateDbContextOptionsBuilder();

        _dbContext = new SmartDbContext((DbContextOptions<SmartDbContext>)builder.Options);
        _dbContext.Database.EnsureCreated();
    }

    /// <summary>
    /// Builds a test <see cref="DataSettings"/> instance backed by <paramref name="dbFactory"/>. Shared by
    /// <see cref="InitDbContext"/> and derived fixtures (e.g. <c>TargetGroupEvaluatorTaskTestsBase</c>) that
    /// need a <see cref="SmartDbContext"/> backed by a different <see cref="DbFactory"/>.
    /// </summary>
    protected static DataSettings CreateTestDataSettings(DbFactory dbFactory)
    {
        return new DataSettings
        {
            AppVersion = SmartstoreVersion.Version,
            ConnectionString = "Test",
            TenantName = "Default",
            TenantRoot = null, // TODO
            DbFactory = dbFactory
        };
    }

    /// <summary>
    /// Builds the shared <see cref="DbContextOptionsBuilder{TContext}"/> for <see cref="SmartDbContext"/>,
    /// optionally wired with <paramref name="interceptor"/>. Shared by <see cref="InitDbContext"/> and derived
    /// fixtures that construct additional <see cref="SmartDbContext"/> instances.
    /// </summary>
    protected static DbContextOptionsBuilder CreateDbContextOptionsBuilder(IInterceptor interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<SmartDbContext>()
            .UseDbFactory(factoryBuilder =>
            {
                factoryBuilder
                    .AddModelAssemblies(new[]
                    {
                        // Add all core models from Smartstore.Core assembly
                        typeof(SmartDbContext).Assembly
                    });
            });

        if (interceptor != null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder;
    }

    private void InitProviders()
    {
        _providerManager.RegisterProvider("FixedTaxRateTest", new FixedRateTestTaxProvider());
        _providerManager.RegisterProvider("FixedRateTestShippingRateComputationMethod", new FixedRateTestShippingRateComputationMethod());
        _providerManager.RegisterProvider("Payments.TestMethod1", new TestPaymentMethod1());
        _providerManager.RegisterProvider("Payments.TestMethod2", new TestPaymentMethod2());
        _providerManager.RegisterProvider("CurrencyExchange.TestProvider", new TestExchangeRateProvider());
        _providerManager.RegisterProvider(DatabaseMediaStorageProvider.SystemName, new TestDatabaseMediaStorageProvider());
    }

    private void InitModules()
    {
        var modules = new List<IModuleDescriptor>
        {
            new ModuleDescriptor { SystemName = "Smartstore.Tax" },
            new ModuleDescriptor { SystemName = "Smartstore.Shipping" },
            new ModuleDescriptor { SystemName = "Smartstore.Payment" },
        };

        //ModuleExplorer.ReferencedPlugins = plugins;
    }

    protected List<Lazy<IPriceCalculator, PriceCalculatorMetadata>> GetPriceCalculators(
        IPriceCalculatorFactory priceCalculatorFactory,
        IDiscountService discountService,
        PriceSettings priceSettings)
    {
        var calculators = new List<Lazy<IPriceCalculator, PriceCalculatorMetadata>>();
        var productMetadata = new PriceCalculatorMetadata { ValidTargets = CalculatorTargets.Product, Order = CalculatorOrdering.Default + 10 };

        var attributePriceCalculator = new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
            new AttributePriceCalculator(priceCalculatorFactory, DbContext, priceSettings), productMetadata);

        var bundlePriceCalculator =
            new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
                new BundlePriceCalculator(priceCalculatorFactory, null),
                new() { ValidTargets = CalculatorTargets.Bundle, Order = CalculatorOrdering.Early });

        var discountPriceCalculator =
            new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
                new DiscountPriceCalculator(DbContext, discountService, priceSettings),
                new() { ValidTargets = CalculatorTargets.All, Order = CalculatorOrdering.Late });

        var groupedProductPriceCalculator =
            new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
                new GroupedProductPriceCalculator(null, priceCalculatorFactory, null),
                new() { ValidTargets = CalculatorTargets.GroupedProduct, Order = CalculatorOrdering.Early });

        var lowestPriceCalculator = new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
            new LowestPriceCalculator(), productMetadata);

        var offerPriceCalculator =
            new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
                new OfferPriceCalculator(),
                new() { ValidTargets = CalculatorTargets.Product | CalculatorTargets.Bundle, Order = CalculatorOrdering.Default });

        var preselectedPriceCalculator =
            new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
                new PreselectedPriceCalculator(null),
                new() { ValidTargets = CalculatorTargets.Product, Order = CalculatorOrdering.Early + 1 });

        var tierPriceCalculator =
            new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
                new TierPriceCalculator(),
                new() { ValidTargets = CalculatorTargets.Product, Order = CalculatorOrdering.Default + 100 });

        // Custom calculator for additional charge.
        var customCalculator = new Lazy<IPriceCalculator, PriceCalculatorMetadata>(() =>
            new CustomAdditionalChargeCalculator(), new PriceCalculatorMetadata { ValidTargets = CalculatorTargets.Product, Order = CalculatorOrdering.Default });

        calculators.AddRange(new[] {
            attributePriceCalculator,
            bundlePriceCalculator,
            discountPriceCalculator,
            groupedProductPriceCalculator,
            lowestPriceCalculator,
            offerPriceCalculator,
            preselectedPriceCalculator,
            tierPriceCalculator,
            customCalculator
        });

        return calculators;
    }
}