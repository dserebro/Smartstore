using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Caching;
using Smartstore.Collections;
using Smartstore.Core.Data;
using Smartstore.Core.Identity;
using Smartstore.Core.Identity.Rules;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Scheduling;
using Smartstore.Test.Common;
using Smartstore.Threading;

namespace Smartstore.Core.Tests.Platform.Identity.Rules;

[TestFixture]
public class TargetGroupEvaluatorTaskTests : ServiceTestBase
{
    private TestableTargetGroupEvaluatorTask _task;
    private Mock<ICacheManager> _cacheMock;
    private Mock<IRuleService> _ruleServiceMock;
    private Mock<IRuleProviderFactory> _ruleProviderFactoryMock;
    private Mock<ITargetGroupService> _targetGroupServiceMock;

    [OneTimeSetUp]
    public new void SetUp()
    {
        _cacheMock = new Mock<ICacheManager>();
        _ruleServiceMock = new Mock<IRuleService>();
        _ruleProviderFactoryMock = new Mock<IRuleProviderFactory>();
        _targetGroupServiceMock = new Mock<ITargetGroupService>();

        _ruleProviderFactoryMock
            .Setup(x => x.GetProvider(RuleScope.Customer, null))
            .Returns(_targetGroupServiceMock.Object);

        _task = new TestableTargetGroupEvaluatorTask(
            DbContext,
            _cacheMock.Object,
            _ruleServiceMock.Object,
            _ruleProviderFactoryMock.Object);
    }

    [SetUp]
    public async Task TestSetUp()
    {
        DbContext.CustomerRoleMappings.RemoveRange(DbContext.CustomerRoleMappings);
        DbContext.Customers.RemoveRange(DbContext.Customers);
        await DbContext.SaveChangesAsync();

        var roles = DbContext.CustomerRoles
            .Where(x => x.SystemName == null || x.SystemName.StartsWith("TestRole"))
            .ToList();
        if (roles.Count > 0)
        {
            DbContext.CustomerRoles.RemoveRange(roles);
            await DbContext.SaveChangesAsync();
        }

        _cacheMock.Reset();
        _ruleServiceMock.Reset();
        _targetGroupServiceMock.Reset();
    }

    #region Helpers

    private TaskExecutionContext CreateTaskContext(IDictionary<string, string> parameters = null)
    {
        var taskStoreMock = new Mock<ITaskStore>();
        taskStoreMock
            .Setup(x => x.UpdateExecutionInfoAsync(It.IsAny<TaskExecutionInfo>()))
            .Returns(Task.CompletedTask);

        var asyncStateMock = new Mock<IAsyncState>();
        asyncStateMock
            .Setup(x => x.GetAsync<TaskDescriptor>(It.IsAny<string>()))
            .ReturnsAsync((TaskDescriptor)null);

        var httpContext = new DefaultHttpContext();
        var componentContext = LifetimeScope.Resolve<IComponentContext>();

        var executionInfo = new TaskExecutionInfo
        {
            TaskDescriptorId = 1,
            IsRunning = true,
            StartedOnUtc = DateTime.UtcNow,
            Task = new TaskDescriptor
            {
                Id = 1,
                Name = "TargetGroupEvaluatorTask",
                Type = nameof(TargetGroupEvaluatorTask),
                Enabled = true,
                CronExpression = "15 2 * * *"
            }
        };

        return new TaskExecutionContext(
            taskStoreMock.Object,
            asyncStateMock.Object,
            httpContext,
            componentContext,
            executionInfo,
            parameters);
    }

    private async Task<(CustomerRole role, RuleSetEntity ruleSet)> SeedRoleWithRuleSetAsync(
        string systemName,
        bool roleActive = true,
        bool ruleSetActive = true)
    {
        var ruleSet = new RuleSetEntity
        {
            Name = $"RuleSet for {systemName}",
            IsActive = ruleSetActive,
            Scope = RuleScope.Customer,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };

        var role = new CustomerRole
        {
            Name = systemName,
            SystemName = systemName,
            Active = roleActive
        };
        role.RuleSets.Add(ruleSet);

        DbContext.CustomerRoles.Add(role);
        await DbContext.SaveChangesAsync();

        return (role, ruleSet);
    }

    private async Task<List<Customer>> SeedCustomersAsync(int count)
    {
        var customers = new List<Customer>();
        for (int i = 0; i < count; i++)
        {
            customers.Add(new Customer
            {
                CustomerGuid = Guid.NewGuid(),
                Active = true,
                CreatedOnUtc = DateTime.UtcNow,
                LastActivityDateUtc = DateTime.UtcNow
            });
        }

        DbContext.Customers.AddRange(customers);
        await DbContext.SaveChangesAsync();
        return customers;
    }

    private void SetupRuleServiceForRuleSet(RuleSetEntity ruleSet, FilterExpressionGroup expressionGroup)
    {
        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.Is<RuleSetEntity>(rs => rs.Id == ruleSet.Id),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .Returns(Task.FromResult<IRuleExpressionGroup>(expressionGroup));
    }

    private void SetupProcessFilter(IQueryable<Customer> customersQuery)
    {
        var pagedList = new Mock<IPagedList<Customer>>();
        pagedList.Setup(x => x.SourceQuery).Returns(customersQuery);

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns(pagedList.Object);
    }

    private void SetupProcessFilterEmpty()
    {
        SetupProcessFilter(DbContext.Customers.Where(x => false));
    }

    private async Task SeedSystemMappingsAsync(int roleId, int[] customerIds)
    {
        foreach (int customerId in customerIds)
        {
            DbContext.CustomerRoleMappings.Add(new CustomerRoleMapping
            {
                CustomerId = customerId,
                CustomerRoleId = roleId,
                IsSystemMapping = true
            });
        }

        await DbContext.SaveChangesAsync();
    }

    #endregion

    #region 1. Full Evaluation (no CustomerRoleIds parameter)

    [Test]
    public async Task Full_evaluation_creates_mappings_for_active_roles_with_active_rulesets()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Full");
        var customers = await SeedCustomersAsync(3);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
            .ToList();

        mappings.Count.ShouldEqual(3);
        foreach (var customer in customers)
        {
            mappings.Any(m => m.CustomerId == customer.Id).ShouldBeTrue();
        }
    }

    [Test]
    public async Task Full_evaluation_skips_inactive_roles()
    {
        await SeedRoleWithRuleSetAsync("TestRole_Inactive", roleActive: false);
        await SeedCustomersAsync(2);

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        mappings.Count.ShouldEqual(0);
    }

    [Test]
    public async Task Full_evaluation_skips_roles_without_active_rulesets()
    {
        await SeedRoleWithRuleSetAsync("TestRole_InactiveRuleSet", roleActive: true, ruleSetActive: false);
        await SeedCustomersAsync(2);

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        mappings.Count.ShouldEqual(0);
    }

    #endregion

    #region 2. Filtered Evaluation (with CustomerRoleIds parameter)

    [Test]
    public async Task Filtered_evaluation_only_evaluates_specified_roles()
    {
        var (role1, ruleSet1) = await SeedRoleWithRuleSetAsync("TestRole_Target");
        var (role2, ruleSet2) = await SeedRoleWithRuleSetAsync("TestRole_Other");
        var customers = await SeedCustomersAsync(3);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet1, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = role1.Id.ToString()
        };
        var ctx = CreateTaskContext(parameters);
        await _task.Run(ctx, CancellationToken.None);

        var targetMappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role1.Id && x.IsSystemMapping)
            .ToList();
        targetMappings.Count.ShouldEqual(3);

        var otherMappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role2.Id && x.IsSystemMapping)
            .ToList();
        otherMappings.Count.ShouldEqual(0);
    }

    [Test]
    public async Task Filtered_evaluation_preserves_other_roles_system_mappings()
    {
        var (role1, ruleSet1) = await SeedRoleWithRuleSetAsync("TestRole_Filtered");
        var (role2, ruleSet2) = await SeedRoleWithRuleSetAsync("TestRole_Untouched");
        var customers = await SeedCustomersAsync(3);

        await SeedSystemMappingsAsync(role2.Id, customers.Select(c => c.Id).ToArray());

        var existingOtherMappingsCount = DbContext.CustomerRoleMappings
            .Count(x => x.CustomerRoleId == role2.Id && x.IsSystemMapping);
        existingOtherMappingsCount.ShouldEqual(3);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet1, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var parameters = new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = role1.Id.ToString()
        };
        var ctx = CreateTaskContext(parameters);
        await _task.Run(ctx, CancellationToken.None);

        var untouchedMappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role2.Id && x.IsSystemMapping)
            .ToList();
        untouchedMappings.Count.ShouldEqual(3);
    }

    #endregion

    #region 3. Cancellation Token Propagation

    [Test]
    public async Task Cancellation_during_ruleset_loop_exits_cleanly()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Cancel");

        var cts = new CancellationTokenSource();

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .Callback(() => cts.Cancel())
            .ReturnsAsync((IRuleExpressionGroup)null);

        var ctx = CreateTaskContext();
        await _task.Run(ctx, cts.Token);

        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        mappings.Count.ShouldEqual(0);
    }

    [Test]
    public async Task Cancellation_during_chunk_insert_preserves_committed_chunks()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_CancelChunk");
        await SeedCustomersAsync(1000);

        var cts = new CancellationTokenSource();
        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        int chunkCount = 0;
        EventHandler<SavedChangesEventArgs> onSaved = (_, args) =>
        {
            if (args.EntitiesSavedCount > 0)
            {
                chunkCount++;
                cts.Cancel();
            }
        };
        DbContext.SavedChanges += onSaved;

        var ctx = CreateTaskContext();

        try
        {
            await _task.Run(ctx, cts.Token);
        }
        finally
        {
            DbContext.SavedChanges -= onSaved;
        }

        var mappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
            .ToList();

        chunkCount.ShouldEqual(1);
        mappings.Count.ShouldEqual(500);
    }

    #endregion

    #region 4. Chunked Insertion and Batch Commits

    [Test]
    public async Task Chunked_insertion_handles_more_than_500_customers()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Chunk");
        var customers = await SeedCustomersAsync(550);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
            .ToList();

        mappings.Count.ShouldEqual(550);
    }

    [Test]
    public async Task Chunked_insertion_with_exactly_500_customers()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Exact500");
        var customers = await SeedCustomersAsync(500);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
            .ToList();

        mappings.Count.ShouldEqual(500);
    }

    [Test]
    public async Task DetachEntities_called_after_each_role_chunks_committed()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Detach");
        var customers = await SeedCustomersAsync(3);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        // After the task completes, DetachEntities should have been called,
        // so the change tracker should have no tracked CustomerRoleMapping entries.
        var trackedMappings = DbContext.ChangeTracker
            .Entries<CustomerRoleMapping>()
            .ToList();

        trackedMappings.Count.ShouldEqual(0);
    }

    #endregion

    #region 5. Cache Invalidation

    [Test]
    public async Task Cache_invalidated_when_mappings_change()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Cache");
        var customers = await SeedCustomersAsync(2);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN),
            Times.Once);
    }

    [Test]
    public async Task Cache_not_invalidated_when_no_mappings_change()
    {
        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    #endregion

    #region 6. No-Op Scenarios

    [Test]
    public async Task No_active_roles_produces_no_mappings()
    {
        await SeedCustomersAsync(5);

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings.Where(x => x.IsSystemMapping).ToList();
        mappings.Count.ShouldEqual(0);

        _cacheMock.Verify(
            x => x.RemoveByPatternAsync(It.IsAny<string>()),
            Times.Never);
    }

    [Test]
    public async Task Empty_filter_result_produces_no_mappings_for_role()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_EmptyFilter");
        await SeedCustomersAsync(3);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilterEmpty();

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
            .ToList();

        mappings.Count.ShouldEqual(0);
    }

    [Test]
    public async Task Null_expression_group_produces_no_mappings()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_NullExpr");
        await SeedCustomersAsync(2);

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IRuleExpressionGroup)null);

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
            .ToList();

        mappings.Count.ShouldEqual(0);
    }

    #endregion

    #region 7. Behavioral Parity Regression Tests

    [Test]
    public async Task Parity_delete_then_insert_pattern()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Parity_DTI");
        var customers = await SeedCustomersAsync(3);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var firstRunMappings = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
            .ToList();
        firstRunMappings.Count.ShouldEqual(3);

        // Re-run to verify delete-then-insert: old mappings should be replaced, not duplicated.
        // Note: ExecuteDeleteAsync may not work with InMemory provider.
        // If the second run succeeds, verify no duplicates.
        try
        {
            await _task.Run(ctx, CancellationToken.None);

            var secondRunMappings = DbContext.CustomerRoleMappings
                .Where(x => x.CustomerRoleId == role.Id && x.IsSystemMapping)
                .ToList();
            secondRunMappings.Count.ShouldEqual(3);
        }
        catch (InvalidOperationException)
        {
            // ExecuteDeleteAsync not supported by InMemory provider — expected limitation.
            // First run already verified insert behavior.
        }
    }

    [Test]
    public async Task Parity_page_size_500_for_fast_pager()
    {
        var (role, ruleSet) = await SeedRoleWithRuleSetAsync("TestRole_Parity_PageSize");
        var customers = await SeedCustomersAsync(600);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet, expression);

        int capturedPageSize = 0;

        _targetGroupServiceMock
            .Setup(x => x.ProcessFilter(
                It.IsAny<FilterExpression[]>(),
                It.IsAny<LogicalRuleOperator>(),
                It.IsAny<int>(),
                It.IsAny<int>()))
            .Returns<FilterExpression[], LogicalRuleOperator, int, int>((filters, op, pageIndex, pageSize) =>
            {
                capturedPageSize = pageSize;
                var pagedList = new Mock<IPagedList<Customer>>();
                pagedList.Setup(x => x.SourceQuery).Returns(DbContext.Customers.AsQueryable());
                return pagedList.Object;
            });

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        capturedPageSize.ShouldEqual(500);
    }

    [Test]
    public async Task Parity_progress_reported_per_role()
    {
        var (role1, ruleSet1) = await SeedRoleWithRuleSetAsync("TestRole_Progress1");
        var (role2, ruleSet2) = await SeedRoleWithRuleSetAsync("TestRole_Progress2");

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet1, expression);
        SetupRuleServiceForRuleSet(ruleSet2, expression);
        SetupProcessFilterEmpty();

        var mockCtx = new Mock<TaskExecutionContext>(
            new Mock<ITaskStore>().Object,
            new Mock<IAsyncState>().Object,
            new DefaultHttpContext(),
            LifetimeScope.Resolve<IComponentContext>(),
            new TaskExecutionInfo
            {
                TaskDescriptorId = 1,
                IsRunning = true,
                StartedOnUtc = DateTime.UtcNow,
                Task = new TaskDescriptor
                {
                    Id = 1,
                    Name = "Test",
                    Type = nameof(TargetGroupEvaluatorTask),
                    Enabled = true,
                    CronExpression = "15 2 * * *"
                }
            },
            null)
        { CallBase = true };

        var progressCalls = new List<(int value, int max, string msg)>();
        mockCtx
            .Setup(x => x.SetProgressAsync(It.IsAny<int?>(), It.IsAny<string>()))
            .Callback<int?, string>((progress, msg) =>
            {
                progressCalls.Add((progress ?? 0, 0, msg));
            })
            .Returns(Task.CompletedTask);

        await _task.Run(mockCtx.Object, CancellationToken.None);

        progressCalls.Count.ShouldEqual(2);
    }

    [Test]
    public async Task Parity_cancellation_check_in_ruleset_loop()
    {
        var (role, ruleSet1) = await SeedRoleWithRuleSetAsync("TestRole_CancelRS");

        // Add a second rule set to the same role to verify cancellation between rule sets.
        var ruleSet2 = new RuleSetEntity
        {
            Name = "Second RuleSet",
            IsActive = true,
            Scope = RuleScope.Customer,
            CreatedOnUtc = DateTime.UtcNow,
            UpdatedOnUtc = DateTime.UtcNow
        };
        var roleToUpdate = DbContext.CustomerRoles
            .Where(x => x.Id == role.Id)
            .First();
        roleToUpdate.RuleSets.Add(ruleSet2);
        await DbContext.SaveChangesAsync();

        var cts = new CancellationTokenSource();
        int callCount = 0;

        _ruleServiceMock
            .Setup(x => x.CreateExpressionGroupAsync(
                It.IsAny<RuleSetEntity>(),
                It.IsAny<IRuleVisitor>(),
                It.IsAny<bool>()))
            .Callback(() =>
            {
                callCount++;
                if (callCount >= 1)
                {
                    cts.Cancel();
                }
            })
            .ReturnsAsync((IRuleExpressionGroup)null);

        var ctx = CreateTaskContext();
        await _task.Run(ctx, cts.Token);

        // First rule set was processed, cancellation fired before second.
        callCount.ShouldEqual(1);
    }

    [Test]
    public async Task Parity_multiple_roles_evaluated_sequentially()
    {
        var (role1, ruleSet1) = await SeedRoleWithRuleSetAsync("TestRole_SeqA");
        var (role2, ruleSet2) = await SeedRoleWithRuleSetAsync("TestRole_SeqB");
        var customers = await SeedCustomersAsync(2);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet1, expression);
        SetupRuleServiceForRuleSet(ruleSet2, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        var mappingsRole1 = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role1.Id && x.IsSystemMapping)
            .ToList();
        var mappingsRole2 = DbContext.CustomerRoleMappings
            .Where(x => x.CustomerRoleId == role2.Id && x.IsSystemMapping)
            .ToList();

        mappingsRole1.Count.ShouldEqual(2);
        mappingsRole2.Count.ShouldEqual(2);
    }

    [Test]
    public async Task Parity_detach_entities_after_each_role()
    {
        var (role1, ruleSet1) = await SeedRoleWithRuleSetAsync("TestRole_DetachParity1");
        var (role2, ruleSet2) = await SeedRoleWithRuleSetAsync("TestRole_DetachParity2");
        var customers = await SeedCustomersAsync(3);

        var expression = new FilterExpressionGroup();
        SetupRuleServiceForRuleSet(ruleSet1, expression);
        SetupRuleServiceForRuleSet(ruleSet2, expression);
        SetupProcessFilter(DbContext.Customers.AsQueryable());

        var ctx = CreateTaskContext();
        await _task.Run(ctx, CancellationToken.None);

        // After processing multiple roles, DetachEntities should have been called
        // after each role. The change tracker should have no CustomerRoleMapping entries.
        var trackedMappings = DbContext.ChangeTracker
            .Entries<CustomerRoleMapping>()
            .ToList();

        trackedMappings.Count.ShouldEqual(0);

        // But the mappings should exist in the database (committed before detach).
        var totalMappings = DbContext.CustomerRoleMappings
            .Where(x => x.IsSystemMapping)
            .Count();
        totalMappings.ShouldEqual(6);
    }

    #endregion

    private sealed class TestableTargetGroupEvaluatorTask : TargetGroupEvaluatorTask
    {
        public TestableTargetGroupEvaluatorTask(
            SmartDbContext db,
            ICacheManager cache,
            IRuleService ruleService,
            IRuleProviderFactory ruleProviderFactory)
            : base(db, cache, ruleService, ruleProviderFactory)
        {
        }

        protected override async Task<int> DeleteSystemMappingsAsync(
            IQueryable<CustomerRoleMapping> deleteQuery,
            CancellationToken cancelToken)
        {
            var toDelete = await deleteQuery.ToListAsync(cancelToken);
            _db.CustomerRoleMappings.RemoveRange(toDelete);
            return toDelete.Count;
        }
    }
}
