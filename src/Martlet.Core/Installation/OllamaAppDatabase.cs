using System.Runtime.InteropServices;
using System.Text;

namespace Martlet.Core.Installation;

/// <summary>What Ollama for Windows' app database says about its Model location: <paramref name="KnownShape"/> when it has
/// the <c>settings</c> table with its <c>models</c> column and row 1, as Martlet expects; <paramref name="Models"/> the saved
/// location (empty: the app uses OLLAMA_MODELS, else <c>%USERPROFILE%\.ollama\models</c>); <paramref name="Problem"/> why it
/// couldn't be read.</summary>
public sealed record OllamaAppSettings(bool Found, bool KnownShape, string? Models, string? Problem);

/// <summary>Ollama for Windows' tray app keeps its settings in <c>%LOCALAPPDATA%\Ollama\db.sqlite</c>; its Model location
/// (<c>settings.models</c>, row <c>id = 1</c>) overrides OLLAMA_MODELS for the server it starts. This is Ollama's own,
/// undocumented storage, so every read and write checks the table, column and row first and does nothing when they aren't
/// as expected. Uses the SQLite that ships with Windows (<c>winsqlite3.dll</c>); off Windows nothing can be read.</summary>
public static class OllamaAppDatabase
{
    /// <summary>Reads the Model location, read-only. Safe while Ollama runs (waits up to two seconds for its lock).</summary>
    public static OllamaAppSettings Read(string database)
    {
        if (!File.Exists(database)) return new(false, false, null, "Ollama's app database isn't there.");
        if (!OperatingSystem.IsWindows()) return new(true, false, null, "Ollama's app database can only be read on Windows.");
        try
        {
            using var db = Sqlite.Open(database, Sqlite.ReadOnly, busyMilliseconds: 2000);
            if (!KnownShape(db, out var problem)) return new(true, false, null, problem);
            return new(true, true, db.Text("SELECT models FROM settings WHERE id = 1") ?? "", null);
        }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return new(true, false, null, "Ollama's app database couldn't be read: " + error.Message);
        }
    }

    /// <summary>Copies <c>db.sqlite</c> and its <c>-wal</c> and <c>-shm</c> files into a new <c>martlet-backup-&lt;time&gt;</c>
    /// folder beside them, and returns that folder. Copy them back (with Ollama stopped) to undo a change.</summary>
    public static string Backup(string database, DateTimeOffset now)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(database))!;
        var name = "martlet-backup-" + now.ToLocalTime().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backup = Path.Combine(directory, name);
        for (var n = 2; Directory.Exists(backup); n++) backup = Path.Combine(directory, $"{name}-{n}");
        Directory.CreateDirectory(backup);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = database + suffix;
            if (File.Exists(file)) File.Copy(file, Path.Combine(backup, Path.GetFileName(file)));
        }
        return backup;
    }

    /// <summary>Saves <paramref name="models"/> as the app's Model location, checking the table, column and row first and the
    /// saved value after. Only while Ollama's app is stopped. Throws <see cref="InvalidOperationException"/> when the database
    /// isn't as expected or the change didn't save.</summary>
    public static void WriteModels(string database, string models)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Ollama's app database can only be changed on Windows.");
        if (!File.Exists(database)) throw new InvalidOperationException("Ollama's app database isn't there.");
        try
        {
            using var db = Sqlite.Open(database, Sqlite.ReadWrite, busyMilliseconds: 5000);
            if (!KnownShape(db, out var problem)) throw new InvalidOperationException(problem);
            var changed = db.Execute("UPDATE settings SET models = ?1 WHERE id = 1", models);
            if (changed != 1) throw new InvalidOperationException($"Ollama's app database changed {changed} rows instead of one.");
            var saved = db.Text("SELECT models FROM settings WHERE id = 1");
            if (!string.Equals(saved, models, StringComparison.Ordinal))
                throw new InvalidOperationException("Ollama's app database didn't keep the new model location.");
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Windows' SQLite (winsqlite3.dll) isn't available: " + error.Message);
        }
    }

    /// <summary>Runs <paramref name="sql"/> (several statements allowed) on <paramref name="database"/>, creating it. For
    /// fixtures only.</summary>
    internal static void Create(string database, string sql)
    {
        using var db = Sqlite.Open(database, Sqlite.ReadWrite | Sqlite.CreateFlag, busyMilliseconds: 2000);
        db.Script(sql);
    }

    private static bool KnownShape(Sqlite db, out string? problem)
    {
        problem = null;
        if (db.Text("SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'settings'") is null)
            problem = "Ollama's app database has no settings table. This Ollama version keeps its settings differently.";
        else if (db.Text("SELECT name FROM pragma_table_info('settings') WHERE name = 'models'") is null)
            problem = "Ollama's app settings have no models column. This Ollama version keeps its model location differently.";
        else if (db.Text("SELECT CAST(id AS TEXT) FROM settings WHERE id = 1") is null)
            problem = "Ollama's app settings have no saved row yet.";
        return problem is null;
    }

    /// <summary>The few SQLite calls Martlet needs, through Windows' own <c>winsqlite3.dll</c>.</summary>
    private sealed class Sqlite : IDisposable
    {
        internal const int ReadOnly = 0x1, ReadWrite = 0x2, CreateFlag = 0x4;
        private const int Ok = 0, Row = 100, Done = 101, FullMutex = 0x10000;
        private static readonly IntPtr Transient = new(-1);
        private IntPtr handle;

        private Sqlite(IntPtr handle) => this.handle = handle;

        internal static Sqlite Open(string path, int flags, int busyMilliseconds)
        {
            var result = sqlite3_open_v2(Utf8(path), out var handle, flags | FullMutex, IntPtr.Zero);
            var db = new Sqlite(handle);
            if (result != Ok)
            {
                var message = db.Error();
                db.Dispose();
                throw new InvalidOperationException($"SQLite couldn't open it ({result}: {message}).");
            }
            sqlite3_busy_timeout(handle, busyMilliseconds);
            return db;
        }

        /// <summary>The first column of the first row as text, or null when there is no row.</summary>
        internal string? Text(string sql, params string[] parameters)
        {
            var statement = Prepare(sql, parameters);
            try
            {
                var step = sqlite3_step(statement);
                if (step == Done) return null;
                if (step != Row) throw new InvalidOperationException($"SQLite couldn't read ({step}: {Error()}).");
                var text = sqlite3_column_text(statement, 0);
                return text == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(text);
            }
            finally { sqlite3_finalize(statement); }
        }

        /// <summary>Runs one statement and returns how many rows it changed.</summary>
        internal int Execute(string sql, params string[] parameters)
        {
            var statement = Prepare(sql, parameters);
            try
            {
                var step = sqlite3_step(statement);
                if (step != Done) throw new InvalidOperationException($"SQLite couldn't save ({step}: {Error()}).");
                return sqlite3_changes(handle);
            }
            finally { sqlite3_finalize(statement); }
        }

        internal void Script(string sql)
        {
            var result = sqlite3_exec(handle, Utf8(sql), IntPtr.Zero, IntPtr.Zero, out var error);
            if (error != IntPtr.Zero)
            {
                var message = Marshal.PtrToStringUTF8(error);
                sqlite3_free(error);
                throw new InvalidOperationException($"SQLite couldn't run the script ({result}: {message}).");
            }
            if (result != Ok) throw new InvalidOperationException($"SQLite couldn't run the script ({result}: {Error()}).");
        }

        private IntPtr Prepare(string sql, string[] parameters)
        {
            var bytes = Utf8(sql);
            var result = sqlite3_prepare_v2(handle, bytes, bytes.Length, out var statement, IntPtr.Zero);
            if (result != Ok) throw new InvalidOperationException($"SQLite couldn't prepare a query ({result}: {Error()}).");
            for (var i = 0; i < parameters.Length; i++)
            {
                var value = Encoding.UTF8.GetBytes(parameters[i]);
                if (sqlite3_bind_text(statement, i + 1, value, value.Length, Transient) != Ok)
                {
                    sqlite3_finalize(statement);
                    throw new InvalidOperationException($"SQLite couldn't use a value ({Error()}).");
                }
            }
            return statement;
        }

        private string Error() => handle == IntPtr.Zero ? "no connection" : Marshal.PtrToStringUTF8(sqlite3_errmsg(handle)) ?? "unknown";

        private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

        public void Dispose()
        {
            if (handle == IntPtr.Zero) return;
            sqlite3_close_v2(handle);
            handle = IntPtr.Zero;
        }

        private const string Library = "winsqlite3.dll";

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_close_v2(IntPtr db);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_bind_text(IntPtr statement, int index, byte[] value, int bytes, IntPtr destructor);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_step(IntPtr statement);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_changes(IntPtr db);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr sqlite3_errmsg(IntPtr db);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int sqlite3_exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, out IntPtr error);

        [DllImport(Library, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern void sqlite3_free(IntPtr memory);
    }
}
