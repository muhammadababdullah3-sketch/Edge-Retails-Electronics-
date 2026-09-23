using System.Text;
using EdgeRetails.Application.Production.Licensing;

namespace EdgeRetails.Infrastructure.Production.Licensing;

public sealed class FileLicenseStore : ILicenseStore
{
    private const long MaximumLicenseBytes = 1024 * 1024;
    private readonly string _path;

    public FileLicenseStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("License path is required.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(_path));
    }

    public async Task<string?> ReadRawAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var info = new FileInfo(_path);
        if (info.Length <= 0 || info.Length > MaximumLicenseBytes)
        {
            throw new InvalidDataException("Installed license artifact size is invalid.");
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: false);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signedLicenseJson))
        {
            throw new ArgumentException("Signed license content is required.", nameof(signedLicenseJson));
        }

        var bytes = Encoding.UTF8.GetBytes(signedLicenseJson);
        if (bytes.LongLength > MaximumLicenseBytes)
        {
            throw new InvalidDataException("Signed license artifact is unexpectedly large.");
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".initial.tmp";
        try
        {
            await using (var stream = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temp, _path, overwrite: false);
                return true;
            }
            catch (IOException) when (File.Exists(_path))
            {
                return false;
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
            }

            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    public async Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(signedLicenseJson))
        {
            throw new ArgumentException("Signed license content is required.", nameof(signedLicenseJson));
        }

        var bytes = Encoding.UTF8.GetBytes(signedLicenseJson);
        if (bytes.LongLength > MaximumLicenseBytes)
        {
            throw new InvalidDataException("Signed license artifact is unexpectedly large.");
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
            {
                File.Replace(temp, _path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _path, overwrite: false);
            }
        }
        finally
        {
            try { if (File.Exists(temp)) { File.Delete(temp); } } catch { }
            Array.Clear(bytes, 0, bytes.Length);
        }
    }
}
