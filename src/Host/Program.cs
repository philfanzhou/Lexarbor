using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Ruoyu.Study.Vocabulary.Database;
using Ruoyu.Study.Vocabulary.Database.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Repositories;
using Ruoyu.Study.Vocabulary.Domain.Services;
using Ruoyu.Study.Vocabulary.Service;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddGrpc();
var connectionString = builder.Configuration.GetConnectionString("Default");
var isPostgreSql = !string.IsNullOrWhiteSpace(connectionString)
    && (connectionString.Contains("Host=", StringComparison.OrdinalIgnoreCase)
        || connectionString.Contains("Server=", StringComparison.OrdinalIgnoreCase));
builder.Services.AddDbContext<VocabularyDbContext>(options =>
{
    if (isPostgreSql)
        options.UseNpgsql(connectionString);
    else
        options.UseSqlite(connectionString ?? "Data Source=data/sqlite/ruoyu_study_vocabulary.db");
});

builder.Services.AddScoped<IVocabularyRepository, VocabularyRepository>();
builder.Services.AddScoped<IVocabularyBookRepository, VocabularyBookRepository>();
builder.Services.AddScoped<IVocabularyMeaningRepository, VocabularyMeaningRepository>();
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();
builder.Services.AddScoped<VocabularyDomainService>();
builder.Services.AddScoped<VocabularyBookDomainService>();

var app = builder.Build();

app.Logger.LogInformation("Vocabulary Service starting");
app.Logger.LogInformation("Listening: {Urls}", Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "(default)");
if (isPostgreSql && !string.IsNullOrEmpty(connectionString))
{
    var csb = new DbConnectionStringBuilder { ConnectionString = connectionString };
    app.Logger.LogInformation("Database: PostgreSQL {Host}:{Port}/{Database}", csb["Host"], csb.TryGetValue("Port", out var dbPort) ? dbPort : "5432", csb["Database"]);
}
else
{
    app.Logger.LogInformation("Database: SQLite");
}

// Configure database initialization (Code First without Migrations)
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
    var loggerFactory = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
    await DatabaseInitializer.InitializeAsync(dbContext, loggerFactory);
}

// Configure the HTTP request pipeline.
app.MapGrpcService<VocabularyServiceImpl>();
app.MapGrpcService<VocabularyBookServiceImpl>();
app.MapGet("/", () => "Communication with gRPC endpoints must be made through a gRPC client.");

app.Run();