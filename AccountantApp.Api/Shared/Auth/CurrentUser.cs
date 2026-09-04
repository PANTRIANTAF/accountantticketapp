namespace AccountantApp.Api.Shared.Auth;

public record CurrentUser(string Id, UserRole Role, Guid? CustomerId = null);
