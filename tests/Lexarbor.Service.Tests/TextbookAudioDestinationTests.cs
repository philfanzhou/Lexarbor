using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Audio;
using Lexarbor.Host.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class TextbookAudioDestinationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferenceAndDestinationReservation_OrderTextbookAllocationWithoutCheckThenReserveGap(bool destinationFirst)
    {
        var path = Path.Combine(Path.GetTempPath(), $"textbook-destination-{Guid.NewGuid():N}.db");
        try
        {
            var services = new ServiceCollection(); services.AddDbContext<VocabularyDbContext>(o => o.UseSqlite($"Data Source={path};Pooling=False"));
            services.AddScoped<IAudioRecordExistenceRepository, AudioRecordExistenceRepository>();
            await using var provider = services.BuildServiceProvider();
            var records = new BarrierRecords(new AudioDestinationRecords(provider.GetRequiredService<IServiceScopeFactory>()));
            var options = AudioStorageTests.ValidOptions(); options.Enabled = false;
            var factory = new Mock<IAudioObjectStoreFactory>(); factory.Setup(f => f.Create(It.IsAny<AudioOptions>())).Returns(() => Mock.Of<IAudioObjectStore>());
            using var runtime = new AudioRuntimeConfiguration(Options.Create(options), factory.Object, records);
            await using var scope = provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
            await db.Database.MigrateAsync(Ct); var version = Guid.NewGuid().ToString();
            db.VocabularyBooks.Add(new() { Id = "book", BookName = "Book" });
            db.VocabularyBookUnits.Add(new() { Id = "unit", BookId = "book", Number = 1 });
            db.TextbookAudioTracks.Add(new() { Id = "track", BookId = "book", UnitId = "unit", Title = "Track", Version = version });
            await db.SaveChangesAsync(Ct); db.ChangeTracker.Clear();
            options.Bucket = "new-textbook-bucket";
            using var config = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }))).Build();
            AudioOperationReference operation;
            if (destinationFirst)
            {
                var preparation = runtime.PrepareAsync(config, Ct);
                await records.Checked.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
                var acquiring = runtime.AcquireAsync(Ct); Assert.False(acquiring.IsCompleted);
                records.Finish.TrySetResult(); using var prepared = await preparation; Assert.NotNull(prepared);
                Assert.False(acquiring.IsCompleted); prepared.Publish(); operation = await acquiring;
                Assert.Equal(options.Bucket, operation.Snapshot.Bucket);
            }
            else
            {
                operation = await runtime.AcquireAsync(Ct);
                await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => runtime.PrepareAsync(config, Ct));
                Assert.False(records.Checked.Task.IsCompleted);
            }
            using (operation)
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var request = new TextbookAudioUploadRequest(Guid.NewGuid().ToString(), new string('a', 64), "book", "unit", "track", version, 10,
                    new string('b', 64), 128, operation.Snapshot.Bucket, operation.Snapshot.KeyPrefix);
                var upload = (await new TextbookAudioUploadRepository(db).ReserveAsync(request, "uploader", now, now + 300000, Ct)).Upload!;
                Assert.Equal(operation.Snapshot.Bucket, upload.Asset.Location.Bucket);
                Assert.True(await new AudioRecordExistenceRepository(db).HasAnyAsync(Ct));
            }
            // After the reference ends, the textbook asset/operation still protects its destination.
            options.Bucket = "another-textbook-bucket"; records.Finish.TrySetResult();
            using var next = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = options }))).Build();
            await Assert.ThrowsAsync<ConfigurationChangeConflictException>(() => runtime.PrepareAsync(next, Ct));
            Assert.Empty(await db.AudioUploadOperations.ToListAsync(Ct));
        }
        finally { File.Delete(path); File.Delete(path + "-wal"); File.Delete(path + "-shm"); }
    }

    private sealed class BarrierRecords(IAudioDestinationRecords inner) : IAudioDestinationRecords
    {
        public readonly TaskCompletionSource Checked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> HasAnyAsync(CancellationToken token)
        {
            var result = await inner.HasAnyAsync(token); Checked.TrySetResult(); await Finish.Task.WaitAsync(token); return result;
        }
    }
}
