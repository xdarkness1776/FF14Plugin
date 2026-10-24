using System.Collections.Concurrent;

namespace PlayerSyncExport.Contracts;

/// <summary>A character state together with the local files behind its available resources.</summary>
public sealed record CharacterResolution(CharacterStateDto State, IReadOnlyDictionary<string, ResolvedFile> Files);

/// <summary>
/// Export jobs on the provider side: validation of requests, one job at a time, waiting for the
/// synchronization to complete, and verified copies of only the files in the character's mapping.
/// Lives in the shared contracts so that the logic PlayerSync runs is the logic the tests cover.
/// <para>
/// Lifecycle: while waiting, every read of the character checks that it is still found, still has the expected name
/// (and home world, when both sides know it) and is not still synchronizing; any of these failing ends the job
/// without copying. Once the copy phase starts, the file list is frozen: the copies continue if the character then
/// leaves, because every file is verified against the frozen mapping's hash, and the manifest is built from that
/// frozen state only. The destination is validated again before every file. A job removes only the temporary files it
/// created, and the one-job slot is always released, also when that cleanup fails.
/// </para>
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
    private readonly Action<string>? _deleteTempFile;

    /// <param name="resolve">Builds the current state and file map of a character by EntityId.</param>
    /// <param name="forbiddenRoots">Folders exports may never be written into.</param>
    /// <param name="deleteTempFile">Deletes one temporary file during cleanup (tests replace it); defaults to <see cref="File.Delete"/>.</param>
    public ExportJobRegistry(Func<uint, CharacterResolution> resolve, Func<IEnumerable<string?>> forbiddenRoots,
        Func<TimeSpan, CancellationToken, Task>? delay = null, Func<DateTimeOffset>? clock = null, Action<Exception, string>? log = null,
        Action<string>? deleteTempFile = null)
    {
        _deleteTempFile = deleteTempFile;
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

        try
        {
            var job = new Job(Guid.NewGuid().ToString("N"), request, CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token), _clock())
            {
                TempFiles = new TempFileLedger(_deleteTempFile),
            };
            _jobs[job.Id] = job;
            Prune();
            LastJobTask = Task.Run(() => RunAsync(job));
            return new() { Accepted = true, JobId = job.Id };
        }
        catch (Exception ex)
        {
            // The job never started, so nothing else will release the slot.
            _slot.Release();
            _log?.Invoke(ex, "Export job could not be started");
            return new() { Error = "The export could not be started: " + ex.Message };
        }
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

                if (job.Request.ExpectedHomeWorld != null && state.HomeWorld != null
                                                          && !string.Equals(state.HomeWorld, job.Request.ExpectedHomeWorld, StringComparison.OrdinalIgnoreCase))
                {
                    job.Set(ExportJobPhase.Failed, $"The game object now belongs to {state.PlayerName} of {state.HomeWorld}, not of {job.Request.ExpectedHomeWorld}. " +
                                                   "Reopen the export window for the character you want.");
                    return;
                }

                if (state.SyncState is SyncState.Complete or SyncState.CompleteWithMissing) break;
                if (_clock() > deadline)
                {
                    job.State = state;
                    job.Set(ExportJobPhase.Failed, state.SyncState == SyncState.Stale
                        ? "PlayerSync has received newer data for this character than it applied, and did not apply it in time. Nothing was exported. " +
                          "The exporter does not reapply data by itself; wait for PlayerSync, or use Reapply PlayerSync Data (which may download files)."
                        : $"Synchronization did not finish in time (state: {state.SyncState}). Nothing was exported.");
                    return;
                }

                await _delay(TimeSpan.FromMilliseconds(500), token).ConfigureAwait(false);
            }

            // From here on the mapping is frozen; the character leaving no longer stops the copies.
            job.State = resolution.State;
            if (job.Request.ExpectedHomeWorld != null && resolution.State.HomeWorld == null)
                resolution.State.Notes.Add($"The character's home world could not be confirmed by the source; it was identified by its name and game object only (expected {job.Request.ExpectedHomeWorld}).");
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

                // A link swapped into the destination after the job started must not redirect the next write.
                var destinationError = ExportPathPolicy.ValidateDestination(job.Request.DestinationDirectory, _forbiddenRoots());
                if (destinationError != null)
                {
                    foreach (var rest in files.Where(f => f.Outcome == FileExportOutcome.Pending))
                    {
                        rest.Outcome = FileExportOutcome.Failed;
                        rest.Error = destinationError;
                    }

                    job.Set(ExportJobPhase.Failed, "The destination is no longer allowed: " + destinationError);
                    return;
                }

                file.FileName = ExportPathPolicy.CacheFileName(file.CacheHash, source.Extension);
                var result = await VerifiedFileCopier.CopyAsync(source.Path, job.Request.DestinationDirectory, file.FileName,
                    file.CacheHash, progress, token, ledger: job.TempFiles).ConfigureAwait(false);
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
            try
            {
                var failed = job.TempFiles.DeleteAll();
                if (failed.Count > 0)
                    job.AddNote($"{failed.Count} temporary file(s) of this export could not be removed (for example {failed[0].Path}: {failed[0].Error}).");
            }
            catch (Exception ex)
            {
                // Cleanup must never keep the slot taken or hide the job's own outcome.
                _log?.Invoke(ex, "Export job cleanup failed");
                job.AddNote("Temporary files of this export could not be removed: " + ex.Message);
            }
            finally
            {
                job.Finish();
                _slot.Release();
            }
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
        public TempFileLedger TempFiles { get; init; } = new();
        private bool _closed;

        public bool IsFinished
        {
            get
            {
                // Finished once cleanup ran too, so a caller never sees a final result while temporary files are still being removed.
                lock (_lock) return _closed;
            }
        }

        /// <summary>Sets the phase. The first final phase wins; nothing changes it afterwards, nor after <see cref="Finish"/>.</summary>
        public void Set(ExportJobPhase phase, string message)
        {
            lock (_lock)
            {
                if (_closed || IsFinal(_phase)) return;
                _phase = phase;
                _message = message;
            }
        }

        /// <summary>Appends to the message without changing the outcome (cleanup problems).</summary>
        public void AddNote(string note)
        {
            lock (_lock) _message = string.IsNullOrEmpty(_message) ? note : _message + " " + note;
        }

        /// <summary>Called once the job ran to its end; a job that somehow never set a final phase is reported as failed.</summary>
        public void Finish()
        {
            lock (_lock)
            {
                if (!IsFinal(_phase))
                {
                    _phase = ExportJobPhase.Failed;
                    _message = "The export ended without a result.";
                }

                _closed = true;
            }
        }

        private static bool IsFinal(ExportJobPhase phase)
            => phase is ExportJobPhase.Completed or ExportJobPhase.CompletedWithIssues or ExportJobPhase.Failed or ExportJobPhase.Cancelled;

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
                var finished = _closed;
                return new ExportJobStatusDto
                {
                    JobId = Id,
                    Found = true,
                    // Until cleanup is done the outcome is held back.
                    Phase = finished || !IsFinal(_phase) ? _phase : ExportJobPhase.Copying,
                    Message = finished || !IsFinal(_phase) ? _message : "Removing temporary files.",
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
