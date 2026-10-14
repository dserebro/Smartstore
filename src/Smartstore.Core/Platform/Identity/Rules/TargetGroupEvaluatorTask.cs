using System.Diagnostics;
using Smartstore.Caching;
using Smartstore.Core.Data;
using Smartstore.Core.Rules;
using Smartstore.Core.Rules.Filters;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Hooks;
using Smartstore.Scheduling;

namespace Smartstore.Core.Identity.Rules;

/// <remarks>
/// Unlike the legacy task, which unconditionally disabled all save hooks (<c>hooksEnabled: false</c>) while
/// rebuilding system role mappings, this task only suppresses hooks of <see cref="HookImportance.Normal"/>
/// via <c>minHookImportance: HookImportance.Important</c>. Hooks tagged <see cref="ImportantAttribute"/> or
/// <see cref="HookImportance.Essential"/> would still fire. <see cref="DefaultDbHookRegistry"/> selects hooks
/// by <c>HookedType.IsAssignableFrom(entityType)</c>, so several <c>[Important]</c> hooks declared generically
/// over <c>BaseEntity</c> (e.g. <c>StoreContext</c>, <c>MediaTrackerHook</c>, <c>AssignedEntitiesHook</c>,
/// <c>SearchQueryAliasHook</c>) ARE selected and invoked for every <see cref="CustomerRoleMapping"/> save,
/// since it derives from <see cref="BaseEntity"/> — "no hook targets this type" would be the wrong framing.
/// What keeps the divergence moot today is that each of those hooks internally no-ops for
/// <see cref="CustomerRoleMapping"/> (they check the concrete entity type/instance themselves and return
/// <see cref="HookResult.Void"/> otherwise), not that the hook dispatch mechanism skips it. Re-check this
/// remark if a future <c>[Important]</c>/<c>[Essential]</c> hook adds real logic for <see cref="CustomerRoleMapping"/>
/// (or any of its base/interface types) instead of no-op'ing.
/// </remarks>
/// <remarks>
/// The role-filtered delete below (<c>roleIds.Contains(x.CustomerRoleId)</c>) is not just a faithful port of
/// the legacy raw-SQL delete, it fixes a latent bug: legacy bound <c>string.Join(",", roleIds)</c> as a single
/// SQL parameter into <c>In ({0})</c>, which only happened to work for one role id (the only case the admin UI
/// ever triggers), because SQL Server can implicitly convert a one-element numeric string to <c>int</c>. With
/// more than one id the same parameter (e.g. <c>"1,2,3"</c>) fails that conversion and the legacy delete throws
/// instead of deleting the matching rows. The LINQ translation here produces a true parameterized <c>IN</c>
/// clause and is correct for both the single- and multi-id case.
/// </remarks>
public partial class TargetGroupEvaluatorTask(
    SmartDbContext db,
    ICacheManager cache,
    IRuleService ruleService,
    IRuleProviderFactory ruleProviderFactory) : ITask
{
    protected readonly SmartDbContext _db = db;
    protected readonly ICacheManager _cache = cache;
    protected readonly IRuleService _ruleService = ruleService;
    protected readonly ITargetGroupService _targetGroupService = ruleProviderFactory.GetProvider<ITargetGroupService>(RuleScope.Customer);

    public async Task Run(TaskExecutionContext ctx, CancellationToken cancelToken = default)
    {
        var count = 0;
        var numDeleted = 0;
        var numAdded = 0;
        var rolesCount = 0;

        using (var scope = new DbContextScope(_db, autoDetectChanges: false, minHookImportance: HookImportance.Important, deferCommit: true))
        {
            // Delete existing system mappings.
            var deleteQuery = _db.CustomerRoleMappings.Where(x => x.IsSystemMapping);
            if (ctx.Parameters.ContainsKey("CustomerRoleIds"))
            {
                var roleIds = ctx.Parameters["CustomerRoleIds"].ToIntArray();
                deleteQuery = deleteQuery.Where(x => roleIds.Contains(x.CustomerRoleId));
            }

            numDeleted = await deleteQuery.ExecuteDeleteAsync(cancelToken);

            // Insert new customer role mappings.
            var roles = await _db.CustomerRoles
                .Include(x => x.RuleSets)
                .ThenInclude(x => x.Rules)
                .AsNoTracking()
                .AsSplitQuery()
                .Where(x => x.Active && x.RuleSets.Any(y => y.IsActive))
                .ToListAsync(cancelToken);
            rolesCount = roles.Count;

            foreach (var role in roles)
            {
                var ruleSetCustomerIds = new HashSet<int>();

                await ctx.SetProgressAsync(++count, roles.Count, $"Add customer assignments for role \"{role.SystemName.NaIfEmpty()}\".");

                // Execute active rule sets and collect customer ids.
                foreach (var ruleSet in role.RuleSets.Where(x => x.IsActive))
                {
                    if (cancelToken.IsCancellationRequested)
                        return;

                    var expressionGroup = await _ruleService.CreateExpressionGroupAsync(ruleSet, _targetGroupService);
                    if (expressionGroup is FilterExpression expression)
                    {
                        var filterResult = _targetGroupService.ProcessFilter(expression, 0, 500);
                        var resultPager = new FastPager<Customer>(filterResult.SourceQuery, 500);

                        while ((await resultPager.ReadNextPageAsync(x => x.Id, x => x, cancelToken)).Out(out var customerIds))
                        {
                            ruleSetCustomerIds.AddRange(customerIds);
                        }
                    }
                }

                // Add mappings.
                if (ruleSetCustomerIds.Any())
                {
                    foreach (var chunk in ruleSetCustomerIds.Chunk(500))
                    {
                        if (cancelToken.IsCancellationRequested)
                            return;

                        foreach (var customerId in chunk)
                        {
                            _db.CustomerRoleMappings.Add(new CustomerRoleMapping
                            {
                                CustomerId = customerId,
                                CustomerRoleId = role.Id,
                                IsSystemMapping = true
                            });

                            ++numAdded;
                        }

                        await scope.CommitAsync(cancelToken);
                    }

                    try
                    {
                        scope.DbContext.DetachEntities<CustomerRoleMapping>();
                    }
                    catch
                    {
                    }
                }
            }
        }

        if (numAdded > 0 || numDeleted > 0)
        {
            await _cache.RemoveByPatternAsync(AclService.ACL_SEGMENT_PATTERN);
        }

        Debug.WriteLineIf(numDeleted > 0 || numAdded > 0, $"Deleted {numDeleted} and added {numAdded} customer assignments for {rolesCount} roles.");
    }
}