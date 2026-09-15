using Microsoft.EntityFrameworkCore;
using VoiceWink.Models.Entities;

namespace VoiceWink.Services.Data;

/// <summary>
/// EF Core context for VoiceWink — SQLite at %LOCALAPPDATA%/VoiceWink/voicewink.db.
/// </summary>
public class VoiceWinkDbContext : DbContext
{
    public DbSet<WordReplacement> WordReplacements => Set<WordReplacement>();
    public DbSet<TranscriptionRecord> TranscriptionRecords => Set<TranscriptionRecord>();
    public DbSet<VocabularyWord> VocabularyWords => Set<VocabularyWord>();

    private readonly string _dbPath;

    public VoiceWinkDbContext()
    {
        Helpers.AppPaths.EnsureRoot();
        _dbPath = Helpers.AppPaths.DatabaseFile;
    }

    public VoiceWinkDbContext(DbContextOptions<VoiceWinkDbContext> options) : base(options)
    {
        _dbPath = string.Empty; // Options-based constructor uses configured connection
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WordReplacement>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.OriginalText).IsRequired();
            entity.Property(e => e.ReplacementText).IsRequired();
        });

        modelBuilder.Entity<TranscriptionRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Text).IsRequired();
            entity.HasIndex(e => e.Timestamp);
        });

        modelBuilder.Entity<VocabularyWord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Word).IsRequired();
            entity.HasIndex(e => e.Word).IsUnique();
        });
    }
}
