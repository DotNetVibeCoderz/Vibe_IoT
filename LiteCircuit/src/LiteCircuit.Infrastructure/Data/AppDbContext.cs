using LiteCircuit.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LiteCircuit.Infrastructure.Data;

public class AppUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public string AvatarUrl { get; set; } = "";
    public string Bio { get; set; } = "";
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<PcbProject> Projects => Set<PcbProject>();
    public DbSet<DesignCommit> Commits => Set<DesignCommit>();
    public DbSet<DesignComment> Comments => Set<DesignComment>();
    public DbSet<LibComponent> Components => Set<LibComponent>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<PcbProject>(e =>
        {
            e.HasMany(p => p.Commits).WithOne().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(p => p.Comments).WithOne().HasForeignKey(c => c.ProjectId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => p.OwnerId);
        });

        b.Entity<ChatSession>(e =>
        {
            e.HasMany(s => s.Messages).WithOne().HasForeignKey(m => m.SessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(s => s.UserId);
        });

        b.Entity<LibComponent>(e =>
        {
            e.HasIndex(c => c.Category);
            e.HasIndex(c => c.Name);
            e.Property(c => c.PriceUsd).HasPrecision(12, 4);
        });
    }
}
