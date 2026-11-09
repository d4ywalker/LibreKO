using System.Text.Json;
using LibreKO.Common.Domain.Entities;
using LibreKO.Common.Enums;
using Microsoft.Extensions.Logging;

namespace LibreKO.Game.World;

public interface IMaintenanceService
{
    bool IsMaintenanceActive { get; }
    string MaintenanceReason { get; }
    DateTime? MaintenanceStartedAt { get; }
    int? ScheduledDurationMinutes { get; }
    bool IsCountdownRunning { get; }
    int RemainingCountdownSeconds { get; }

    void SetMaintenance(bool active, string reason = "", int? durationMinutes = null);
    void CancelCountdown();
    Task StartCountdownAsync(int countdownSeconds, string reason, int? durationMinutes, Func<string, Task> broadcastNotice, Func<Task> onCountdownFinished);
}

public class MaintenanceService : IMaintenanceService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ILogger<MaintenanceService> _logger;
    private readonly object _lock = new();
    private readonly string _stateFilePath;
    private CancellationTokenSource? _countdownCts;

    public bool IsMaintenanceActive { get; private set; }
    public string MaintenanceReason { get; private set; } = string.Empty;
    public DateTime? MaintenanceStartedAt { get; private set; }
    public int? ScheduledDurationMinutes { get; private set; }

    public bool IsCountdownRunning
    {
        get
        {
            lock (_lock)
                return _countdownCts != null && !_countdownCts.IsCancellationRequested;
        }
    }

    public int RemainingCountdownSeconds { get; private set; }

    public record MaintenanceStateRecord(
        bool IsActive,
        string? Reason,
        DateTime? StartedAt,
        int? DurationMinutes
    );

    public MaintenanceService(ILogger<MaintenanceService> logger, string? stateFilePath = null)
    {
        _logger = logger;
        _stateFilePath = stateFilePath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "maintenance.json");
        LoadPersistentState();
    }

    private void LoadPersistentState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return;

            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<MaintenanceStateRecord>(json, JsonOpts);
            if (state == null || !state.IsActive)
                return;

            if (state.DurationMinutes.HasValue && state.StartedAt.HasValue)
            {
                var expiration = state.StartedAt.Value.AddMinutes(state.DurationMinutes.Value);
                if (DateTime.UtcNow >= expiration)
                {
                    _logger.LogInformation("Persistent maintenance window ({Duration}m, started {StartedAt:u}) has expired. Automatically turning OFF.",
                        state.DurationMinutes.Value, state.StartedAt.Value);
                    TryDeleteStateFile();
                    return;
                }
            }

            IsMaintenanceActive = true;
            MaintenanceReason = state.Reason ?? string.Empty;
            MaintenanceStartedAt = state.StartedAt ?? DateTime.UtcNow;
            ScheduledDurationMinutes = state.DurationMinutes;

            _logger.LogWarning("### [PERSISTENT MAINTENANCE] Server started in MAINTENANCE MODE! Reason: '{Reason}', Started: {StartedAt:u} ###",
                MaintenanceReason, MaintenanceStartedAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persistent maintenance state from {Path}", _stateFilePath);
        }
    }

    private void SavePersistentState(bool active, string reason, DateTime? startedAt, int? durationMinutes)
    {
        try
        {
            if (active)
            {
                var state = new MaintenanceStateRecord(true, reason, startedAt, durationMinutes);
                var json = JsonSerializer.Serialize(state, JsonOpts);
                File.WriteAllText(_stateFilePath, json);
                _logger.LogInformation("Saved persistent maintenance state to {Path}", _stateFilePath);
            }
            else
            {
                TryDeleteStateFile();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist maintenance state to {Path}", _stateFilePath);
        }
    }

    private void TryDeleteStateFile()
    {
        try
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
                _logger.LogInformation("Removed persistent maintenance state file {Path}", _stateFilePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete maintenance state file {Path}", _stateFilePath);
        }
    }

    public void SetMaintenance(bool active, string reason = "", int? durationMinutes = null)
    {
        lock (_lock)
        {
            CancelCountdownInternal();
            IsMaintenanceActive = active;
            MaintenanceReason = active ? reason : string.Empty;
            MaintenanceStartedAt = active ? DateTime.UtcNow : null;
            ScheduledDurationMinutes = active ? durationMinutes : null;

            SavePersistentState(active, MaintenanceReason, MaintenanceStartedAt, ScheduledDurationMinutes);
        }

        if (active)
        {
            _logger.LogWarning("Maintenance mode ENABLED (Persistent). Reason: '{Reason}', Duration: {Duration}m",
                reason, durationMinutes);
        }
        else
        {
            _logger.LogInformation("Maintenance mode DISABLED (Persistent). Server is open to all players.");
        }
    }

    public void CancelCountdown()
    {
        lock (_lock)
        {
            CancelCountdownInternal();
        }
        _logger.LogInformation("Maintenance countdown cancelled.");
    }

    private void CancelCountdownInternal()
    {
        if (_countdownCts != null)
        {
            try
            {
                _countdownCts.Cancel();
                _countdownCts.Dispose();
            }
            catch (Exception)
            {
            }
            _countdownCts = null;
        }
        RemainingCountdownSeconds = 0;
    }

    public async Task StartCountdownAsync(
        int countdownSeconds,
        string reason,
        int? durationMinutes,
        Func<string, Task> broadcastNotice,
        Func<Task> onCountdownFinished)
    {
        CancellationToken token;
        lock (_lock)
        {
            CancelCountdownInternal();
            _countdownCts = new CancellationTokenSource();
            token = _countdownCts.Token;
            RemainingCountdownSeconds = countdownSeconds;
        }

        _logger.LogInformation("Starting maintenance countdown for {Seconds}s. Reason: '{Reason}'", countdownSeconds, reason);

        _ = Task.Run(async () =>
        {
            try
            {
                int remaining = countdownSeconds;
                while (remaining > 0)
                {
                    RemainingCountdownSeconds = remaining;

                    if (remaining == countdownSeconds || remaining % 60 == 0 || remaining == 30 || remaining == 10 || remaining <= 5)
                    {
                        string timeDesc = remaining >= 60
                            ? $"{remaining / 60} minute(s)"
                            : $"{remaining} second(s)";

                        string noticeMsg = string.IsNullOrWhiteSpace(reason)
                            ? $"### [MAINTENANCE] Server maintenance starts in {timeDesc}! Please find a safe spot and log out. ###"
                            : $"### [MAINTENANCE] Server maintenance starts in {timeDesc}! ({reason}) Please log out safely. ###";

                        await broadcastNotice(noticeMsg);
                    }

                    await Task.Delay(1000, token);
                    remaining--;
                }

                RemainingCountdownSeconds = 0;
                lock (_lock)
                {
                    _countdownCts = null;
                }

                await onCountdownFinished();
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Maintenance countdown was aborted.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during maintenance countdown.");
            }
        }, token);

        await Task.CompletedTask;
    }
}
