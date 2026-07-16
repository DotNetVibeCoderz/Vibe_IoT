namespace LiteCircuit.Core.Interfaces;

/// <summary>Pluggable blob storage (FileSystem, Azure Blob, S3, MinIO).</summary>
public interface IFileStorage
{
    /// <summary>Saves a file and returns a browser-reachable URL (relative or absolute).</summary>
    Task<string> SaveAsync(Stream content, string fileName, string contentType, CancellationToken ct = default);

    Task<Stream?> OpenAsync(string fileName, CancellationToken ct = default);

    Task DeleteAsync(string fileName, CancellationToken ct = default);
}
