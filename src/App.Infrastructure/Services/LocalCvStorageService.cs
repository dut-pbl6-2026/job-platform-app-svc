using System.Text.RegularExpressions;
using App.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure.Services;

public class LocalCvStorageService : IFileStorageService
{
    public const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5MB per SRS NFR
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf",
        ".doc",
        ".docx"
    };

    private static readonly Dictionary<string, string> ExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
    };

    private static readonly Dictionary<string, List<byte[]>> FileSignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new() { new byte[] { 0x25, 0x50, 0x44, 0x46 } }, // %PDF
        [".doc"] = new() { new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 } },
        [".docx"] = new() { new byte[] { 0x50, 0x4B, 0x03, 0x04 } } // PK..
    };

    private readonly string _storageDirectory;
    private readonly ILogger<LocalCvStorageService> _logger;

    public LocalCvStorageService(IConfiguration config, ILogger<LocalCvStorageService> logger)
    {
        _logger = logger;
        _storageDirectory = config["APP_CV_STORAGE_PATH"]
                            ?? config["Storage:CvPath"]
                            ?? Path.Combine(Directory.GetCurrentDirectory(), "storage", "cvs");

        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
            _logger.LogInformation("Created CV storage directory at: {Path}", _storageDirectory);
        }
    }

    public async Task<string> SaveCvAsync(
        Stream fileStream,
        string originalFileName,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        if (fileStream == null || fileStream.Length == 0)
            throw new ArgumentException("CV file stream cannot be null or empty.", nameof(fileStream));

        if (fileStream.Length > MaxFileSizeBytes)
            throw new ArgumentException($"CV file exceeds maximum permitted size of {MaxFileSizeBytes / (1024 * 1024)}MB.", nameof(fileStream));

        var extension = Path.GetExtension(originalFileName);
        if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension))
        {
            throw new ArgumentException(
                $"File extension '{extension}' is not allowed. Permitted formats: {string.Join(", ", AllowedExtensions)}.",
                nameof(originalFileName));
        }

        ValidateFileSignature(fileStream, extension);

        // Sanitize original file name: keep only alphanumeric, dots, dashes, and underscores
        var safeBaseName = Path.GetFileNameWithoutExtension(originalFileName);
        safeBaseName = Regex.Replace(safeBaseName, @"[^a-zA-Z0-9_\-]", "_");
        if (safeBaseName.Length > 50)
            safeBaseName = safeBaseName.Substring(0, 50);

        var uniqueFileName = $"{Guid.NewGuid()}_{safeBaseName}{extension.ToLowerInvariant()}";
        var destinationPath = Path.Combine(_storageDirectory, uniqueFileName);

        // Security check: ensure path does not escape storage directory
        var fullPath = Path.GetFullPath(destinationPath);
        if (!fullPath.StartsWith(Path.GetFullPath(_storageDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Invalid path traversal detected in file name.");
        }

        using (var outputStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await fileStream.CopyToAsync(outputStream, cancellationToken);
        }

        _logger.LogInformation("Successfully stored CV file '{FileName}' ({Size} bytes)", uniqueFileName, fileStream.Length);

        // Return relative API URL for accessing this CV
        return $"/api/applications/cv/{uniqueFileName}";
    }

    public Task<(Stream Stream, string ContentType, string FileName)?> GetCvAsync(
        string fileName,
        CancellationToken cancellationToken = default)
    {
        // Sanitize fileName against path traversal
        var sanitized = Path.GetFileName(fileName);
        var fullPath = Path.Combine(_storageDirectory, sanitized);

        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Requested CV file '{FileName}' was not found at '{Path}'", fileName, fullPath);
            return Task.FromResult<(Stream Stream, string ContentType, string FileName)?>(null);
        }

        var ext = Path.GetExtension(sanitized);
        var mime = ExtensionToMime.TryGetValue(ext, out var contentType)
            ? contentType
            : "application/octet-stream";

        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Task.FromResult<(Stream Stream, string ContentType, string FileName)?>((stream, mime, sanitized));
    }

    public Task<bool> DeleteCvAsync(string fileName, CancellationToken cancellationToken = default)
    {
        var sanitized = Path.GetFileName(fileName);
        var fullPath = Path.Combine(_storageDirectory, sanitized);

        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
            _logger.LogInformation("Deleted CV file '{FileName}'", fileName);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private static void ValidateFileSignature(Stream stream, string extension)
    {
        if (!FileSignatures.TryGetValue(extension, out var validSignatures))
            return;

        if (!stream.CanSeek)
            return;

        var maxHeaderLength = validSignatures.Max(s => s.Length);
        var buffer = new byte[maxHeaderLength];
        var originalPos = stream.Position;

        int bytesRead = stream.Read(buffer, 0, maxHeaderLength);
        stream.Position = originalPos;

        if (bytesRead < 4)
        {
            throw new ArgumentException("CV file is too small to be a valid document.", nameof(stream));
        }

        var matches = validSignatures.Any(sig =>
            bytesRead >= sig.Length && buffer.Take(sig.Length).SequenceEqual(sig));

        if (!matches)
        {
            throw new ArgumentException(
                $"File content signature does not match permitted format for extension '{extension}'.",
                nameof(stream));
        }
    }
}
