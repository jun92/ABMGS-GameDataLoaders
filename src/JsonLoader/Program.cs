using JsonLoader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Services.Configure<LoaderOptions>(
    builder.Configuration.GetSection(LoaderOptions.SectionName));
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

