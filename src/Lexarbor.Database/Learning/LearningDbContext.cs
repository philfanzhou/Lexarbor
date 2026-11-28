using Lexarbor.Learning.Database.Entities;
using Microsoft.EntityFrameworkCore;

namespace Lexarbor.Learning.Database;

public class LearningDbContext : DbContext
{
    public LearningDbContext(DbContextOptions<LearningDbContext> options) : base(options) { }

    public DbSet<LearnerIdentity> Learners => Set<LearnerIdentity>();
    public DbSet<UnitPracticeSession> UnitPracticeSessions => Set<UnitPracticeSession>();
    public DbSet<UnitPracticeItem> UnitPracticeItems => Set<UnitPracticeItem>();
    public DbSet<MemoryCardEntity> MemoryCards => Set<MemoryCardEntity>();
    public DbSet<VocabCardLocationEntity> VocabCardLocations => Set<VocabCardLocationEntity>();
    public DbSet<MemoryReviewLogEntity> MemoryReviewLogs => Set<MemoryReviewLogEntity>();
    public DbSet<VocabPlanEntity> VocabPlans => Set<VocabPlanEntity>();
    public DbSet<VocabPlanChangeEntity> VocabPlanChanges => Set<VocabPlanChangeEntity>();
    public DbSet<VocabPlanDayEntity> VocabPlanDays => Set<VocabPlanDayEntity>();
    public DbSet<VocabIssuedQuestionEntity> VocabIssuedQuestions => Set<VocabIssuedQuestionEntity>();
    public DbSet<VocabAttemptEntity> VocabAttempts => Set<VocabAttemptEntity>();
    public DbSet<VocabNotebookItemEntity> VocabNotebookItems => Set<VocabNotebookItemEntity>();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        int count;
        try { count = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false); }
        catch (DbUpdateException error) when (TouchesMemoryCard(error) && IsMemoryCardWriteConflict(error))
        {
            // Concurrent reviews of the same card: optimistic version check failed
            // or two first ratings raced on the composite primary key.
            ChangeTracker.Clear();
            throw new Lexarbor.Learning.Domain.Services.MemoryCardConflictException();
        }
        catch (DbUpdateException error) when (TouchesVocabPlan(error) && IsVocabPlanWriteConflict(error))
        {
            // Concurrent plan writes: optimistic version check failed, or two
            // creations raced on the partial unique index that keeps at most
            // one Active plan per student.
            ChangeTracker.Clear();
            if (error is DbUpdateConcurrencyException)
            {
                throw new Lexarbor.Learning.Domain.Services.VocabPlanVersionConflictException();
            }
            throw new Lexarbor.Learning.Domain.Services.VocabPlanActiveExistsException();
        }
        catch (DbUpdateException error) when (TouchesVocabPlanDay(error) && IsWriteConflict(error))
        {
            // Concurrent plan-day writes: optimistic version check failed, or
            // two requests raced to materialize the same (plan, local date).
            ChangeTracker.Clear();
            throw new Lexarbor.Learning.Domain.Services.VocabPlanDayConflictException();
        }
        catch (DbUpdateException error) when (TouchesVocabAttempt(error) && IsWriteConflict(error))
        {
            // Two concurrent answers to the same question raced on the attempt
            // primary key: the first submission wins, the loser rereads it.
            ChangeTracker.Clear();
            throw new Lexarbor.Learning.Domain.Services.VocabAttemptDuplicateException();
        }
        return count;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<UnitPracticeSession>().HasOne<LearnerIdentity>().WithMany()
            .HasForeignKey(s => s.StudentId).OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UnitPracticeItem>().HasOne<UnitPracticeSession>().WithMany()
            .HasForeignKey(i => i.SessionId).HasPrincipalKey(s => s.SessionId).OnDelete(DeleteBehavior.Cascade);

        // The table and its foreign key to the card come from CardLocationV6.sql.
        modelBuilder.Entity<VocabCardLocationEntity>(entity => entity.HasKey(e => new { e.StudentId, e.WordKey, e.MeaningKey }));

        modelBuilder.Entity<MemoryCardEntity>(entity =>
        {
            entity.HasKey(e => new { e.StudentId, e.WordKey, e.MeaningKey });
            entity.HasIndex(e => new { e.StudentId, e.DueAt });
            entity.Property(e => e.State).HasColumnType("smallint");
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasOne(e => e.Student)
                  .WithMany()
                  .HasForeignKey(e => e.StudentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryReviewLogEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Rating).HasColumnType("smallint");
            entity.Property(e => e.StateBefore).HasColumnType("smallint");
            entity.Property(e => e.StateAfter).HasColumnType("smallint");
            entity.HasIndex(e => new { e.StudentId, e.WordKey, e.MeaningKey });
            entity.HasOne<MemoryCardEntity>()
                  .WithMany()
                  .HasForeignKey(e => new { e.StudentId, e.WordKey, e.MeaningKey })
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabPlanEntity>(entity =>
        {
            entity.HasIndex(e => e.StudentId)
                .IsUnique()
                .HasDatabaseName("IX_vocab_plan_student_id_active")
                .HasFilter("status = 1");
            entity.HasIndex(e => new { e.StudentId, e.Status, e.UpdatedAt });
            entity.Property(e => e.Controller).HasColumnType("smallint");
            entity.Property(e => e.Status).HasColumnType("smallint");
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasOne(e => e.Student)
                  .WithMany()
                  .HasForeignKey(e => e.StudentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabPlanChangeEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ActorType).HasColumnType("smallint");
            entity.HasIndex(e => new { e.PlanId, e.CreatedAt });
            entity.HasOne(e => e.Plan)
                  .WithMany()
                  .HasForeignKey(e => e.PlanId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabNotebookFamilySourceEntity>(entity =>
        {
            entity.HasKey(source => source.Id);
            entity.HasOne(source => source.NotebookItem).WithMany(item => item.FamilySources)
                .HasForeignKey(source => source.NotebookItemId).OnDelete(DeleteBehavior.Cascade)
                .HasConstraintName("FK_vocab_notebook_family_source_item");
            entity.HasIndex(source => new
            {
                source.NotebookItemId,
                source.SourceWordId,
                source.RelationType,
                source.Direction,
                source.TargetBookId,
                source.TargetWordId,
                source.TargetMeaningId,
                source.SourceBookId,
                source.SourceMeaningId
            }).IsUnique().HasDatabaseName("IX_vocab_notebook_family_source_identity");
        });

        modelBuilder.Entity<VocabNotebookItemEntity>(entity =>
        {
            entity.Property(item => item.FamilyOnly).HasDefaultValue(false).HasSentinel(false);
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.StudentId, e.WordKey, e.MeaningKey }).IsUnique();
            entity.HasIndex(e => new { e.StudentId, e.AddedAt });
            entity.HasOne(e => e.Student)
                  .WithMany()
                  .HasForeignKey(e => e.StudentId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabPlanDayEntity>(entity =>
        {
            entity.HasKey(e => new { e.PlanId, e.LocalDate });
            entity.Property(e => e.Version).IsConcurrencyToken();
            entity.HasOne(e => e.Plan)
                  .WithMany()
                  .HasForeignKey(e => e.PlanId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabIssuedQuestionEntity>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasColumnType("smallint");
            entity.Property(e => e.Direction).HasColumnType("smallint");
            entity.HasIndex(e => new { e.StudentId, e.Kind, e.IssuedAt });
            entity.HasIndex(e => new { e.StudentId, e.WordKey, e.Kind });
            entity.HasIndex(e => new { e.PlanId, e.Kind, e.IssuedAt, e.Id })
                  .HasDatabaseName("ix_vocab_issued_question_plan_open")
                  .HasFilter("answered_at IS NULL");
            entity.HasIndex(e => new { e.StudentId, e.Kind, e.IssuedAt, e.Id })
                  .HasDatabaseName("ix_vocab_issued_question_student_open")
                  .HasFilter("answered_at IS NULL");
            entity.HasIndex(e => new { e.StudentId, e.WordKey, e.MeaningKey, e.IssuedAt, e.Id })
                  .HasDatabaseName("ix_vocab_issued_question_student_card")
                  .IsDescending(false, false, false, true, true);
            entity.HasOne(e => e.Student)
                  .WithMany()
                  .HasForeignKey(e => e.StudentId)
                  .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Plan)
                  .WithMany()
                  .HasForeignKey(e => e.PlanId)
                  .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<VocabAttemptEntity>(entity =>
        {
            entity.HasKey(e => e.IssuedQuestionId);
            entity.Property(e => e.Result).HasColumnType("smallint");
            entity.Property(e => e.Rating).HasColumnType("smallint");
            entity.HasOne(e => e.Question)
                  .WithMany()
                  .HasForeignKey(e => e.IssuedQuestionId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static bool TouchesMemoryCard(DbUpdateException error) =>
        error.Entries.Any(entry => entry.Metadata.ClrType == typeof(MemoryCardEntity));

    private static bool IsMemoryCardWriteConflict(DbUpdateException error) =>
        error is DbUpdateConcurrencyException ||
        error.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static bool TouchesVocabPlan(DbUpdateException error) =>
        error.Entries.Any(entry => entry.Metadata.ClrType == typeof(VocabPlanEntity));

    private static bool IsVocabPlanWriteConflict(DbUpdateException error) =>
        error is DbUpdateConcurrencyException ||
        error.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private static bool TouchesVocabPlanDay(DbUpdateException error) =>
        error.Entries.Any(entry => entry.Metadata.ClrType == typeof(VocabPlanDayEntity));

    private static bool TouchesVocabAttempt(DbUpdateException error) =>
        error.Entries.Any(entry => entry.Metadata.ClrType == typeof(VocabAttemptEntity));

    private static bool IsWriteConflict(DbUpdateException error) =>
        error is DbUpdateConcurrencyException ||
        error.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}
