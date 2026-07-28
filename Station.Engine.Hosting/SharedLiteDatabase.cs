// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using System.Globalization;
using LiteDB;

#if ZEUS_PRODUCT_HOST
namespace Zeus.Product.Hosting.Data;
#else
namespace Zeus.Data;
#endif

// Process-wide, reference-counted registry of LiteDatabase instances keyed by
// normalized file path. Guarantees EXACTLY ONE LiteDatabase (one LiteEngine)
// per physical database file per process.
//
// Why this exists (the corruption root cause): Zeus had ~40 stores that each
// did `new LiteDatabase("Filename=...;Connection=shared")` against the SAME
// zeus-prefs.db (and two more against the encrypted zeus.db). Many independent
// engines on one file — each with its own page cache and its own
// open/checkpoint/close lifecycle — is the documented LiteDB corruption
// antipattern: an interrupted write (power loss, the app killed on a network
// change) can leave the shared -log half-checkpointed and brick the file.
// LiteDB's own guidance is the opposite: a LiteDatabase is thread-safe and is
// meant to be opened ONCE and shared.
//
// Every store now Acquire()s a lease here instead of opening its own database.
// The underlying database is opened on the first lease for a given path and
// disposed — flushing a clean WAL checkpoint to disk — when the last lease for
// that path is released. Reference counting (rather than a single DI singleton)
// is deliberate: it serves both production (all stores resolve the same prefs
// path → one engine) and the test suites (each test points its stores at an
// isolated temp file → its own engine, disposed when that test's stores
// dispose, after which a reopen of the same path sees the flushed data).
public static class SharedLiteDatabase
{
    internal sealed class Entry
    {
        public required LiteDatabase Database { get; init; }
        public required string Path { get; init; }
        public Action<string, Exception?>? Warning { get; init; }
        public int RefCount;
        public int Tripped;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Open = new(PathComparer);

    // Windows and macOS default to case-insensitive filesystems; Linux is
    // case-sensitive. Key the registry to match so two spellings of the same
    // file collapse to one engine where the OS treats them as one file.
    private static StringComparer PathComparer =>
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    // A borrowed handle to a shared database. The store holds this for its
    // lifetime and disposes it (NOT the LiteDatabase directly) when it is
    // disposed. Disposing the lease decrements the refcount; the last release
    // closes the underlying database. Idempotent and thread-safe.
    public sealed class Lease : IDisposable
    {
        private readonly string _key;
        private readonly Entry _entry;
        private int _disposed;

        internal Lease(string key, Entry entry)
        {
            _key = key;
            _entry = entry;
            Database = entry.Database;
        }

        public LiteDatabase Database { get; }
        public bool IsTripped => Volatile.Read(ref _entry.Tripped) != 0;

        /// <summary>
        /// Records a critical LiteDB failure against the shared engine. Once
        /// tripped, every lease for this file observes <see cref="IsTripped"/>
        /// and product stores can fail open without repeatedly entering an
        /// engine whose state LiteDB has already invalidated.
        /// </summary>
        public bool MarkTripped(LiteException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (!exception.IsCritical)
                return false;

#if ZEUS_PRODUCT_HOST
            return Trip(_entry, exception);
#else
            return Interlocked.Exchange(ref _entry.Tripped, 1) == 0;
#endif
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Release(_key);
        }
    }

    /// <summary>
    /// Acquire a lease on the single shared database for <paramref name="dbPath"/>,
    /// opening it on first use. The caller MUST dispose the returned lease (never
    /// the underlying <see cref="LiteDatabase"/>). Thread-safe.
    /// </summary>
    public static Lease Acquire(
        string dbPath,
        string? password = null,
        Action<string, Exception?>? warning = null)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
            throw new ArgumentException("Database path is required.", nameof(dbPath));

        var key = Normalize(dbPath);

        // Make sure the directory exists before LiteDB tries to create the file.
        var dir = Path.GetDirectoryName(key);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        lock (Gate)
        {
            if (Open.TryGetValue(key, out var existing))
            {
                existing.RefCount++;
                return new Lease(key, existing);
            }

#if ZEUS_PRODUCT_HOST
            QuarantinePendingDatabase(key, warning);
#endif
            var (db, tripped) = OpenWithIntegritySweep(key, password, warning);
            var entry = new Entry
            {
                Database = db,
                Path = key,
                Warning = warning,
                RefCount = 1,
            };
            if (tripped)
                entry.Tripped = 1;
            Open[key] = entry;
            return new Lease(key, entry);
        }
    }

    private static (LiteDatabase Database, bool Tripped) OpenWithIntegritySweep(
        string path,
        string? password,
        Action<string, Exception?>? warning)
    {
#if ZEUS_PRODUCT_HOST
        LiteDatabase? db = null;
        try
        {
            db = OpenWithRetry(path, password);
            Sweep(db, path);
            return (db, Tripped: false);
        }
        // ANY failure on this path condemns the file: structural damage
        // surfaces as a critical LiteException from the sweep, while
        // header-level damage can throw arbitrary types from LiteDB's page
        // parser (ArgumentOutOfRangeException from a garbage timestamp, a
        // non-critical "invalid datafile" LiteException, ...). Transient locks
        // were already retried inside OpenWithRetry, so whatever reaches here
        // means the file cannot be trusted — and Quarantine preserves it.
        catch (Exception ex)
        {
            try { db?.Dispose(); } catch { }
            if (TryQuarantine(path, out var backup))
            {
                Warn(
                    warning,
                    $"product preferences database was structurally corrupt and was moved to '{backup}'; a fresh database was created",
                    ex);
                var fresh = OpenWithRetry(path, password);
                Sweep(fresh, path);
                return (fresh, Tripped: false);
            }

            // The move failed (a Windows reader holding the file, a read-only
            // directory). A failed quarantine must never block startup: reopen
            // the corrupt database degraded — stores fail open — and leave a
            // marker so the next launch retries the quarantine.
            _ = TryCreateMarker(MarkerPath(path), path, warning);
            Warn(
                warning,
                $"could not move the structurally corrupt database '{path}' aside; continuing degraded until the next launch",
                ex);
            try
            {
                return (OpenWithRetry(path, password), Tripped: true);
            }
            catch (Exception reopenEx)
            {
                // Header-level corruption: even reopening throws. The final
                // backstop is an in-memory database — every store constructs,
                // fails open through the tripped flag, and the marker heals
                // the file on the next launch.
                Warn(
                    warning,
                    $"corrupt database '{path}' cannot be opened at all; running this session on an empty in-memory database",
                    reopenEx);
                return (new LiteDatabase(new MemoryStream()), Tripped: true);
            }
        }
#else
        return (OpenWithRetry(path, password), Tripped: false);
#endif
    }

#if ZEUS_PRODUCT_HOST
    // Full data-page enumeration is bounded: a product database that has grown
    // past this size (years of logbook) gets the cheap Count() pass only — the
    // durable trip marker remains the guarantee for anything the capped sweep
    // cannot see. Internal so tests can exercise the capped path.
    internal static long FullSweepMaxBytes = 8 * 1024 * 1024;

    private static void Sweep(LiteDatabase db, string path)
    {
        long length = 0;
        try
        {
            var info = new FileInfo(path);
            length = info.Exists ? info.Length : 0;
        }
        catch
        {
            // Unreadable metadata — do the full sweep; it is the safer default.
        }
        var full = length <= FullSweepMaxBytes;
        foreach (var name in db.GetCollectionNames().ToArray())
        {
            var collection = db.GetCollection(name);
            _ = collection.Count();
            if (!full)
                continue;
            foreach (var _ in collection.FindAll())
            {
                // Force every data page to be materialized. Secondary indexes
                // are still validated by store construction and runtime use;
                // a failure there trips the durable next-open quarantine.
            }
        }
    }

    private static bool Trip(Entry entry, LiteException? exception)
    {
        if (Interlocked.Exchange(ref entry.Tripped, 1) != 0)
            return false;

        // Attribution note: the warning delegate belongs to whichever store
        // opened this database first, so a trip raised by a later lease logs
        // under the first store's category. The console line below always
        // carries the un-attributed truth.
        var marker = MarkerPath(entry.Path);
        var markerReady = TryCreateMarker(marker, entry.Path, entry.Warning);
        Warn(
            entry.Warning,
            markerReady
                ? $"product preferences database is structurally invalid; wrote quarantine marker '{marker}' and will move the database aside on the next launch"
                : $"product preferences database is structurally invalid and the current process will remain degraded; quarantine marker '{marker}' could not be written",
            exception);
        return true;
    }

    private static bool TryCreateMarker(
        string marker,
        string databasePath,
        Action<string, Exception?>? warning)
    {
        try
        {
            // Record the database file's identity so a stale marker can never
            // quarantine a file the operator replaced after the failure.
            string identity;
            try
            {
                var info = new FileInfo(databasePath);
                identity = info.Exists
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}|{1}",
                        info.Length,
                        info.LastWriteTimeUtc.Ticks)
                    : string.Empty;
            }
            catch
            {
                identity = string.Empty; // legacy-format marker still quarantines
            }

            using var stream = new FileStream(
                marker,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 256,
                FileOptions.WriteThrough);
            var payload = System.Text.Encoding.UTF8.GetBytes(identity);
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (IOException) when (File.Exists(marker))
        {
            // Another lease already persisted the same trip.
            return true;
        }
        catch (Exception ex)
        {
            Warn(
                warning,
                $"could not write quarantine marker '{marker}'; the current process will remain degraded",
                ex);
            return false;
        }
    }

    private static void QuarantinePendingDatabase(
        string path,
        Action<string, Exception?>? warning)
    {
        try
        {
            var marker = MarkerPath(path);
            if (!File.Exists(marker))
                return;

            if (!MarkerMatchesDatabase(marker, path))
            {
                // The database was replaced (manual repair, restored backup)
                // after the marker was written: honour the operator's file,
                // drop the stale marker, and let the normal sweep judge it.
                TryDeleteMarker(marker);
                Warn(
                    warning,
                    $"quarantine marker '{marker}' no longer matches the database file; the file was replaced since the failure was recorded, so the marker was discarded",
                    exception: null);
                return;
            }

            var backup = Quarantine(path);
            TryDeleteMarker(marker);
            Warn(
                warning,
                $"found quarantine marker '{marker}'; moved the structurally invalid product preferences database to '{backup}' and created a fresh database",
                exception: null);
        }
        catch (Exception ex)
        {
            // A failed quarantine (a Windows reader holding the file, a
            // read-only directory) must never block startup: keep the marker
            // for the next launch and let the sweep/trip guard degrade this
            // session instead.
            Warn(
                warning,
                $"could not quarantine the product preferences database '{path}'; continuing with the existing file",
                ex);
        }
    }

    // The marker records the database file's identity (length|lastWriteUtcTicks,
    // invariant culture) captured at trip time. An empty, legacy, or unparsable
    // marker counts as a match: corruption safety wins over the rare
    // manual-replacement race, and the .bak preserves the data either way.
    // Known limit: a same-length in-place repair on a coarse-mtime filesystem
    // (FAT/exFAT Pi boot media, some NAS mounts) can still match and be
    // quarantined — recoverable from the .bak, so the simple identity wins
    // over hashing page content here.
    private static bool MarkerMatchesDatabase(string marker, string path)
    {
        try
        {
            var text = File.ReadAllText(marker).Trim();
            if (text.Length == 0)
                return true;
            var parts = text.Split('|');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length)
                || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks))
                return true;

            var info = new FileInfo(path);
            if (!info.Exists)
                return false; // nothing to quarantine; the marker is stale
            return info.Length == length && info.LastWriteTimeUtc.Ticks == ticks;
        }
        catch
        {
            return true;
        }
    }

    private static void TryDeleteMarker(string marker)
    {
        try { DeleteWithRetry(marker); }
        catch
        {
            // A marker that cannot be removed is re-evaluated next launch; the
            // identity check keeps it from harming a replaced file.
        }
    }

    private static bool TryQuarantine(string path, out string backup)
    {
        try
        {
            backup = Quarantine(path);
            return true;
        }
        catch (Exception)
        {
            backup = string.Empty;
            return false;
        }
    }

    private static string Quarantine(string path)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
        var backup = $"{path}.corrupt-{stamp}.bak";
        for (var suffix = 2; File.Exists(backup); suffix++)
            backup = $"{path}.corrupt-{stamp}-{suffix}.bak";

        // WAL first: if it cannot be moved, nothing has been touched and the
        // quarantine retries cleanly next launch. Moving the DB first could
        // strand the log if the second move failed, complicating recovery.
        var log = path + "-log";
        if (File.Exists(log))
            MoveWithRetry(log, backup + "-log");
        if (File.Exists(path))
            MoveWithRetry(path, backup);
        return backup;
    }

    private static void MoveWithRetry(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Move(source, destination);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Windows readers surface either exception for the same
                // transient hold; retry both, matching DeleteWithRetry.
                last = ex;
                Thread.Sleep(100);
            }
        }
        throw last ?? new IOException($"Unable to move corrupt database '{source}'.");
    }

    private static void DeleteWithRetry(string path)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(100);
            }
        }
        throw last ?? new IOException($"Unable to remove quarantine marker '{path}'.");
    }

    private static string MarkerPath(string path) => path + ".quarantine-pending";

    private static void Warn(
        Action<string, Exception?>? warning,
        string message,
        Exception? exception)
    {
        // The delegate is invoked OFF the registry lock: several Warn call
        // sites run under Gate, and a logging pipeline that itself resolves a
        // LiteDB-backed sink through Acquire would otherwise deadlock.
        if (warning is not null)
        {
            _ = Task.Run(() =>
            {
                try { warning(message, exception); } catch { }
            });
        }
        try
        {
            var detail = exception is null
                ? string.Empty
                : $" :: {exception.GetType().Name}: {exception.Message}";
            Console.Error.WriteLine($"product.db.guard {message}{detail}");
        }
        catch { }
    }
#endif

    private static void Release(string key)
    {
        lock (Gate)
        {
            if (!Open.TryGetValue(key, out var entry)) return;
            if (--entry.RefCount > 0) return;

            Open.Remove(key);
            try
            {
                entry.Database.Dispose(); // flushes the WAL checkpoint to disk
            }
            catch
            {
                // A failed checkpoint at shutdown must never throw out of a
                // Dispose path. The next launch's integrity guard handles a file
                // left in a bad state.
            }
#if ZEUS_PRODUCT_HOST
            // Durable-trip marker, written AFTER the dispose: the shutdown WAL
            // checkpoint mutates the file's length/mtime, so an identity
            // captured earlier (at trip time) would no longer match at the next
            // open and the marker would be wrongly discarded. Rewriting here
            // records the file's final at-rest identity. Best effort only.
            if (entry.Tripped != 0) // Gate lock already fences this read
            {
                var marker = MarkerPath(key);
                TryDeleteMarker(marker);
                _ = TryCreateMarker(marker, key, entry.Warning);
            }
#endif
        }
    }

    // Open the database exclusively (LiteDB's default "direct" connection — one
    // engine, no per-operation reopen churn). A short bounded retry absorbs the
    // one realistic transient on the single-process design: an on-access virus
    // scanner (common on Windows) holding a just-created file for a few hundred
    // milliseconds. A non-lock exception — a corrupt/torn file — is NOT a
    // transient and is not retried: it propagates immediately so the caller's
    // startup integrity guard (PrefsDbPath.EnsureUsable) can move the file aside
    // before any store opens it.
    private static LiteDatabase OpenWithRetry(string path, string? password)
    {
        Exception? lastLock = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                return new LiteDatabase(ConnectionString(path, password));
            }
            catch (Exception ex) when (IsTransientLock(ex))
            {
                lastLock = ex;
                Thread.Sleep(100 + attempt * 50);
            }
        }

        throw lastLock ?? new IOException($"Unable to open database '{path}'.");
    }

    private static string ConnectionString(string path, string? password)
    {
        var s = $"Filename={path}";
        if (!string.IsNullOrEmpty(password)) s += $";Password={password}";
        return s;
    }

    private static bool IsTransientLock(Exception ex) =>
        ex is IOException
        || ex is UnauthorizedAccessException
        || ex.InnerException is IOException
        || ex.InnerException is UnauthorizedAccessException;

    private static string Normalize(string path) => Path.GetFullPath(path);

    /// <summary>Number of distinct database files currently open. Diagnostics/tests.</summary>
    public static int OpenCount
    {
        get { lock (Gate) { return Open.Count; } }
    }

    internal static bool IsOpenForTests(string dbPath)
    {
        var key = Normalize(dbPath);
        lock (Gate) { return Open.ContainsKey(key); }
    }

#if ZEUS_PRODUCT_HOST
    internal static void MarkTrippedForTests(string dbPath)
    {
        var key = Normalize(dbPath);
        lock (Gate)
        {
            if (!Open.TryGetValue(key, out var entry))
                throw new InvalidOperationException($"Database '{key}' is not open.");
            _ = Trip(entry, exception: null);
        }
    }
#endif
}
