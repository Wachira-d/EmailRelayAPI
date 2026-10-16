using EmailRelayAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace EmailRelayAPI.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ApiClient> Clients => Set<ApiClient>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<SenderProfile> SenderProfiles => Set<SenderProfile>();
    public DbSet<EmailMessage> Messages => Set<EmailMessage>();
    public DbSet<EmailAttachment> Attachments => Set<EmailAttachment>();
    public DbSet<EmailEvent> EmailEvents => Set<EmailEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();
    public DbSet<BlockEntry> BlockEntries => Set<BlockEntry>();
    public DbSet<ClientSeenIp> ClientSeenIps => Set<ClientSeenIp>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<EmailTemplate> Templates => Set<EmailTemplate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ApiClient>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.DefaultSenderProfile)
             .WithMany()
             .HasForeignKey(x => x.DefaultSenderProfileId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<ApiKey>(e =>
        {
            e.HasIndex(x => x.Prefix).IsUnique();
            e.HasIndex(x => x.ClientId);
            e.HasOne(x => x.Client)
             .WithMany(c => c.ApiKeys)
             .HasForeignKey(x => x.ClientId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<SenderProfile>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<EmailMessage>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.CreatedAt });
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.ContentHash);
            e.HasIndex(x => new { x.ClientId, x.IdempotencyKey })
             .IsUnique()
             .HasFilter("[IdempotencyKey] IS NOT NULL");
            e.HasOne(x => x.Client)
             .WithMany()
             .HasForeignKey(x => x.ClientId)
             .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.SenderProfile)
             .WithMany()
             .HasForeignKey(x => x.SenderProfileId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<EmailAttachment>(e =>
        {
            e.HasIndex(x => x.MessageId);
            e.HasOne(x => x.Message)
             .WithMany(m => m.Attachments)
             .HasForeignKey(x => x.MessageId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<EmailEvent>(e =>
        {
            e.HasIndex(x => x.MessageId);
            e.HasOne(x => x.Message)
             .WithMany(m => m.Events)
             .HasForeignKey(x => x.MessageId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.Category, x.Timestamp });
            e.HasIndex(x => x.ClientId);
            e.HasIndex(x => x.IpAddress);
        });

        b.Entity<RequestLog>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.ClientId, x.Timestamp });
        });

        b.Entity<BlockEntry>(e =>
        {
            e.HasIndex(x => new { x.TargetType, x.Target });
        });

        b.Entity<ClientSeenIp>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.IpAddress }).IsUnique();
        });

        b.Entity<AdminUser>(e =>
        {
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<EmailTemplate>(e =>
        {
            e.HasIndex(x => new { x.ClientId, x.Name });
        });
    }
}
