// SPDX-License-Identifier: GPL-2.0-or-later
//
// Zeus — OpenHPSDR Protocol-1 / Protocol-2 client.
// Copyright (C) 2025-2026 Douglas J. Cerrato (KB2UKA),
//                         Christian Suarez (N9WAR), and contributors.
//
// See ATTRIBUTIONS.md at the repository root for the full provenance
// statement and per-component attribution.

using LiteDB;

namespace Zeus.Server;

/// <summary>
/// Typed startup read of the three zeus-prefs.db collections the engine opens
/// before it accepts work. <see cref="PrefsDbPath.EnsureUsable"/> only reads
/// raw documents, so a file whose pages are intact but whose rows have the
/// wrong shape still passes. A typed-mapping failure that reproduces on every
/// attempt is the same failure that stops the host on develop. A lock or an
/// access denial is not that failure: the shape cannot be judged, and the
/// file stays where it is.
/// </summary>
internal static class PrefsCollectionShapeGuard
{
    internal readonly record struct UnreadableCollection(string Name, Exception Error);

    // Same short-lived shared handle as PrefsDbPath's probe. Disposed before
    // this method returns, so the caller can move the file. Never a
    // SharedLiteDatabase lease: that lease would still be open during the move.
    // open / read are a test seam. Production passes neither: open replaces the
    // constructor, and a non-null exception from read is thrown in place of the
    // typed mapper (a null return is a clean read).
    //
    // A clean read leaves the file alone. A lock or an access denial also
    // leaves it: this guard cannot judge the rows, and it never moves a file.
    // Probe differs on access denial. It uses only IsTransientLock (an
    // IOException, including EndOfStreamException, or an inner IOException),
    // so EnsureUsable moves a file whose open is denied — a permanently
    // unwritable file is corrupt to the probe. This guard does not own that
    // judgement. Only a typed failure that is neither a lock nor an access
    // denial, on every attempt, is reported, and the caller is what moves
    // the file. Any other open failure is logged and left in place:
    // EnsureUsable already judged the open. Nothing here throws.
    internal static UnreadableCollection? FindUnreadable(
        string dbPath,
        Func<string, LiteDatabase>? open = null,
        Func<LiteDatabase, string, Exception?>? read = null)
    {
        try
        {
            if (string.IsNullOrEmpty(dbPath) || !File.Exists(dbPath))
                return null;

            UnreadableCollection? unreadable = null;
            for (var attempt = 0; attempt < PrefsDbTransient.Attempts; attempt++)
            {
                var once = ReadOnce(dbPath, open, read);
                switch (once.Kind)
                {
                    case ShapeAttemptKind.Transient:
                        NoteSkipped(once.Error!);
                        return null;
                    case ShapeAttemptKind.OpenFailed:
                        NoteOpenFailed(dbPath, once.Error!);
                        return null;
                    case ShapeAttemptKind.Clean:
                        return null;
                    case ShapeAttemptKind.Unreadable:
                        unreadable = once.Unreadable;
                        if (attempt < PrefsDbTransient.Attempts - 1)
                            Thread.Sleep(PrefsDbTransient.BackoffMs);
                        break;
                }
            }

            return unreadable;
        }
        catch (Exception ex)
        {
            NoteOpenFailed(dbPath, ex);
            return null;
        }
    }

    private readonly record struct ShapeAttempt(
        ShapeAttemptKind Kind,
        UnreadableCollection? Unreadable,
        Exception? Error);

    private enum ShapeAttemptKind
    {
        Clean,
        Transient,
        Unreadable,
        OpenFailed,
    }

    private static ShapeAttempt ReadOnce(
        string dbPath,
        Func<string, LiteDatabase>? open,
        Func<LiteDatabase, string, Exception?>? read)
    {
        LiteDatabase? db = null;
        try
        {
            db = open is null
                ? new LiteDatabase($"Filename={dbPath};Connection=shared")
                : open(dbPath);
            var present = db.GetCollectionNames().ToHashSet(StringComparer.Ordinal);
            foreach (var spec in TypedReads)
            {
                if (!present.Contains(spec.Name))
                    continue;
                try
                {
                    if (read is null)
                        spec.Read(db);
                    else if (read(db, spec.Name) is { } injected)
                        throw injected;
                }
                catch (Exception ex) when (CannotJudge(ex))
                {
                    return new ShapeAttempt(ShapeAttemptKind.Transient, null, ex);
                }
                catch (Exception ex)
                {
                    return new ShapeAttempt(
                        ShapeAttemptKind.Unreadable,
                        new UnreadableCollection(spec.Name, ex),
                        ex);
                }
            }

            return new ShapeAttempt(ShapeAttemptKind.Clean, null, null);
        }
        catch (Exception ex) when (CannotJudge(ex))
        {
            return new ShapeAttempt(ShapeAttemptKind.Transient, null, ex);
        }
        catch (Exception ex)
        {
            return new ShapeAttempt(ShapeAttemptKind.OpenFailed, null, ex);
        }
        finally
        {
            db?.Dispose();
        }
    }

    // Probe moves a file on access denial. This guard must not: an access
    // denial during the typed read is "cannot judge", same as a lock.
    private static bool CannotJudge(Exception ex) =>
        PrefsDbTransient.IsTransientLock(ex) || PrefsDbTransient.IsAccessDenied(ex);

    private static void NoteSkipped(Exception ex) =>
        Console.Error.WriteLine(
            "prefs.guard shape check skipped (transient " + ex.GetType().Name + ": " + ex.Message +
            "); leaving the preferences file in place.");

    private static void NoteOpenFailed(string dbPath, Exception ex) =>
        Console.Error.WriteLine(
            "prefs.guard shape check could not open '" + dbPath + "' (" + ex.GetType().Name + ": " +
            ex.Message + "); leaving the file in place.");

    private readonly record struct TypedRead(string Name, Action<LiteDatabase> Read);

    private static readonly TypedRead[] TypedReads =
    [
        new(KiwiSettingsStore.CollectionName, static db =>
            _ = db.GetCollection<KiwiSettingsEntry>(KiwiSettingsStore.CollectionName)
                .FindAll().FirstOrDefault()),
        new(DisplaySettingsStore.CollectionName, static db =>
            _ = db.GetCollection<DisplaySettingsEntry>(DisplaySettingsStore.CollectionName)
                .FindAll().FirstOrDefault()),
        new(ProductBundleSettingsStore.CollectionName, static db =>
            _ = db.GetCollection<ProductBundleSettingsEntry>(ProductBundleSettingsStore.CollectionName)
                .FindAll().FirstOrDefault()),
    ];
}

/// <summary>
/// Read the single settings row before a write. A wrong-shaped row shares its
/// pages with whatever collection was cross-linked into it; inserting or
/// dropping that collection can damage the other one. Refuse instead.
/// </summary>
internal static class PrefsSingleRow
{
    internal static T? ReadOrRefuse<T>(ILiteCollection<T> rows, string collection)
    {
        try
        {
            return rows.FindAll().FirstOrDefault();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                collection + " could not be read (" + ex.GetType().Name + ": " + ex.Message +
                "). Refusing to write into an unreadable collection.",
                ex);
        }
    }
}
