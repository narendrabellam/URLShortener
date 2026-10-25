using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace LinkForge;

public sealed class LinkForgeDbContext(DbContextOptions<LinkForgeDbContext> options) : DbContext(options)
{
    public DbSet<ShortUrlEntity> ShortUrls => Set<ShortUrlEntity>();
    public DbSet<ShortUrlReferrerEntity> ShortUrlReferrers => Set<ShortUrlReferrerEntity>();
    public DbSet<WorkflowRunEntity> WorkflowRuns => Set<WorkflowRunEntity>();
    public DbSet<WorkflowAuditEntity> WorkflowAudit => Set<WorkflowAuditEntity>();
    public DbSet<AgentCallEntity> AgentCalls => Set<AgentCallEntity>();
    public DbSet<ArtifactEntity> Artifacts => Set<ArtifactEntity>();
    public DbSet<ValidationEntity> Validations => Set<ValidationEntity>();
    public DbSet<ApprovalEntity> Approvals => Set<ApprovalEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot order or compare DateTimeOffset natively; UTC ticks keep ordering correct.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<NullableUtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ShortUrlEntity>(entity =>
        {
            entity.ToTable("ShortUrls");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Code).HasMaxLength(32).IsRequired();
            entity.Property(value => value.Destination).HasMaxLength(2048).IsRequired();
            entity.HasIndex(value => value.Code).IsUnique();
        });
        modelBuilder.Entity<ShortUrlReferrerEntity>(entity =>
        {
            entity.ToTable("ShortUrlReferrers");
            entity.HasKey(value => new { value.ShortUrlId, value.Referrer });
            entity.Property(value => value.Referrer).HasMaxLength(253).IsRequired();
            entity.HasOne(value => value.ShortUrl).WithMany(value => value.Referrers)
                .HasForeignKey(value => value.ShortUrlId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<WorkflowRunEntity>(entity =>
        {
            entity.ToTable("WorkflowRuns");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasMaxLength(32);
            entity.Property(value => value.ScenarioId).HasMaxLength(32).IsRequired();
            entity.Property(value => value.Status).HasMaxLength(32).IsRequired();
            entity.Property(value => value.Payload).IsRequired();
            entity.HasIndex(value => value.Status);
        });
        modelBuilder.Entity<WorkflowAuditEntity>(entity =>
        {
            entity.ToTable("WorkflowAudit");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.RunId).HasMaxLength(32).IsRequired();
            entity.Property(value => value.Event).HasMaxLength(80).IsRequired();
            entity.Property(value => value.Details).IsRequired();
            entity.Property(value => value.PreviousHash).HasMaxLength(64);
            entity.Property(value => value.Hash).HasMaxLength(64).IsRequired();
            entity.HasIndex(value => new { value.RunId, value.Sequence }).IsUnique();
            entity.HasOne<WorkflowRunEntity>().WithMany().HasForeignKey(value => value.RunId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        ConfigureRunChild<AgentCallEntity>(modelBuilder, "AgentCalls");
        ConfigureRunChild<ArtifactEntity>(modelBuilder, "Artifacts");
        ConfigureRunChild<ValidationEntity>(modelBuilder, "Validations");
        ConfigureRunChild<ApprovalEntity>(modelBuilder, "Approvals");
    }

    private static void ConfigureRunChild<T>(ModelBuilder modelBuilder, string table) where T : class
    {
        modelBuilder.Entity<T>(entity =>
        {
            entity.ToTable(table);
            entity.HasKey("Id");
            entity.Property<string>("RunId").HasMaxLength(32).IsRequired();
            entity.HasIndex("RunId");
            entity.HasOne<WorkflowRunEntity>().WithMany().HasForeignKey("RunId")
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.UtcTicks, ticks => new DateTimeOffset(ticks, TimeSpan.Zero));

    private sealed class NullableUtcTicksConverter() : ValueConverter<DateTimeOffset?, long?>(
        value => value.HasValue ? value.Value.UtcTicks : null,
        ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null);
}
