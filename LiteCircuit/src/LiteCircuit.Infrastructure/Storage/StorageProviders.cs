using Amazon.S3;
using Amazon.S3.Model;
using Azure.Storage.Blobs;
using LiteCircuit.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LiteCircuit.Infrastructure.Storage;

/// <summary>Stores files under a local folder served as static files (default: wwwroot/uploads).</summary>
public class FileSystemStorage(string rootPath, string publicBase) : IFileStorage
{
    public async Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        Directory.CreateDirectory(rootPath);
        var safe = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var full = Path.Combine(rootPath, safe);
        await using var fs = File.Create(full);
        await content.CopyToAsync(fs, ct);
        return $"{publicBase.TrimEnd('/')}/{safe}";
    }

    public Task<Stream?> OpenAsync(string fileName, CancellationToken ct = default)
    {
        var full = Path.Combine(rootPath, Path.GetFileName(fileName));
        return Task.FromResult<Stream?>(File.Exists(full) ? File.OpenRead(full) : null);
    }

    public Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        var full = Path.Combine(rootPath, Path.GetFileName(fileName));
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }
}

public class AzureBlobStorage(string connectionString, string container) : IFileStorage
{
    private BlobContainerClient Client
    {
        get
        {
            var c = new BlobContainerClient(connectionString, container);
            c.CreateIfNotExists(Azure.Storage.Blobs.Models.PublicAccessType.Blob);
            return c;
        }
    }

    public async Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var name = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var blob = Client.GetBlobClient(name);
        await blob.UploadAsync(content, new Azure.Storage.Blobs.Models.BlobUploadOptions
        {
            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType },
        }, ct);
        return blob.Uri.ToString();
    }

    public async Task<Stream?> OpenAsync(string fileName, CancellationToken ct = default)
    {
        var blob = Client.GetBlobClient(fileName);
        return await blob.ExistsAsync(ct) ? await blob.OpenReadAsync(cancellationToken: ct) : null;
    }

    public Task DeleteAsync(string fileName, CancellationToken ct = default) =>
        Client.GetBlobClient(fileName).DeleteIfExistsAsync(cancellationToken: ct);
}

/// <summary>Amazon S3 or any S3-compatible endpoint (MinIO). Returns pre-signed GET URLs.</summary>
public class S3Storage : IFileStorage
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public S3Storage(string accessKey, string secretKey, string bucket, string? serviceUrl, string? region)
    {
        _bucket = bucket;
        var cfg = new AmazonS3Config { ForcePathStyle = !string.IsNullOrEmpty(serviceUrl) };
        if (!string.IsNullOrEmpty(serviceUrl)) cfg.ServiceURL = serviceUrl;
        else cfg.RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region ?? "us-east-1");
        _client = new AmazonS3Client(accessKey, secretKey, cfg);
    }

    public async Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        var key = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket, Key = key, InputStream = content, ContentType = contentType,
        }, ct);
        return _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket, Key = key, Expires = DateTime.UtcNow.AddDays(7),
        });
    }

    public async Task<Stream?> OpenAsync(string fileName, CancellationToken ct = default)
    {
        try
        {
            var res = await _client.GetObjectAsync(_bucket, fileName, ct);
            return res.ResponseStream;
        }
        catch (AmazonS3Exception) { return null; }
    }

    public Task DeleteAsync(string fileName, CancellationToken ct = default) =>
        _client.DeleteObjectAsync(_bucket, fileName, ct);
}

public static class StorageSetup
{
    /// <summary>
    /// Registers IFileStorage from configuration:
    /// Storage:Provider = FileSystem | AzureBlob | S3 | MinIO
    /// </summary>
    public static IServiceCollection AddLiteCircuitStorage(this IServiceCollection services, IConfiguration cfg, string contentRoot)
    {
        var provider = (cfg["Storage:Provider"] ?? "FileSystem").Trim();
        switch (provider.ToLowerInvariant())
        {
            case "filesystem":
                var folder = cfg["Storage:FileSystem:Path"] ?? "wwwroot/uploads";
                services.AddSingleton<IFileStorage>(new FileSystemStorage(
                    Path.Combine(contentRoot, folder), cfg["Storage:FileSystem:PublicBase"] ?? "/uploads"));
                break;
            case "azureblob":
                services.AddSingleton<IFileStorage>(new AzureBlobStorage(
                    cfg["Storage:AzureBlob:ConnectionString"] ?? "", cfg["Storage:AzureBlob:Container"] ?? "litecircuit"));
                break;
            case "s3":
            case "minio":
                services.AddSingleton<IFileStorage>(new S3Storage(
                    cfg["Storage:S3:AccessKey"] ?? "", cfg["Storage:S3:SecretKey"] ?? "",
                    cfg["Storage:S3:Bucket"] ?? "litecircuit",
                    cfg["Storage:S3:ServiceUrl"], cfg["Storage:S3:Region"]));
                break;
            default:
                throw new InvalidOperationException(
                    $"Unknown Storage:Provider '{provider}'. Use FileSystem, AzureBlob, S3 or MinIO.");
        }
        return services;
    }
}
