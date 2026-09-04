using System.Linq.Expressions;
using AccountantApp.Api.Slices.Customers.Application.Dtos;
using AccountantApp.Api.Slices.Customers.Core;

namespace AccountantApp.Api.Slices.Customers.Application;

internal static class CustomerMapper
{
    internal static readonly Expression<Func<Customer, CustomerSummaryDto>> ToSummaryExpression = customer =>
        new CustomerSummaryDto
        {
            Id = customer.Id,
            LegalName = customer.LegalName,
            TradingName = customer.TradingName,
            Status = customer.Status
        };

    internal static CustomerDto ToDto(Customer customer) => new()
    {
        Id = customer.Id,
        LegalName = customer.LegalName,
        TradingName = customer.TradingName,
        TaxNumber = customer.TaxNumber,
        TaxOffice = customer.TaxOffice,
        AddressLine1 = customer.AddressLine1,
        AddressLine2 = customer.AddressLine2,
        AddressCity = customer.AddressCity,
        AddressPostalCode = customer.AddressPostalCode,
        AddressCountry = customer.AddressCountry,
        ContactEmail = customer.ContactEmail,
        ContactPhone = customer.ContactPhone,
        Status = customer.Status,
        OnboardedOn = customer.OnboardedOn,
        CreatedAt = customer.CreatedAt,
        UpdatedAt = customer.UpdatedAt
    };

    internal static CustomerSelfDto ToSelfDto(Customer customer) => new()
    {
        Id = customer.Id,
        LegalName = customer.LegalName,
        TradingName = customer.TradingName,
        AddressLine1 = customer.AddressLine1,
        AddressLine2 = customer.AddressLine2,
        AddressCity = customer.AddressCity,
        AddressPostalCode = customer.AddressPostalCode,
        AddressCountry = customer.AddressCountry,
        ContactEmail = customer.ContactEmail,
        ContactPhone = customer.ContactPhone,
        Status = customer.Status
    };

    internal static object ToAuditSnapshot(Customer customer) => new
    {
        customer.LegalName,
        customer.TradingName,
        customer.TaxNumber,
        customer.TaxOffice,
        customer.AddressLine1,
        customer.AddressLine2,
        customer.AddressCity,
        customer.AddressPostalCode,
        customer.AddressCountry,
        customer.ContactEmail,
        customer.ContactPhone,
        customer.Status,
        customer.OnboardedOn,
        customer.UpdatedAt
    };
}