using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.EntityFrameworkCore;

namespace AccessibilityMap.Server.Services;

/// <summary>
/// S3-совместимое хранилище фотографий. Настройки подходят для Cloud.ru
/// Object Storage, но не привязаны к одному провайдеру.
/// </summary>
public sealed class PhotoObjectStorage : IDisposable
{
    private readonly IAmazonS3? _client;
    private readonly string _bucket;
    private readonly ILogger<PhotoObjectStorage> _logger;

    public bool IsConfigured => _client is not null && !string.IsNullOrWhiteSpace(_bucket);

    public PhotoObjectStorage(IConfiguration configuration, ILogger<PhotoObjectStorage> logger)
    {
        _logger = logger;
        var serviceUrl = configuration["S3:ServiceUrl"];
        var accessKey = configuration["S3:AccessKey"];
        var secretKey = configuration["S3:SecretKey"];
        _bucket = configuration["S3:Bucket"] ?? string.Empty;
        var region = configuration["S3:Region"] ?? "ru-central-1";

        if (string.IsNullOrWhiteSpace(serviceUrl) || string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey) || string.IsNullOrWhiteSpace(_bucket))
        {
            logger.LogWarning("S3 не настроен: фотографии временно сохраняются в PostgreSQL/SQLite");
            return;
        }

        var config = new AmazonS3Config
        {
            ServiceURL = serviceUrl.TrimEnd('/'),
            AuthenticationRegion = region,
            ForcePathStyle = configuration.GetValue("S3:ForcePathStyle", true),
            UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        };
        _client = new AmazonS3Client(new BasicAWSCredentials(accessKey, secretKey), config);
    }

    public async Task PutAsync(string fileName, string contentType, byte[] data, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("S3 не настроен");
        await using var stream = new MemoryStream(data, writable: false);
        await _client!.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = ObjectKey(fileName),
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false
        }, cancellationToken);
    }

    public async Task<StoredPhoto?> GetAsync(string fileName, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) return null;
        try
        {
            using var response = await _client!.GetObjectAsync(_bucket, ObjectKey(fileName), cancellationToken);
            await using var output = new MemoryStream();
            await response.ResponseStream.CopyToAsync(output, cancellationToken);
            return new StoredPhoto(output.ToArray(), response.Headers.ContentType ?? "application/octet-stream");
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать {FileName} из S3", fileName);
            return null;
        }
    }

    public async Task<int> MigrateFromDatabaseAsync(Data.AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("S3 не настроен");
        var migrated = 0;
        await foreach (var photo in db.Photos.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (photo.Data.Length == 0) continue;
            try
            {
                await PutAsync(photo.FileName, photo.ContentType, photo.Data, cancellationToken);
                migrated++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось перенести фотографию {FileName} в S3", photo.FileName);
                throw;
            }
        }
        return migrated;
    }

    public async Task<(int Verified, int Failed)> VerifyDatabaseCopiesAsync(
        Data.AppDbContext db, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured) throw new InvalidOperationException("S3 не настроен");
        var verified = 0;
        var failed = 0;
        await foreach (var photo in db.Photos.AsNoTracking().AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            if (photo.Data.Length == 0) continue;
            var stored = await GetAsync(photo.FileName, cancellationToken);
            if (stored is not null &&
                System.Security.Cryptography.SHA256.HashData(photo.Data)
                    .SequenceEqual(System.Security.Cryptography.SHA256.HashData(stored.Data)))
                verified++;
            else
                failed++;
        }
        return (verified, failed);
    }

    private static string ObjectKey(string fileName) => "photos/" + Path.GetFileName(fileName);

    public void Dispose() => _client?.Dispose();
}

public sealed record StoredPhoto(byte[] Data, string ContentType);
