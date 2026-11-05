using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using ValutaBot.App.MiniApp.Data;

namespace ValutaBot.MiniApp.Services;

/// <summary>
/// PostgreSQL Session-Level Advisory Lock for Single-Instance Leader Election.
/// Ensures that during Kubernetes/Railway rolling updates only one instance acts as the active leader.
/// Automatically releases the lock when the database connection is closed or when the host process terminates.
/// </summary>
public sealed class PostgresDistributedLock : IAsyncDisposable
{
    private readonly long _lockKey;
    private readonly string _lockName;
    private NpgsqlConnection? _connection;
    private bool _isHeld;

    public bool IsHeld => _isHeld && (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()) || (_connection != null && _connection.State == System.Data.ConnectionState.Open));
    public string LockName => _lockName;

    public PostgresDistributedLock(string lockName)
    {
        _lockName = lockName ?? throw new ArgumentNullException(nameof(lockName));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(lockName));
        _lockKey = BitConverter.ToInt64(hash, 0);
    }

    /// <summary>
    /// Attempts to acquire the advisory lock non-blockingly.
    /// Returns true if the lock was acquired, false if held by another instance.
    /// </summary>
    public async Task<bool> TryAcquireAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(DbConnectionFactory.GetConnectionString()))
        {
            // Offline / unit test fallback: assume standalone leader
            _isHeld = true;
            return true;
        }

        if (_isHeld && _connection != null && _connection.State == System.Data.ConnectionState.Open)
        {
            return true;
        }

        try
        {
            if (_connection == null || _connection.State != System.Data.ConnectionState.Open)
            {
                if (_connection != null)
                {
                    try { await _connection.DisposeAsync(); } catch { }
                }
                _connection = await DbConnectionFactory.OpenConnectionAsync(ct);
            }

            await using var cmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key);", _connection);
            cmd.Parameters.AddWithValue("key", _lockKey);
            var result = await cmd.ExecuteScalarAsync(ct);
            _isHeld = result is true;

            if (!_isHeld)
            {
                await _connection.DisposeAsync();
                _connection = null;
            }
            return _isHeld;
        }
        catch (Exception ex)
        {
            BotLogger.Warn($"[DistributedLock] Advisory lock check failed for '{_lockName}': {ex.Message}");
            if (_connection != null)
            {
                try { await _connection.DisposeAsync(); } catch { }
                _connection = null;
            }
            _isHeld = false;
            return false;
        }
    }

    /// <summary>
    /// Waits until the lock is acquired or cancellation is requested.
    /// </summary>
    public async Task WaitAcquireAsync(TimeSpan retryInterval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await TryAcquireAsync(ct))
            {
                BotLogger.Info($"[DistributedLock] Leader lock acquired for '{_lockName}'. Active leader elected.");
                return;
            }

            BotLogger.Info($"[DistributedLock] Standby mode: '{_lockName}' is held by another active instance. Retrying in {retryInterval.TotalSeconds:F0}s...");
            await Task.Delay(retryInterval, ct);
        }
    }

    /// <summary>
    /// Releases the advisory lock and disposes the dedicated connection.
    /// </summary>
    public async Task ReleaseAsync()
    {
        bool wasHeld = _isHeld;
        _isHeld = false;

        if (_connection != null)
        {
            try
            {
                if (wasHeld && _connection.State == System.Data.ConnectionState.Open)
                {
                    await using var cmd = new NpgsqlCommand("SELECT pg_advisory_unlock(@key);", _connection);
                    cmd.Parameters.AddWithValue("key", _lockKey);
                    await cmd.ExecuteScalarAsync();
                }
            }
            catch (Exception ex)
            {
                BotLogger.Warn($"[DistributedLock] Advisory lock unlock failed for '{_lockName}': {ex.Message}");
            }
            finally
            {
                try { await _connection.DisposeAsync(); } catch { }
                _connection = null;
                BotLogger.Info($"[DistributedLock] Released leader lock for '{_lockName}'.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ReleaseAsync();
    }
}
