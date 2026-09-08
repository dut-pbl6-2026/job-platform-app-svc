namespace App.Core.Interfaces;

public interface IFileStorageService
{
    /// <summary>
    /// Validates and saves uploaded CV stream.
    /// Returns the public or local relative URL/path for the stored file.
    /// </summary>
    Task<string> SaveCvAsync(Stream fileStream, string originalFileName, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads stored CV stream, content type, and sanitized filename.
    /// </summary>
    Task<(Stream Stream, string ContentType, string FileName)?> GetCvAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a CV file if needed.
    /// </summary>
    Task<bool> DeleteCvAsync(string fileName, CancellationToken cancellationToken = default);
}
