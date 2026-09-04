using AccountantApp.Api.Slices.Customers.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AccountantApp.Api.Slices.Customers.Infrastructure.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(customer => customer.Id);
        builder.Property(customer => customer.Id).HasColumnName("id");
        builder.Property(customer => customer.LegalName).HasColumnName("legal_name").HasMaxLength(300).IsRequired();
        builder.Property(customer => customer.TradingName).HasColumnName("trading_name").HasMaxLength(300);
        builder.Property(customer => customer.TaxNumber).HasColumnName("tax_number").HasMaxLength(50).IsRequired();
        builder.Property(customer => customer.TaxOffice).HasColumnName("tax_office").HasMaxLength(200);
        builder.Property(customer => customer.AddressLine1).HasColumnName("address_line1").HasMaxLength(200).IsRequired();
        builder.Property(customer => customer.AddressLine2).HasColumnName("address_line2").HasMaxLength(200);
        builder.Property(customer => customer.AddressCity).HasColumnName("address_city").HasMaxLength(100).IsRequired();
        builder.Property(customer => customer.AddressPostalCode).HasColumnName("address_postal_code").HasMaxLength(20).IsRequired();
        builder.Property(customer => customer.AddressCountry).HasColumnName("address_country").HasMaxLength(100).IsRequired();
        builder.Property(customer => customer.ContactEmail).HasColumnName("contact_email").HasMaxLength(320).IsRequired();
        builder.Property(customer => customer.ContactPhone).HasColumnName("contact_phone").HasMaxLength(40).IsRequired();
        builder.Property(customer => customer.Status).HasColumnName("status").HasMaxLength(20).IsRequired();
        builder.Property(customer => customer.OnboardedOn).HasColumnName("onboarded_on").HasColumnType("date");
        builder.Property(customer => customer.CreatedAt).HasColumnName("created_at");
        builder.Property(customer => customer.UpdatedAt).HasColumnName("updated_at");
        builder.Ignore(customer => customer.CustomerId);
        builder.HasIndex(customer => customer.TaxNumber).IsUnique().HasDatabaseName("uq_customers_tax_number");
        builder.HasIndex(customer => new { customer.LegalName, customer.Id }).HasDatabaseName("idx_customers_legal_name");
    }
}