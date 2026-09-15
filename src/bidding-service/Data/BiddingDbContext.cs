// <copyright file="BiddingDbContext.cs" company="Distributed Bidding Auction Platform">
// Copyright (c) Distributed Bidding Auction Platform. Licensed under the MIT license.
// </copyright>
using bidding_service.Domain;
using Microsoft.EntityFrameworkCore;

namespace bidding_service.Data;

/// <summary>
/// Configures EF Core persistence for auctions, bids, and outbox messages.
/// </summary>
public sealed class BiddingDbContext(
    DbContextOptions<BiddingDbContext> options) : DbContext(options)
{
    private const string SaleModeConstraintName = "CK_auctions_sale_mode_buy_now_price";
    private const string SaleModeConstraintSql =
        "(sale_mode = 'AuctionOnly' AND buy_now_price IS NULL) " +
        "OR (sale_mode = 'BuyNowOnly' AND buy_now_price IS NOT NULL) " +
        "OR (sale_mode = 'AuctionAndBuyNow' AND buy_now_price IS NOT NULL " +
        "AND starting_price < buy_now_price)";

    /// <summary>
    /// Gets the auctions.
    /// </summary>
    public DbSet<Auction> Auctions => Set<Auction>();
    /// <summary>
    /// Gets the authoritative tenants.
    /// </summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();
    /// <summary>Gets the durable tenant lifecycle history.</summary>
    public DbSet<TenantStatusTransition> TenantStatusTransitions => Set<TenantStatusTransition>();
    /// <summary>
    /// Gets the registered client applications.
    /// </summary>
    public DbSet<ClientApplication> ClientApplications => Set<ClientApplication>();
    /// <summary>Gets the public credentials registered for client applications.</summary>
    public DbSet<ClientCredential> ClientCredentials => Set<ClientCredential>();
    /// <summary>
    /// Gets or sets the accepted bid history for the auction.
    /// </summary>
    public DbSet<Bid> Bids => Set<Bid>();
    /// <summary>
    /// Gets the outbox messages.
    /// </summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Runs the on model creating operation.
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Auction>(auction =>
        {
            auction.ToTable("auctions", table => table.HasCheckConstraint(SaleModeConstraintName, SaleModeConstraintSql));
            auction.HasKey(a => a.Id);
            auction.Property(a => a.Id).HasColumnName("id");
            auction.Property(a => a.TenantId).HasColumnName("tenant_id").IsRequired();
            auction.Property(a => a.Title)
                .HasColumnName("title")
                .HasMaxLength(200)
                .IsRequired();
            auction.Property(a => a.Description)
                .HasColumnName("description")
                .HasMaxLength(2000)
                .IsRequired();
            auction.Property(a => a.StartingPrice)
                .HasColumnName("starting_price")
                .HasPrecision(18, 2)
                .IsRequired();
            auction.Property(a => a.SaleMode)
                .HasColumnName("sale_mode")
                .HasConversion<string>()
                .HasMaxLength(40)
                .HasDefaultValue(SaleMode.AuctionOnly)
                .IsRequired();
            auction.Property(a => a.BuyNowPrice)
                .HasColumnName("buy_now_price")
                .HasPrecision(18, 2);
            auction.Property(a => a.MinimumBidIncrement)
                .HasColumnName("minimum_bid_increment")
                .HasPrecision(18, 2)
                .IsRequired();
            auction.Property(a => a.CurrentBidAmount)
                .HasColumnName("current_bid_amount")
                .HasPrecision(18, 2);
            auction.Property(a => a.CurrentBidderId)
                .HasColumnName("current_bidder_id")
                .HasMaxLength(120);
            auction.Property(a => a.FinalWinnerId)
                .HasColumnName("final_winner_id")
                .HasMaxLength(120);
            auction.Property(a => a.FinalPrice)
                .HasColumnName("final_price")
                .HasPrecision(18, 2);
            auction.Property(a => a.StartTimeUtc).HasColumnName("start_time_utc").IsRequired();
            auction.Property(a => a.EndTimeUtc).HasColumnName("end_time_utc").IsRequired();
            auction.Property(a => a.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(40)
                .IsRequired();
            auction.Property(a => a.Version)
                .HasColumnName("version")
                .IsConcurrencyToken()
                .IsRequired();
            auction.Property(a => a.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            auction.Property(a => a.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            auction.HasMany(a => a.Bids)
                .WithOne(b => b.Auction)
                .HasForeignKey(b => b.AuctionId)
                .OnDelete(DeleteBehavior.Cascade);
            auction.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(a => a.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
            auction.HasIndex(a => new { a.Status, a.EndTimeUtc })
                .HasDatabaseName("ix_auctions_status_end_time_utc");
        });

        modelBuilder.Entity<Tenant>(tenant =>
        {
            tenant.ToTable("tenants", table => table.HasCheckConstraint(
                "CK_tenants_status",
                "status IN ('Active', 'Suspended', 'Disabled')"));
            tenant.HasKey(t => t.Id);
            tenant.Property(t => t.Id).HasColumnName("id");
            tenant.Property(t => t.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();
            tenant.Property(t => t.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            tenant.Property(t => t.Version)
                .HasColumnName("version")
                .HasDefaultValue(1L)
                .IsConcurrencyToken()
                .IsRequired();
            tenant.Property(t => t.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            tenant.Property(t => t.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            tenant.HasMany(t => t.ClientApplications)
                .WithOne()
                .HasForeignKey(application => application.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantStatusTransition>(transition =>
        {
            transition.ToTable("tenant_status_transitions");
            transition.HasKey(item => item.Id);
            transition.Property(item => item.Id).HasColumnName("id");
            transition.Property(item => item.TenantId).HasColumnName("tenant_id").IsRequired();
            transition.Property(item => item.PreviousStatus)
                .HasColumnName("previous_status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            transition.Property(item => item.CurrentStatus)
                .HasColumnName("current_status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            transition.Property(item => item.TenantVersion).HasColumnName("tenant_version").IsRequired();
            transition.Property(item => item.ChangedAtUtc).HasColumnName("changed_at_utc").IsRequired();
            transition.Property(item => item.ChangedBySubject)
                .HasColumnName("changed_by_subject")
                .HasMaxLength(200)
                .IsRequired();
            transition.Property(item => item.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(128)
                .IsRequired();
            transition.HasIndex(item => new { item.TenantId, item.TenantVersion })
                .IsUnique()
                .HasDatabaseName("ux_tenant_status_transitions_tenant_version");
            transition.HasOne<Tenant>()
                .WithMany()
                .HasForeignKey(item => item.TenantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ClientApplication>(application =>
        {
            application.ToTable("client_applications", table => table.HasCheckConstraint(
                "CK_client_applications_status",
                "status IN ('Active', 'Disabled', 'Revoked')"));
            application.HasKey(item => item.Id);
            application.Property(item => item.Id).HasColumnName("id");
            application.Property(item => item.ClientId)
                .HasColumnName("client_id")
                .HasMaxLength(63)
                .IsRequired();
            application.Property(item => item.TenantId).HasColumnName("tenant_id").IsRequired();
            application.Property(item => item.Name)
                .HasColumnName("name")
                .HasMaxLength(200)
                .IsRequired();
            application.Property(item => item.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            application.Property(item => item.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            application.Property(item => item.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            application.HasIndex(item => item.ClientId)
                .IsUnique()
                .HasDatabaseName("ux_client_applications_client_id");
            application.HasIndex(item => item.TenantId)
                .HasDatabaseName("ix_client_applications_tenant_id");
            application.HasMany(item => item.ClientCredentials)
                .WithOne()
                .HasForeignKey(credential => credential.ClientApplicationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ClientCredential>(credential =>
        {
            credential.ToTable("client_credentials", table => table.HasCheckConstraint(
                "CK_client_credentials_status",
                "status IN ('Active', 'Revoked')"));
            credential.HasKey(item => item.Id);
            credential.Property(item => item.Id).HasColumnName("id");
            credential.Property(item => item.ClientApplicationId)
                .HasColumnName("client_application_id")
                .IsRequired();
            credential.Property(item => item.KeyId)
                .HasColumnName("key_id")
                .HasMaxLength(63)
                .IsRequired();
            credential.Property(item => item.PublicKeyPem)
                .HasColumnName("public_key_pem")
                .HasColumnType("text")
                .IsRequired();
            credential.Property(item => item.PublicKeyFingerprint)
                .HasColumnName("public_key_fingerprint")
                .HasMaxLength(64)
                .IsRequired();
            credential.Property(item => item.Status)
                .HasColumnName("status")
                .HasConversion<string>()
                .HasMaxLength(20)
                .IsRequired();
            credential.Property(item => item.ValidFromUtc).HasColumnName("valid_from_utc").IsRequired();
            credential.Property(item => item.ExpiresAtUtc).HasColumnName("expires_at_utc");
            credential.Property(item => item.RevokedAtUtc).HasColumnName("revoked_at_utc");
            credential.Property(item => item.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            credential.Property(item => item.UpdatedAtUtc).HasColumnName("updated_at_utc").IsRequired();
            credential.HasIndex(item => item.KeyId)
                .IsUnique()
                .HasDatabaseName("ux_client_credentials_key_id");
            credential.HasIndex(item => item.ClientApplicationId)
                .HasDatabaseName("ix_client_credentials_client_application_id");
            credential.HasIndex(item => item.PublicKeyFingerprint)
                .IsUnique()
                .HasDatabaseName("ux_client_credentials_public_key_fingerprint");
        });

        modelBuilder.Entity<Bid>(bid =>
        {
            bid.ToTable("bids");
            bid.HasKey(b => b.Id);
            bid.Property(b => b.Id).HasColumnName("id");
            bid.Property(b => b.AuctionId).HasColumnName("auction_id");
            bid.Property(b => b.BidderId)
                .HasColumnName("bidder_id")
                .HasMaxLength(120)
                .IsRequired();
            bid.Property(b => b.Amount).HasColumnName("amount").HasPrecision(18, 2).IsRequired();
            bid.Property(b => b.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            bid.HasIndex(b => b.AuctionId).HasDatabaseName("ix_bids_auction_id");
            bid.HasIndex(b => new { b.AuctionId, b.CreatedAtUtc })
                .HasDatabaseName("ix_bids_auction_id_created_at_utc");
        });

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Id).HasColumnName("id");
            outbox.Property(m => m.EventType)
                .HasColumnName("event_type")
                .HasMaxLength(120)
                .IsRequired();
            outbox.Property(m => m.AggregateType)
                .HasColumnName("aggregate_type")
                .HasMaxLength(120)
                .IsRequired();
            outbox.Property(m => m.AggregateId).HasColumnName("aggregate_id").IsRequired();
            outbox.Property(m => m.AggregateVersion)
                .HasColumnName("aggregate_version")
                .IsRequired();
            outbox.Property(m => m.OccurredAtUtc).HasColumnName("occurred_at_utc").IsRequired();
            outbox.Property(m => m.CorrelationId)
                .HasColumnName("correlation_id")
                .HasMaxLength(120);
            outbox.Property(m => m.Payload)
                .HasColumnName("payload")
                .HasColumnType("jsonb")
                .IsRequired();
            outbox.Property(m => m.CreatedAtUtc).HasColumnName("created_at_utc").IsRequired();
            outbox.Property(m => m.PublishedAtUtc).HasColumnName("published_at_utc");
            outbox.Property(m => m.PublishAttempts)
                .HasColumnName("publish_attempts")
                .HasDefaultValue(0)
                .IsRequired();
            outbox.Property(m => m.LastError)
                .HasColumnName("last_error")
                .HasMaxLength(2000);
            outbox.HasIndex(m => new { m.PublishedAtUtc, m.CreatedAtUtc })
                .HasDatabaseName("ix_outbox_messages_published_at_created_at");
            outbox.HasIndex(m => new { m.AggregateId, m.AggregateVersion })
                .HasDatabaseName("ix_outbox_messages_aggregate_id_version");
        });
    }
}
