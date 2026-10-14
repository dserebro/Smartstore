using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Smartstore.Core.Identity;
using Smartstore.Core.Rules;
using Smartstore.Core.Security;

namespace Smartstore.Core.Tests.Identity.Rules;

[TestFixture]
public class TargetGroupEvaluatorTaskTests : TargetGroupEvaluatorTaskTestsBase
{
    private static RuleSetEntity CreateTaxExemptRuleSet(bool isActive)
    {
        var ruleSet = new RuleSetEntity { Scope = RuleScope.Customer, IsActive = isActive };
        ruleSet.Rules.Add(new RuleEntity { RuleType = "TaxExempt", Operator = "=", Value = "true" });
        return ruleSet;
    }

    [Test]
    public async Task Run_DeletesOnlySystemMappings_WhenNoCustomerRoleIdsParameterGiven()
    {
        using var db = CreateSmartDbContext();

        // Role without any active rule set, so it is never a candidate for new assignments:
        // this isolates the deletion behavior from the insertion behavior.
        var role = new CustomerRole { Name = "RoleWithoutActiveRuleSet", Active = true };
        db.CustomerRoles.Add(role);

        var customer = new Customer { Username = "cust1" };
        db.Customers.Add(customer);

        await db.SaveChangesAsync();

        db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role.Id, IsSystemMapping = true });
        db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role.Id, IsSystemMapping = false });
        await db.SaveChangesAsync();

        await RunTaskAsync(db);

        var remaining = await db.CustomerRoleMappings.AsNoTracking().ToListAsync();

        Assert.That(remaining, Has.Count.EqualTo(1), "Only the non-system mapping should survive.");
        Assert.That(remaining[0].IsSystemMapping, Is.False);
    }

    [Test]
    public async Task Run_DeletesOnlyScopedSystemMappings_WhenCustomerRoleIdsParameterGiven()
    {
        using var db = CreateSmartDbContext();

        var roleA = new CustomerRole { Name = "RoleA", Active = true };
        var roleB = new CustomerRole { Name = "RoleB", Active = true };
        db.CustomerRoles.AddRange(roleA, roleB);

        var customer = new Customer { Username = "cust1" };
        db.Customers.Add(customer);

        await db.SaveChangesAsync();

        db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = roleA.Id, IsSystemMapping = true });
        db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = roleA.Id, IsSystemMapping = false });
        db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = roleB.Id, IsSystemMapping = true });
        await db.SaveChangesAsync();

        await RunTaskAsync(db, new Dictionary<string, string>
        {
            ["CustomerRoleIds"] = roleA.Id.ToString()
        });

        var remaining = await db.CustomerRoleMappings.AsNoTracking().ToListAsync();

        Assert.That(remaining, Has.Count.EqualTo(2),
            "Role A's system mapping must be deleted; Role A's non-system mapping and Role B's (out of scope) system mapping must remain.");
        Assert.That(remaining.Any(x => x.CustomerRoleId == roleA.Id && !x.IsSystemMapping), Is.True);
        Assert.That(remaining.Any(x => x.CustomerRoleId == roleB.Id && x.IsSystemMapping), Is.True);
    }

    [Test]
    public async Task Run_OnlyEvaluatesActiveRoles_WithAtLeastOneActiveRuleSet()
    {
        using var db = CreateSmartDbContext();

        var exemptCustomer = new Customer { Username = "exempt", IsTaxExempt = true };
        var nonExemptCustomer = new Customer { Username = "nonexempt", IsTaxExempt = false };
        db.Customers.AddRange(exemptCustomer, nonExemptCustomer);

        var activeRole = new CustomerRole { Name = "ActiveRole", Active = true };
        activeRole.RuleSets.Add(CreateTaxExemptRuleSet(isActive: true));

        var inactiveRole = new CustomerRole { Name = "InactiveRole", Active = false };
        inactiveRole.RuleSets.Add(CreateTaxExemptRuleSet(isActive: true));

        var roleWithInactiveRuleSet = new CustomerRole { Name = "ActiveRoleInactiveRuleSet", Active = true };
        roleWithInactiveRuleSet.RuleSets.Add(CreateTaxExemptRuleSet(isActive: false));

        db.CustomerRoles.AddRange(activeRole, inactiveRole, roleWithInactiveRuleSet);
        await db.SaveChangesAsync();

        await RunTaskAsync(db);

        var mappings = await db.CustomerRoleMappings.AsNoTracking().ToListAsync();

        Assert.That(mappings.Count(x => x.CustomerRoleId == activeRole.Id), Is.EqualTo(1),
            "Only the exempt customer should be mapped to the active role with an active rule set.");
        Assert.That(mappings.Any(x => x.CustomerRoleId == inactiveRole.Id), Is.False,
            "An inactive role must never be evaluated, even if its rule set is active.");
        Assert.That(mappings.Any(x => x.CustomerRoleId == roleWithInactiveRuleSet.Id), Is.False,
            "An active role whose only rule set is inactive must contribute no mappings.");
    }

    [Test]
    public async Task Run_CommitsInChunksOf500()
    {
        const int customerCount = 750;
        const int expectedChunks = 2; // 500 + 250

        using var seedDb = CreateSmartDbContext();

        var role = new CustomerRole { Name = "BigRole", Active = true };
        role.RuleSets.Add(CreateTaxExemptRuleSet(isActive: true));
        seedDb.CustomerRoles.Add(role);

        for (var i = 0; i < customerCount; i++)
        {
            seedDb.Customers.Add(new Customer { Username = $"customer{i}", IsTaxExempt = true });
        }

        await seedDb.SaveChangesAsync();

        // Use a SEPARATE SmartDbContext for running the task under test, so the interceptor below only
        // counts the commits made by the task itself, not the seeding SaveChangesAsync call above.
        var interceptor = new SaveChangesCountingInterceptor();
        using var sutDb = CreateSmartDbContext(interceptor);

        var (task, _) = CreateSut(sutDb);
        var ctx = CreateExecutionContext();

        await task.Run(ctx, CancellationToken.None);

        Assert.That(interceptor.SaveChangesCount, Is.EqualTo(expectedChunks),
            "750 customers chunked by 500 must result in exactly two real SaveChanges commits (500 + 250); " +
            "DbContextScope must not trigger any additional implicit commit on Dispose.");

        var mappingCount = await seedDb.CustomerRoleMappings.AsNoTracking().CountAsync(x => x.CustomerRoleId == role.Id);
        Assert.That(mappingCount, Is.EqualTo(customerCount));
    }

    [Test]
    public async Task Run_StopsProcessingFurtherRoles_WhenCancelledMidLoop()
    {
        using var db = CreateSmartDbContext();

        var customer1 = new Customer { Username = "cust1", IsTaxExempt = true };
        var customer2 = new Customer { Username = "cust2", IsTaxExempt = true };
        db.Customers.AddRange(customer1, customer2);

        var role1 = new CustomerRole { Name = "Role1", Active = true };
        role1.RuleSets.Add(CreateTaxExemptRuleSet(isActive: true));

        var role2 = new CustomerRole { Name = "Role2", Active = true };
        role2.RuleSets.Add(CreateTaxExemptRuleSet(isActive: true));

        db.CustomerRoles.AddRange(role1, role2);
        await db.SaveChangesAsync();

        var (task, cacheManagerMock) = CreateSut(db);

        using var cts = new CancellationTokenSource();
        var progressCount = 0;

        // Cancel right after the SECOND role's progress is reported, i.e. once the first role has
        // already been fully processed and committed, but before the second role's rule sets run.
        var ctx = CreateExecutionContext(onProgressReported: () =>
        {
            progressCount++;
            if (progressCount == 2)
            {
                cts.Cancel();
            }
        });

        await task.Run(ctx, cts.Token);

        var mappings = await db.CustomerRoleMappings.AsNoTracking().ToListAsync();
        var roleIdsWithMappings = mappings.Select(x => x.CustomerRoleId).Distinct().ToList();

        Assert.That(roleIdsWithMappings, Has.Count.EqualTo(1),
            "Cancellation must stop the loop after the currently-processed role, so exactly one of the " +
            "two roles ends up with mappings (role processing order is not asserted, only that it stopped).");

        // numAdded > 0 for the role that was fully processed before cancellation, yet the cooperative
        // cancellation check makes Run() return before reaching the cache-invalidation step entirely.
        cacheManagerMock.Verify(x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN), Times.Never,
            "Run() returns from inside the loop on cancellation, bypassing the post-loop cache invalidation check.");
    }

    [Test]
    public async Task Run_InvalidatesAclCache_WhenMappingsWereAdded()
    {
        using var db = CreateSmartDbContext();

        var customer = new Customer { Username = "cust1", IsTaxExempt = true };
        db.Customers.Add(customer);

        var role = new CustomerRole { Name = "Role", Active = true };
        role.RuleSets.Add(CreateTaxExemptRuleSet(isActive: true));
        db.CustomerRoles.Add(role);

        await db.SaveChangesAsync();

        var (_, cacheManagerMock) = await RunTaskAsync(db);

        cacheManagerMock.Verify(x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN), Times.Once);
    }

    [Test]
    public async Task Run_InvalidatesAclCache_WhenMappingsWereDeleted()
    {
        using var db = CreateSmartDbContext();

        // Role without any active rule set, so it is never a candidate for new assignments:
        // this isolates the deletion-only branch of the cache-invalidation condition
        // (numDeleted > 0 && numAdded == 0) from the insertion branch.
        var role = new CustomerRole { Name = "RoleWithoutActiveRuleSet", Active = true };
        db.CustomerRoles.Add(role);

        var customer = new Customer { Username = "cust1" };
        db.Customers.Add(customer);

        await db.SaveChangesAsync();

        db.CustomerRoleMappings.Add(new CustomerRoleMapping { CustomerId = customer.Id, CustomerRoleId = role.Id, IsSystemMapping = true });
        await db.SaveChangesAsync();

        var (_, cacheManagerMock) = await RunTaskAsync(db);

        var remaining = await db.CustomerRoleMappings.AsNoTracking().ToListAsync();
        Assert.That(remaining, Is.Empty, "The pre-existing system mapping should have been deleted and nothing re-added.");

        cacheManagerMock.Verify(x => x.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN), Times.Once,
            "Cache invalidation must fire when numDeleted > 0, even though numAdded == 0.");
    }

    [Test]
    public async Task Run_DoesNotInvalidateAclCache_WhenNothingChanged()
    {
        using var db = CreateSmartDbContext();

        // No pre-existing system mappings, and the only customer does not match the rule, so the task
        // neither deletes nor adds anything.
        var nonExemptCustomer = new Customer { Username = "cust1", IsTaxExempt = false };
        db.Customers.Add(nonExemptCustomer);

        var role = new CustomerRole { Name = "Role", Active = true };
        role.RuleSets.Add(CreateTaxExemptRuleSet(isActive: true));
        db.CustomerRoles.Add(role);

        await db.SaveChangesAsync();

        var (_, cacheManagerMock) = await RunTaskAsync(db);

        var mappings = await db.CustomerRoleMappings.AsNoTracking().ToListAsync();
        Assert.That(mappings, Is.Empty, "No customer matches the rule, so no mappings should have been added.");

        cacheManagerMock.Verify(x => x.RemoveByPatternAsync(It.IsAny<string>()), Times.Never);
    }
}
