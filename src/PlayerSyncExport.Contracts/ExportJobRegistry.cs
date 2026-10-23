using System.Collections.Concurrent;

namespace PlayerSyncExport.Contracts;

/// <summary>A character state together with the local files behind its available resources.</summary>
public sealed record CharacterResolution(CharacterStateDto State, IReadOnlyDictionary<string, ResolvedFile> Files);

/// <summary>
/// Export jobs on the provider side: validation of requests, one job at a time, waiting for the
/// synchronization to complete, and verified copies of only the files in the character's mapping.
/// Lives in the shared contracts so that the logic PlayerSync runs is the logic the tests cover.
/// </summary>
public sealed class ExportJobRegistry : IDisposable
{
    public const int MaxRetainedJobs = 10;
    public const int MaxHashesPerJob = 20000;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _slot = new(1, 1);
    private readonly Func<uint, CharacterResolution> _resolve;
    private readonly Func<IEnumerable<string?>> _forbiddenRoots;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Action<Exception, string>? _log;

    /// <param name="resolve">Builds the current state and file map of a character by EntityId.</param>
    /// <param name="forbiddenRoots">Folders exports may never be written into.</param>
    public ExportJobRegistry(Func<uint, CharacterResolution> resolve, Func<IEnumerable<string?>> forbiddenRoots,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null, Action<Exception, string>? log = null)
    {
        _resolve = resolve;
        _forbiddenRoots = forbiddenRoots;
        _delay = delay ?? Task.Delay;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _log = log;
    }

    /// <summary>The task of the most recently started job (for tests and shutdown).</summary>
    public Task LastJobTask { get; private set; } = Task.CompletedTask;

    public ExportStartResultDto Start(ExportRequestDto? request)
    {
        if (_shutdown.IsCancellationRequested) return new() { Error = "PlayerSync is shutting down." };
        if (request == null) return new() { Error = "Empty export request." };
        if (request.ApiVersion != ExportIpc.ApiVersion)
            return new() { Error = $"Exporter speaks export IPC v{request.ApiVersion}, this PlayerSync build provides v{ExportIpc.ApiVersion}." };
        if (request.CacheHashes is not { Count: > 0 }) return new() { Error = "No files were selected." };
        if (request.CacheHashes.Count > MaxHashesPerJob) return new() { Error = "Too many files in one request." };
        if (!request.CacheHashes.TrueForAll(ExportPathPolicy.IsSha1Hex)) return new() { Error = "The request contains an invalid file identifier." };
        if (string.IsNullOrEmpty(request.ExpectedPlayerName)) return new() { Error = "The request does not name the character." };

        var destinationError = ExportPathPolicy.ValidateDestination(request.DestinationDirectory, _forbiddenRoots());
        if (destinationError != null) return new() { Error = destinationError };
        if (!Directory.Exists(request.DestinationDirectory)) return new() { Error = "The destination folder does not exist." };

        if (!_slot.Wait(0)) return new() { Error = "Another export is still running." };

        var job = new Job(Guid.NewGuid().ToString("N"), request, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), _clock());
        _jobs[job.Id] = job;
        Prune();
        LastJobTask = Task.Run(() => RunAsync(job));
        return new() { Accepted = true, JobId = job.Id };
    }

    public ExportJobStatusDto GetStatus(string? jobId)
        => !string.IsNullOrEmpty(jobId) && _jobs.TryGetValue(jobId, out var job) ? job.ToDto() : new() { JobId = jobId ?? string.Empty, Found = false };

    public bool Cancel(string? jobId)
    {
        if (string.IsNullOrEmpty(jobId) || !_jobs.TryGetValue(jobId, out var job) || job.IsFinished) return false;
        job.Cts.Cancel();
        return true;
    }

    private void Prune()
    {
        foreach (var old in _jobs.Values.Where(j => j.IsFinished).OrderByDescending(j => j.Created).Skip(MaxRetainedJobs).ToList())
        {
            if (_jobs.TryRemove(old.Id, out _)) old.Cts.Dispose();
        }
    }

    private async Task RunAsync(Job job)
    {
        var token = job.Cts.Token;
        try
        {
            job.Set(ExportJobPhase.WaitingForSync, "Waiting for PlayerSync to finish synchronizing the character.");
            var deadline = _clock().AddSeconds(Math.Clamp(job.Request.SyncWaitSeconds, 0, 900));
            CharacterResolution resolution;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                resolution = _resolve(job.Request.EntityId);
                var state = resolution.State;
                if (state.Lookup != CharacterLookup.Found)
                {
                    job.Set(ExportJobPhase.Failed, $"The character is no longer available through PlayerSync ({state.Lookup}). It may have left the area, despawned, or ZoneSync may have ended.");
                    return;
                }

                if (!string.Equals(state.PlayerName, job.Request.ExpectedPlayerName, StringComparison.Ordinal))
                {
                    job.Set(ExportJobPhase.Failed, "The game object now belongs to a different character. Reopen the export window for the character you want.");
                    return;
                }

                if (state.SyncState is SyncState.Complete or SyncState.CompleteWithMissing) break;
                if (_clock() > deadline)
                {
                    job.State = state;
                    job.Set(ExportJobPhase.Failed, $"Synchronization did not finish in time (state: {state.SyncState}). Nothing was exported.");
                    return;
                }

                await _delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }

            job.State = resolution.State;
            var files = job.Request.CacheHashes.Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(h => new ExportFileResultDto { CacheHash = h.ToUpperInvariant(), Outcome = FileExportOutcome.Pending }).ToList();
            job.SetFiles(files, files.Sum(f => resolution.Files.TryGetValue(f.CacheHash, out var rf) ? SafeLength(rf.Path) : 0));
            job.Set(ExportJobPhase.Copying, "Copying files.");

            var progress = new InlineProgress(job.AddBytes);
            foreach (var file in files)
            {
                if (token.IsCancellationRequested)
                {
                    file.Outcome = FileExportOutcome.Cancelled;
                    continue;
                }

                if (!resolution.Files.TryGetValue(file.CacheHash, out var source))
                {
                    file.Outcome = FileExportOutcome.NotInCharacterMapping;
                    file.Error = "This file is not part of the character's current resolved mapping.";
                    job.FileDone();
                    continue;
                }

                file.FileName = ExportPathPolicy.CacheFileName(file.CacheHash, source.Extension);
                var result = await VerifiedFileCopier.CopyAsync(source.Path, job.Request.DestinationDirectory, file.FileName,
                    file.CacheHash, progress, token).ConfigureAwait(false);
                file.Outcome = result.Outcome;
                file.Error = result.Error;
                file.Size = result.Size;
                file.Sha1Verified = result.Sha1Verified;
                file.Sha256 = result.Sha256;
                job.FileDone();
            }

            if (token.IsCancellationRequested)
                job.Set(ExportJobPhase.Cancelled, "Export cancelled.");
            else if (files.TrueForAll(f => f.Outcome is FileExportOutcome.Copied or FileExportOutcome.AlreadyPresentIdentical))
                job.Set(ExportJobPhase.Completed, "All selected files were exported and verified.");
            else
                job.Set(ExportJobPhase.CompletedWithIssues, "Some files could not be exported. See the per-file results.");
        }
        catch (OperationCanceledException)
        {
            job.Set(ExportJobPhase.Cancelled, "Export cancelled.");
        }
        catch (Exception ex)
        {
            _log?.Invoke(ex, "Export job failed");
            job.Set(ExportJobPhase.Failed, "Export failed: " + ex.Message);
        }
        finally
        {
            VerifiedFileCopier.CleanupTempFiles(job.Request.DestinationDirectory);
            _slot.Release();
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            LastJobTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // Job errors are reported through its status.
        }
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    private sealed class Job(string id, ExportRequestDto request, CancellationTokenSource cts, DateTimeOffset created)
    {
        private readonly object _lock = new();
        private ExportJobPhase _phase = ExportJobPhase.Queued;
        private string? _message;
        private int _filesDone;
        private long _bytesDone;
        private long _bytesTotal;
        private List<ExportFileResultDto> _files = [];

        public string Id { get; } = id;
        public ExportRequestDto Request { get; } = request;
        public CancellationTokenSource Cts { get; } = cts;
        public DateTimeOffset Created { get; } = created;
        public CharacterStateDto? State { get; set; }

        public bool IsFinished
        {
            get
            {
                lock (_lock) return _phase is ExportJobPhase.Completed or ExportJobPhase.CompletedWithIssues or ExportJobPhase.Failed or ExportJobPhase.Cancelled;
            }
        }

        public void Set(ExportJobPhase phase, string message)
        {
            lock (_lock)
            {
                _phase = phase;
                _message = message;
            }
        }

        public void SetFiles(List<ExportFileResultDto> files, long bytesTotal)
        {
            lock (_lock)
            {
                _files = files;
                _bytesTotal = bytesTotal;
            }
        }

        public void FileDone() => Interlocked.Increment(ref _filesDone);
        public void AddBytes(long bytes) => Interlocked.Add(ref _bytesDone, bytes);

        public ExportJobStatusDto ToDto()
        {
            lock (_lock)
            {
                var finished = _phase is ExportJobPhase.Completed or ExportJobPhase.CompletedWithIssues or ExportJobPhase.Failed or ExportJobPhase.Cancelled;
                return new ExportJobStatusDto
                {
                    JobId = Id,
                    Found = true,
                    Phase = _phase,
                    Message = _message,
                    FilesTotal = _files.Count,
                    FilesDone = Volatile.Read(ref _filesDone),
                    BytesTotal = _bytesTotal,
                    BytesDone = Interlocked.Read(ref _bytesDone),
                    // Per-file results and the state are only handed out once the job is final.
                    Files = finished ? [.. _files] : [],
                    State = finished ? State : null,
                };
            }
        }
    }
}
