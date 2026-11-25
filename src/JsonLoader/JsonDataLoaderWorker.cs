using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace JsonLoader;

public sealed class JsonDataLoaderWorker(
    NpgsqlDataSource dataSource,
    IOptions<LoaderOptions> options,
    IHostApplicationLifetime appLifetime,
    ILogger<JsonDataLoaderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var sourceDir = Path.GetFullPath(options.Value.DataSourcePath);
            logger.LogInformation("Starting JSON Data Loader. DataSourcePath: {SourceDir}", sourceDir);

            if (!Directory.Exists(sourceDir))
            {
                logger.LogWarning("Data source directory does not exist: {SourceDir}", sourceDir);
                return;
            }

            var jsonFiles = Directory.GetFiles(sourceDir, "*.json", SearchOption.TopDirectoryOnly);
            logger.LogInformation("Found {Count} JSON file(s) to process.", jsonFiles.Length);

            await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
            logger.LogInformation("Connected to PostgreSQL: {Host}:{Port}/{Database}",
                connection.Host, connection.Port, connection.Database);

            foreach (var filePath in jsonFiles)
            {
                stoppingToken.ThrowIfCancellationRequested();

                var tableName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
                logger.LogInformation("Processing file '{FileName}' -> target table '{TableName}'",
                    Path.GetFileName(filePath), tableName);

                // TODO: 1. JSON 파일 스키마 파싱 및 컬럼 타입 추론
                // TODO: 2. CREATE TABLE IF NOT EXISTS 실행
                // TODO: 3. JSON 데이터 레코드 적재 (INSERT / COPY)
            }

            logger.LogInformation("JSON Data Loader completed successfully.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning("JSON Data Loader was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An unhandled error occurred while loading JSON data.");
            Environment.ExitCode = 1;
        }
        finally
        {
            // CLI 배치 작업 완료 후 Host가 정상 종료되도록 신호 전달
            appLifetime.StopApplication();
        }
    }
}