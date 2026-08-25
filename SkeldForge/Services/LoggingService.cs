using System.Text;
using SkeldForge.Models;

namespace SkeldForge.Services;

public enum LogLevel
{
    Debug,
    Information,
    Warning,
    Error
}

/// <summary>Small, dependency-free, daily rolling file logger.</summary>
public sealed class LoggingService : IDisposable
{
    private readonly AppPaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public LoggingService(AppPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string CurrentLogFilePath
        => Path.Combine(_paths.LogsDirectory, $"SkeldForge-{DateTime.Now:yyyy-MM-dd}.log");

    public string LogFilePath => CurrentLogFilePath;

    public void Debug(string message) => Write(LogLevel.Debug, message);
    public void Info(string message) => Write(LogLevel.Information, message);
    public void Warning(string message, Exception? exception = null) => Write(LogLevel.Warning, message, exception);
    public void Error(string message, Exception? exception = null) => Write(LogLevel.Error, message, exception);

    public Task DebugAsync(string message, CancellationToken cancellationToken = default)
        => WriteAsync(LogLevel.Debug, message, null, cancellationToken);

    public Task InfoAsync(string message, CancellationToken cancellationToken = default)
        => WriteAsync(LogLevel.Information, message, null, cancellationToken);

    public Task WarningAsync(string message, Exception? exception = null, CancellationToken cancellationToken = default)
        => WriteAsync(LogLevel.Warning, message, exception, cancellationToken);

    public Task ErrorAsync(string message, Exception? exception = null, CancellationToken cancellationToken = default)
        => WriteAsync(LogLevel.Error, message, exception, cancellationToken);

    public Task LogAsync(
        LogLevel level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
        => WriteAsync(level, message, exception, cancellationToken);

    public void Write(LogLevel level, string message, Exception? exception = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _writeLock.Wait();
        try
        {
            EnsureLogDirectory();
            File.AppendAllText(CurrentLogFilePath, FormatEntry(level, message, exception), new UTF8Encoding(false));
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task WriteAsync(
        LogLevel level,
        string message,
        Exception? exception = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureLogDirectory();
            await File.AppendAllTextAsync(
                CurrentLogFilePath,
                FormatEntry(level, message, exception),
                new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public Task LogStartupAsync(CancellationToken cancellationToken = default)
        => InfoAsync($"SkeldForge v{SkeldForgeVersion.Current} starting. Created by NyxShade.", cancellationToken);

    private void EnsureLogDirectory() => Directory.CreateDirectory(_paths.LogsDirectory);

    private static string FormatEntry(LogLevel level, string message, Exception? exception)
    {
        var safeMessage = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        var builder = new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
            .Append(" [")
            .Append(level.ToString().ToUpperInvariant())
            .Append("] ")
            .Append(safeMessage);

        if (exception is not null)
        {
            builder.Append(" | ").AppendLine().Append(exception);
        }

        return builder.AppendLine().ToString();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }
}
