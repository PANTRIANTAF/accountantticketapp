using AccountantApp.Api.Shared.Auth;

namespace AccountantApp.Api.Shared.Authorization;

public interface ICustomerScoped
{
    Guid CustomerId { get; }
}

/// <summary>
/// The Customer aggregate root itself, which is in scope by its own primary key rather than by a
/// foreign key to one. Kept separate from <see cref="ICustomerScoped"/> because a root's
/// CustomerId can only be a computed property (=&gt; Id), which EF ignores and therefore cannot
/// translate into SQL.
/// </summary>
public interface ICustomerRoot
{
    Guid Id { get; }
}

public static class CustomerScope
{
    public static IQueryable<T> WhereInCustomerScope<T>(this IQueryable<T> query, CurrentUser user)
        where T : ICustomerScoped =>
        user.Role is UserRole.AccountantAdmin or UserRole.AccountantUser
            ? query
            : query.Where(entity => entity.CustomerId == user.CustomerId!.Value);

    // Deliberately not an overload of WhereInCustomerScope: the two filter on different columns,
    // and an entity that implemented both interfaces would bind to one of them silently by
    // overload resolution, with no reader able to tell which ran.
    //
    // This lives in Shared, next to the filter above, and not in the Customers slice. Both
    // implementations of the LOCKED tenant boundary have to be in one file or the invariant that
    // makes it auditable -- fix the filter once and every slice is fixed -- is inverted into "find
    // every per-slice copy". See correction note Customers C-1.
    public static IQueryable<T> WhereMatchesCustomerScope<T>(this IQueryable<T> query, CurrentUser user)
        where T : ICustomerRoot =>
        user.Role is UserRole.AccountantAdmin or UserRole.AccountantUser
            ? query
            : query.Where(entity => entity.Id == user.CustomerId!.Value);
}