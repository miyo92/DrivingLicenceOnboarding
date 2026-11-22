using Microsoft.EntityFrameworkCore;
using Onboarding.Core.Applications;

namespace Onboarding.Infrastructure.Persistence;

public sealed class OnboardingDbContext(DbContextOptions<OnboardingDbContext> options) : DbContext(options)
{
    private const string InProgressFilter = "status IN ('Submitted','ValidatingData','CheckingPhoto','RiskAssessment')";

    public DbSet<Application> Applications => Set<Application>();
    public DbSet<HistoryEntry> History => Set<HistoryEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Application>(b =>
        {
            b.ToTable("applications");
            b.HasKey(a => a.Id);
            b.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(a => a.Status).HasColumnName("status").HasConversion<string>().IsRequired();
            b.Property(a => a.CreatedAt).HasColumnName("created_at");
            b.Property(a => a.UpdatedAt).HasColumnName("updated_at");
            b.Ignore(a => a.IsTerminal);

            // Optimistic concurrency on PostgreSQL's system column; no column is created.
            b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").IsRowVersion();

            b.OwnsOne(a => a.Applicant, o =>
            {
                o.Property(p => p.FirstName).HasColumnName("first_name");
                o.Property(p => p.LastName).HasColumnName("last_name");
                o.Property(p => p.DateOfBirth).HasColumnName("date_of_birth");
                o.Property(p => p.NationalId).HasColumnName("national_id");
                o.Property(p => p.Email).HasColumnName("email");
                o.Property(p => p.PhoneNumber).HasColumnName("phone_number");
                o.Property(p => p.Address).HasColumnName("address");
                o.Property(p => p.LicenceCategory).HasColumnName("licence_category");
            });
            b.Navigation(a => a.Applicant).IsRequired();

            b.OwnsOne(a => a.Photo, o =>
            {
                o.Property(p => p.StorageKey).HasColumnName("photo_storage_key").IsRequired();
                o.Property(p => p.ContentType).HasColumnName("photo_content_type").IsRequired();
                o.Property(p => p.SizeBytes).HasColumnName("photo_size_bytes");
                o.Property(p => p.QualityScore).HasColumnName("photo_quality_score");
            });

            b.HasMany(a => a.History).WithOne().HasForeignKey("ApplicationId").OnDelete(DeleteBehavior.Cascade);
            b.Navigation(a => a.History).HasField("_history").UsePropertyAccessMode(PropertyAccessMode.Field);

            b.HasIndex(a => a.UpdatedAt)
                .HasDatabaseName("ix_applications_in_progress_updated_at")
                .HasFilter(InProgressFilter);
        });

        modelBuilder.Entity<HistoryEntry>(b =>
        {
            b.ToTable("application_history");
            b.HasKey(h => h.Id);
            b.Property(h => h.Id).HasColumnName("id").UseIdentityAlwaysColumn();
            b.Property<Guid>("ApplicationId").HasColumnName("application_id");
            b.Property(h => h.OccurredAt).HasColumnName("occurred_at");
            b.Property(h => h.FromStatus).HasColumnName("from_status").HasConversion<string>();
            b.Property(h => h.ToStatus).HasColumnName("to_status").HasConversion<string>().IsRequired();
            b.Property(h => h.Event).HasColumnName("event").HasConversion<string>().IsRequired();
            b.Property(h => h.Details).HasColumnName("details");

            b.HasIndex("ApplicationId", nameof(HistoryEntry.OccurredAt), nameof(HistoryEntry.Id))
                .HasDatabaseName("ix_application_history_application_id_occurred_at");
        });

        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(m => m.Id);
            b.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(m => m.ApplicationId).HasColumnName("application_id");
            b.Property(m => m.Type).HasColumnName("type").IsRequired();
            b.Property(m => m.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();
            b.Property(m => m.CreatedAt).HasColumnName("created_at");
            b.Property(m => m.SentAt).HasColumnName("sent_at");
            b.Property(m => m.PublishAttempts).HasColumnName("publish_attempts").HasDefaultValue(0);
            b.Property(m => m.LastError).HasColumnName("last_error");

            b.HasIndex(m => m.CreatedAt)
                .HasDatabaseName("ix_outbox_messages_unsent")
                .HasFilter("sent_at IS NULL");
            b.HasIndex(m => m.ApplicationId)
                .HasDatabaseName("ix_outbox_messages_unsent_application_id")
                .HasFilter("sent_at IS NULL");
        });
    }
}
