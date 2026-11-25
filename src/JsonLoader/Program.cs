using JsonLoader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

// User Secrets에서 민감한 설정(ConnectionStrings:Postgres 등)을 읽어오도록 추가
builder.Configuration.AddUserSecrets<Program>(optional: true);

// appsettings.json 설정 바인딩
builder.Services.Configure<LoaderOptions>(
    builder.Configuration.GetSection(LoaderOptions.SectionName));

// Postgres DataSource 및 BackgroundService(Worker) 등록
var connectionString = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'Postgres' not found. Set it via User Secrets: " +
        "dotnet user-secrets set \"ConnectionStrings:Postgres\" \"Host=...\"");
}

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddHostedService<JsonDataLoaderWorker>();

using var host = builder.Build();
await host.RunAsync();

