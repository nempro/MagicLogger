using System.IO;
using Microsoft.Data.Sqlite;

namespace MagicLogger;

internal static class ReadOnlySourceConnection
{
    internal static FileStream AcquireReadLease(string path)
    {
        path = Path.GetFullPath(path);
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("読取対象にリンクを含むパスは指定できません。");
        var lease = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            foreach (var suffix in new[] { "-wal", "-journal" })
                if (File.Exists(path + suffix) && new FileInfo(path + suffix).Length > 0)
                    throw new InvalidOperationException("会話DBに未反映のWALまたはjournalがあります。通常版を終了してから検証してください。");
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    internal static SqliteConnection Open(string path, bool immutable)
    {
        var fullPath = Path.GetFullPath(path);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            // Immutable avoids WAL/SHM creation. HA holds a read lease and rejects
            // pending WAL before using this connection, so the file cannot change.
            DataSource = immutable ? new Uri(fullPath).AbsoluteUri + "?immutable=1" : fullPath,
            Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; PRAGMA busy_timeout=3000;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
