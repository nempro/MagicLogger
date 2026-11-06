using Microsoft.Data.Sqlite;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MagicLogger;

public sealed class LocalDatabase
{
    private const int SupportedSchemaVersion = 4;
    // SQLite's shared page cache can return SQLITE_LOCKED immediately when a
    // reader and a writer from this process touch the same table.  WAL is the
    // concurrency mechanism we want here, so each short-lived connection gets
    // its own private cache instead.
    private const int BusyTimeoutMilliseconds = 3000;
    private static readonly TimeSpan[] BusyRetryDelays =
    [
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(500),
        TimeSpan.FromSeconds(1)
    ];

    private const string FragmentSelectColumns = """
        f.fragment_id,f.source_conversation_id,f.date,f.title,f.excerpt,f.summary,f.classification,
        f.bookmarked,f.hidden,f.edited_fields,f.generation_metadata,
        COALESCE((SELECT group_concat(k.display,char(31)) FROM fragment_keywords fk JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE fk.fragment_id=f.fragment_id),''),
        COALESCE((SELECT group_concat(c.display,char(31)) FROM fragment_categories fc JOIN categories c ON c.category_id=fc.category_id WHERE fc.fragment_id=f.fragment_id),''),
        COALESCE((SELECT group_concat(display,char(31)) FROM (
            SELECT t.display,t.normalized FROM fragment_tags ft JOIN tags t ON t.tag_id=ft.tag_id
            WHERE ft.fragment_id=f.fragment_id AND NOT EXISTS(
                SELECT 1 FROM fragment_tag_exclusions x WHERE x.fragment_id=ft.fragment_id AND x.tag_id=ft.tag_id)
            UNION
            SELECT t.display,t.normalized FROM fragment_user_tags ut JOIN tags t ON t.tag_id=ut.tag_id
            WHERE ut.fragment_id=f.fragment_id ORDER BY normalized)),''),
        f.section_type,
        COALESCE((SELECT group_concat(t.display,char(31)) FROM fragment_tags ft JOIN tags t ON t.tag_id=ft.tag_id
            WHERE ft.fragment_id=f.fragment_id AND NOT EXISTS(
                SELECT 1 FROM fragment_tag_exclusions x WHERE x.fragment_id=ft.fragment_id AND x.tag_id=ft.tag_id)),''),
        COALESCE((SELECT group_concat(t.display,char(31)) FROM fragment_user_tags ut JOIN tags t ON t.tag_id=ut.tag_id
            WHERE ut.fragment_id=f.fragment_id),''),
        COALESCE((SELECT group_concat(t.display,char(31)) FROM fragment_tag_exclusions x JOIN tags t ON t.tag_id=x.tag_id
            WHERE x.fragment_id=f.fragment_id),''),
        COALESCE((SELECT group_concat(t.display,char(31)) FROM fragment_user_tags ut JOIN tags t ON t.tag_id=ut.tag_id
            WHERE ut.fragment_id=f.fragment_id AND ut.source_kind='INDEX'),''),
        (SELECT a.folder_id FROM fragment_folder_assignments a WHERE a.fragment_id=f.fragment_id AND a.folder_kind='BOOKMARK'),
        (SELECT d.name FROM fragment_folder_assignments a JOIN collection_folders d ON d.folder_id=a.folder_id
            WHERE a.fragment_id=f.fragment_id AND a.folder_kind='BOOKMARK'),
        (SELECT a.folder_id FROM fragment_folder_assignments a WHERE a.fragment_id=f.fragment_id AND a.folder_kind='REFERENCE'),
        (SELECT d.name FROM fragment_folder_assignments a JOIN collection_folders d ON d.folder_id=a.folder_id
            WHERE a.fragment_id=f.fragment_id AND a.folder_kind='REFERENCE')
        """;

    public string DatabasePath { get; }
    public string DataDirectory { get; }
    public bool IsReadOnly { get; }

    public LocalDatabase(string dataDirectory, bool readOnly = false, string? databasePath = null)
    {
        DataDirectory = dataDirectory;
        IsReadOnly = readOnly;
        if (!readOnly) Directory.CreateDirectory(DataDirectory);
        DatabasePath = databasePath ?? Path.Combine(DataDirectory, "magic-logger.db");
    }

    private SqliteConnection Open()
    {
        if (IsReadOnly) return ReadOnlySourceConnection.Open(DatabasePath, immutable: true);
        var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadWriteCreate;Cache=Private");
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static bool IsTransientLock(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6; // SQLITE_BUSY / SQLITE_LOCKED

    // A lock held by another process is retried only three times.  Callers get
    // the final exception if it remains unavailable; no write is treated as
    // successful until SQLite has committed it.
    private static T ExecuteWithBusyRetry<T>(Func<T> operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return operation(); }
            catch (SqliteException ex) when (IsTransientLock(ex) && attempt < BusyRetryDelays.Length)
            {
                Thread.Sleep(BusyRetryDelays[attempt]);
            }
        }
    }

    public void Initialize()
    {
        if (IsReadOnly)
        {
            if (GetMeta("schema_version") != "4")
                throw new InvalidOperationException("読取専用HAはSchema v4の魔道書DBが対象です。HA中は移行しません。");
            return;
        }
        // Inspect the existing database through a read-only connection before opening
        // it for migration. In particular, never create a backup or rewrite version
        // metadata for a database produced by a newer application.
        if (File.Exists(DatabasePath)) RejectNewerSchemaOnDisk();
        using var connection = Open();
        RejectNewerSchema(connection);
        EnsurePhase03SafetyBackupBeforeMigration(connection);
        var sourceArchiveBackup = EnsureSourceArchiveSafetyBackupBeforeMigration(connection);
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS meta (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversations (
                conversation_id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                imported_at TEXT NOT NULL,
                source_is_archived INTEGER CHECK(source_is_archived IN (0,1) OR source_is_archived IS NULL)
            );
            CREATE TABLE IF NOT EXISTS messages (
                conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id),
                message_id TEXT NOT NULL,
                parent_message_id TEXT,
                timestamp TEXT NOT NULL,
                role TEXT NOT NULL,
                raw_text TEXT NOT NULL,
                source_hash TEXT NOT NULL UNIQUE,
                imported_at TEXT NOT NULL,
                PRIMARY KEY (conversation_id, message_id)
            );
            CREATE TRIGGER IF NOT EXISTS messages_raw_text_immutable
            BEFORE UPDATE OF raw_text ON messages
            BEGIN
                SELECT RAISE(ABORT, 'raw_text is immutable');
            END;
            CREATE TABLE IF NOT EXISTS fragments (
                fragment_id TEXT PRIMARY KEY,
                source_conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id),
                date TEXT NOT NULL,
                title TEXT NOT NULL,
                excerpt TEXT NOT NULL,
                summary TEXT NOT NULL,
                classification TEXT NOT NULL,
                bookmarked INTEGER NOT NULL DEFAULT 0,
                hidden INTEGER NOT NULL DEFAULT 0,
                edited_fields TEXT NOT NULL DEFAULT '[]',
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                generation_metadata TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS fragment_messages (
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id) ON DELETE CASCADE,
                conversation_id TEXT NOT NULL,
                message_id TEXT NOT NULL,
                sequence_no INTEGER NOT NULL,
                PRIMARY KEY (fragment_id, message_id),
                FOREIGN KEY (conversation_id, message_id) REFERENCES messages(conversation_id, message_id)
            );
            CREATE TABLE IF NOT EXISTS keywords (
                keyword_id INTEGER PRIMARY KEY AUTOINCREMENT,
                normalized TEXT NOT NULL UNIQUE,
                display TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS fragment_keywords (
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id) ON DELETE CASCADE,
                keyword_id INTEGER NOT NULL REFERENCES keywords(keyword_id),
                sequence_no INTEGER NOT NULL,
                PRIMARY KEY (fragment_id, keyword_id)
            );
            CREATE TABLE IF NOT EXISTS fragment_revisions (
                revision_id INTEGER PRIMARY KEY AUTOINCREMENT,
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id),
                field_name TEXT NOT NULL,
                old_value TEXT NOT NULL,
                new_value TEXT NOT NULL,
                saved_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS import_runs (
                import_id INTEGER PRIMARY KEY AUTOINCREMENT,
                source_path TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                status TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                error_text TEXT
            );
            CREATE TABLE IF NOT EXISTS import_run_files (
                import_id INTEGER NOT NULL REFERENCES import_runs(import_id) ON DELETE CASCADE,
                source_path TEXT NOT NULL COLLATE NOCASE,
                source_hash TEXT NOT NULL,
                status TEXT NOT NULL,
                error_text TEXT,
                processed_at TEXT,
                PRIMARY KEY (import_id, source_path)
            );
            CREATE TABLE IF NOT EXISTS relations (
                from_fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id),
                to_fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id),
                evidence_type TEXT NOT NULL DEFAULT 'DISCOVERED_RELATION',
                score REAL NOT NULL,
                created_at TEXT NOT NULL,
                PRIMARY KEY (from_fragment_id, to_fragment_id, evidence_type)
            );
            CREATE VIRTUAL TABLE IF NOT EXISTS fragment_fts USING fts5(fragment_id UNINDEXED, title, excerpt, summary, keywords);
            CREATE TABLE IF NOT EXISTS categories (
                category_id INTEGER PRIMARY KEY AUTOINCREMENT,
                normalized TEXT NOT NULL UNIQUE,
                display TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS fragment_categories (
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id) ON DELETE CASCADE,
                category_id INTEGER NOT NULL REFERENCES categories(category_id),
                sequence_no INTEGER NOT NULL,
                PRIMARY KEY (fragment_id, category_id)
            );
            CREATE TABLE IF NOT EXISTS tags (
                tag_id INTEGER PRIMARY KEY AUTOINCREMENT,
                normalized TEXT NOT NULL UNIQUE,
                display TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS fragment_tags (
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(tag_id),
                sequence_no INTEGER NOT NULL,
                PRIMARY KEY (fragment_id, tag_id)
            );
            CREATE TABLE IF NOT EXISTS fragment_user_tags (
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(tag_id),
                source_kind TEXT NOT NULL DEFAULT 'MANUAL' CHECK(source_kind IN ('MANUAL','INDEX')),
                sequence_no INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                PRIMARY KEY (fragment_id, tag_id)
            );
            CREATE TABLE IF NOT EXISTS fragment_tag_exclusions (
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id) ON DELETE CASCADE,
                tag_id INTEGER NOT NULL REFERENCES tags(tag_id),
                excluded_at TEXT NOT NULL,
                PRIMARY KEY (fragment_id, tag_id)
            );
            CREATE TABLE IF NOT EXISTS promoted_index_tags (
                tag_id INTEGER PRIMARY KEY REFERENCES tags(tag_id),
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS collection_folders (
                folder_id INTEGER PRIMARY KEY AUTOINCREMENT,
                folder_kind TEXT NOT NULL CHECK(folder_kind IN ('BOOKMARK','REFERENCE')),
                normalized TEXT NOT NULL,
                name TEXT NOT NULL,
                created_at TEXT NOT NULL,
                UNIQUE(folder_kind, normalized)
            );
            CREATE TABLE IF NOT EXISTS fragment_folder_assignments (
                fragment_id TEXT NOT NULL REFERENCES fragments(fragment_id) ON DELETE CASCADE,
                folder_kind TEXT NOT NULL CHECK(folder_kind IN ('BOOKMARK','REFERENCE')),
                folder_id INTEGER NOT NULL REFERENCES collection_folders(folder_id) ON DELETE CASCADE,
                assigned_at TEXT NOT NULL,
                PRIMARY KEY (fragment_id, folder_kind)
            );
            CREATE INDEX IF NOT EXISTS ix_fragments_visible_date
                ON fragments(classification, hidden, date DESC, fragment_id DESC);
            CREATE INDEX IF NOT EXISTS ix_fragments_bookmarked_date
                ON fragments(bookmarked, hidden, date DESC, fragment_id DESC);
            CREATE INDEX IF NOT EXISTS ix_fragments_conversation
                ON fragments(source_conversation_id);
            CREATE INDEX IF NOT EXISTS ix_fragment_messages_fragment_sequence
                ON fragment_messages(fragment_id, sequence_no);
            CREATE INDEX IF NOT EXISTS ix_fragment_keywords_keyword_fragment
                ON fragment_keywords(keyword_id, fragment_id);
            CREATE INDEX IF NOT EXISTS ix_fragment_categories_category_fragment
                ON fragment_categories(category_id, fragment_id);
            CREATE INDEX IF NOT EXISTS ix_fragment_tags_tag_fragment
                ON fragment_tags(tag_id, fragment_id);
            CREATE INDEX IF NOT EXISTS ix_fragment_user_tags_tag_fragment
                ON fragment_user_tags(tag_id, fragment_id);
            CREATE INDEX IF NOT EXISTS ix_fragment_tag_exclusions_tag_fragment
                ON fragment_tag_exclusions(tag_id, fragment_id);
            CREATE INDEX IF NOT EXISTS ix_fragment_folder_assignments_folder_fragment
                ON fragment_folder_assignments(folder_kind, folder_id, fragment_id);
            CREATE INDEX IF NOT EXISTS ix_import_run_files_path_status
                ON import_run_files(source_path COLLATE NOCASE, import_id DESC, status);
            """;
        command.ExecuteNonQuery();
        AddColumnIfMissing(connection, "fragments", "section_type", "TEXT NOT NULL DEFAULT 'MAIN'");
        AddColumnIfMissing(connection, "conversations", "source_is_archived",
            "INTEGER CHECK(source_is_archived IN (0,1) OR source_is_archived IS NULL)");
        using var search = connection.CreateCommand();
        search.CommandText = """
            CREATE VIRTUAL TABLE IF NOT EXISTS fragment_search_fts USING fts5(
                fragment_id UNINDEXED, title, summary, raw_text, categories, tags, index_words,
                tokenize='trigram'
            );
            INSERT INTO meta(key,value)
            SELECT 'page_size','10'
            WHERE NOT EXISTS(SELECT 1 FROM meta WHERE key='phase03_page_size_initialized')
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            INSERT OR IGNORE INTO meta(key,value) VALUES('phase03_page_size_initialized','1');
            INSERT INTO meta(key,value) VALUES('schema_version','4')
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            PRAGMA user_version=4;
            """;
        search.ExecuteNonQuery();
        if (sourceArchiveBackup is not null)
        {
            using var remember = connection.CreateCommand();
            remember.CommandText = "INSERT INTO meta(key,value) VALUES('source_archive_migration_backup_path',$path) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            remember.Parameters.AddWithValue("$path", sourceArchiveBackup);
            remember.ExecuteNonQuery();
        }
    }

    private void RejectNewerSchemaOnDisk()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Mode=ReadOnly;Cache=Private;Pooling=False");
        connection.Open();
        RejectNewerSchema(connection);
    }

    private static void RejectNewerSchema(SqliteConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        var userVersion = Convert.ToInt32(version.ExecuteScalar());
        var metaVersion = 0;
        if (TableExists(connection, "meta"))
        {
            using var meta = connection.CreateCommand();
            meta.CommandText = "SELECT value FROM meta WHERE key='schema_version'";
            var stored = meta.ExecuteScalar();
            if (stored is not null && stored != DBNull.Value &&
                !int.TryParse(Convert.ToString(stored, CultureInfo.InvariantCulture), out metaVersion))
                throw new InvalidOperationException("DBのschema versionを読めません。変更せず終了します。");
        }
        var currentVersion = Math.Max(userVersion, metaVersion);
        if (currentVersion > SupportedSchemaVersion)
            throw new InvalidOperationException($"この魔導書DBはSchema v{currentVersion}です。このアプリが対応するのはv{SupportedSchemaVersion}までです。DBを変更せず終了します。新しいアプリで開いてください。");
    }

    private string? EnsureSourceArchiveSafetyBackupBeforeMigration(SqliteConnection connection)
    {
        if (!TableExists(connection, "conversations") || ColumnExists(connection, "conversations", "source_is_archived")) return null;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM conversations";
            if (Convert.ToInt64(count.ExecuteScalar()) == 0) return null;
        }

        if (TableExists(connection, "meta"))
        {
            using var existing = connection.CreateCommand();
            existing.CommandText = "SELECT value FROM meta WHERE key='source_archive_migration_backup_path'";
            if (existing.ExecuteScalar() is string path && IsVerifiedDatabase(path)) return path;
        }

        var directory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var discovered = Directory.GetFiles(directory, "magic-logger-pre-source-archive-*.db")
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault(IsVerifiedDatabase);
        if (discovered is not null) return discovered;

        var destination = AllocateBackupPath(directory, "magic-logger-pre-source-archive");
        CreateVerifiedSnapshot(destination);
        return destination;
    }

    private static bool IsVerifiedDatabase(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0) return false;
        try
        {
            VerifyDatabaseIntegrity(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void EnsurePhase03SafetyBackupBeforeMigration(SqliteConnection connection)
    {
        if (!TableExists(connection, "meta") || !TableExists(connection, "messages")
            || TableExists(connection, "fragment_user_tags")) return;
        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM messages";
            if (Convert.ToInt64(count.ExecuteScalar()) == 0) return;
        }

        using (var existing = connection.CreateCommand())
        {
            existing.CommandText = "SELECT value FROM meta WHERE key='phase03_backup_path'";
            if (existing.ExecuteScalar() is string path && IsVerifiedDatabase(path)) return;
        }

        var directory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var discovered = Directory.GetFiles(directory, "magic-logger-pre-phase03-*.db")
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault(IsVerifiedDatabase);
        var destination = discovered ?? AllocateBackupPath(directory, "magic-logger-pre-phase03");
        if (discovered is null) CreateVerifiedSnapshot(destination);
        using var remember = connection.CreateCommand();
        remember.CommandText = "INSERT INTO meta(key,value) VALUES('phase03_backup_path',$path) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        remember.Parameters.AddWithValue("$path", destination);
        remember.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string definition)
    {
        using var info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({table})";
        using var reader = info.ExecuteReader();
        while (reader.Read())
            if (reader.GetString(1).Equals(column, StringComparison.OrdinalIgnoreCase)) return;
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
    }

    public long BeginImport(string path, string hash, IReadOnlyList<(string Path, string Hash)> files)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO import_runs(source_path,source_hash,status,started_at) VALUES($p,$h,'running',$now); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$p", path);
        command.Parameters.AddWithValue("$h", hash);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        var importId = (long)(command.ExecuteScalar() ?? 0L);
        foreach (var file in files)
        {
            using var fileCommand = connection.CreateCommand();
            fileCommand.Transaction = transaction;
            fileCommand.CommandText = "INSERT INTO import_run_files(import_id,source_path,source_hash,status) VALUES($id,$path,$hash,'pending')";
            fileCommand.Parameters.AddWithValue("$id", importId);
            fileCommand.Parameters.AddWithValue("$path", file.Path);
            fileCommand.Parameters.AddWithValue("$hash", file.Hash);
            fileCommand.ExecuteNonQuery();
        }
        transaction.Commit();
        return importId;
    }

    public void FinishImport(long id, string status, string? error = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE import_runs SET status=$s, finished_at=$now, error_text=$e WHERE import_id=$id";
        command.Parameters.AddWithValue("$s", status);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public void FinishImportFile(long importId, string path, string status, string? error = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE import_run_files
            SET status=$status, error_text=$error, processed_at=$now
            WHERE import_id=$id AND source_path=$path
            """;
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", importId);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ImportErrorSource> GetImportErrors()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current.import_id, current.source_path, current.status,
                   COALESCE(current.error_text, runs.error_text, ''),
                   COALESCE(current.processed_at, runs.finished_at, runs.started_at),
                   CASE WHEN dismissed.key IS NULL THEN 0 ELSE 1 END
            FROM import_run_files AS current
            JOIN import_runs AS runs ON runs.import_id = current.import_id
            LEFT JOIN meta AS dismissed
                ON dismissed.key = 'import_error_dismissed:' || current.import_id || ':' || current.source_path
            WHERE current.import_id = (
                SELECT MAX(latest.import_id)
                FROM import_run_files AS latest
                WHERE latest.source_path = current.source_path COLLATE NOCASE
            )
              AND current.status IN ('failed', 'partial', 'pending')
            ORDER BY current.source_path COLLATE NOCASE
            """;
        using var reader = command.ExecuteReader();
        var errors = new List<ImportErrorSource>();
        while (reader.Read())
            errors.Add(new ImportErrorSource(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)), reader.GetInt64(5) != 0));
        return errors;
    }

    public IReadOnlyList<string> GetRetryableImportFiles() =>
        GetImportErrors().Where(error => !error.Dismissed).Select(error => error.Path).ToList();

    public void DismissImportError(ImportErrorSource error)
    {
        EnsureWritable();
        SetMeta($"import_error_dismissed:{error.ImportId}:{error.Path}", "1");
    }

    public (int MessagesAdded, int FragmentsAdded) SaveConversation(NormalizedConversation conversation, IReadOnlyList<FragmentDraft> drafts)
    {
        var messagesAdded = SaveRawConversation(conversation);
        var fragmentsAdded = SaveAnalysis(conversation.Id, drafts);
        return (messagesAdded, fragmentsAdded);
    }

    public int SaveRawConversation(NormalizedConversation conversation)
        => SaveRawConversationWithResult(conversation).MessagesAdded;

    internal RawConversationSaveResult SaveRawConversationWithResult(NormalizedConversation conversation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var updated = conversation.UpdatedAt.ToString("O");
        var contentChanged = true;
        var existingMessageCount = 0L;
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT title,updated_at,
                       (SELECT COUNT(*) FROM messages WHERE conversation_id=$id)
                FROM conversations WHERE conversation_id=$id;
                """;
            existing.Parameters.AddWithValue("$id", conversation.Id);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                existingMessageCount = reader.GetInt64(2);
                contentChanged = !reader.GetString(0).Equals(conversation.Title, StringComparison.Ordinal)
                    || !reader.GetString(1).Equals(updated, StringComparison.Ordinal);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversations(conversation_id,title,updated_at,imported_at,source_is_archived)
                VALUES($id,$title,$updated,$now,$sourceArchived)
                ON CONFLICT(conversation_id) DO UPDATE SET
                    title=excluded.title,
                    updated_at=excluded.updated_at,
                    source_is_archived=CASE
                        WHEN excluded.source_is_archived IS NULL THEN conversations.source_is_archived
                        ELSE excluded.source_is_archived
                    END;
                """;
            command.Parameters.AddWithValue("$id", conversation.Id);
            command.Parameters.AddWithValue("$title", conversation.Title);
            command.Parameters.AddWithValue("$updated", updated);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$sourceArchived", conversation.SourceIsArchived is bool archived ? archived ? 1 : 0 : DBNull.Value);
            command.ExecuteNonQuery();
        }

        var incomingMessageCount = conversation.Messages.Select(message => message.MessageId).Distinct(StringComparer.Ordinal).Count();
        var messagesAdded = 0;
        foreach (var message in conversation.Messages)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO messages(conversation_id,message_id,parent_message_id,timestamp,role,raw_text,source_hash,imported_at)
                VALUES($cid,$mid,$parent,$time,$role,$raw,$hash,$now);
                """;
            command.Parameters.AddWithValue("$cid", message.ConversationId);
            command.Parameters.AddWithValue("$mid", message.MessageId);
            command.Parameters.AddWithValue("$parent", (object?)message.ParentMessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("$time", message.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$role", message.Role);
            command.Parameters.AddWithValue("$raw", message.RawText);
            command.Parameters.AddWithValue("$hash", message.SourceHash);
            command.Parameters.AddWithValue("$now", now);
            messagesAdded += command.ExecuteNonQuery();
        }

        transaction.Commit();
        contentChanged = contentChanged || messagesAdded > 0 || existingMessageCount != incomingMessageCount;
        return new RawConversationSaveResult(messagesAdded, contentChanged);
    }

    public int SaveAnalysis(IReadOnlyList<FragmentDraft> drafts)
        => SaveAnalysis(drafts.FirstOrDefault()?.ConversationId, drafts);

    public int SaveAnalysis(string? conversationId, IReadOnlyList<FragmentDraft> drafts)
    {
        if (drafts.Count == 0 && string.IsNullOrWhiteSpace(conversationId)) return 0;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var fragmentsAdded = 0;
        foreach (var draft in drafts)
            fragmentsAdded += InsertFragment(connection, transaction, draft, now);
        if (!string.IsNullOrWhiteSpace(conversationId))
            HideObsoleteGeneratedFragments(connection, transaction, conversationId, drafts.Select(draft => draft.Id).ToList(), now);
        transaction.Commit();
        return fragmentsAdded;
    }

    private static void HideObsoleteGeneratedFragments(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        IReadOnlyList<string> keepIds,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var keep = new List<string>();
        for (var i = 0; i < keepIds.Count; i++)
        {
            var name = "$keep" + i;
            keep.Add(name);
            command.Parameters.AddWithValue(name, keepIds[i]);
        }
        var keepClause = keep.Count == 0 ? string.Empty : $"AND fragment_id NOT IN ({string.Join(',', keep)})";
        command.CommandText = $"""
            UPDATE fragments
            SET hidden=1,updated_at=$now
            WHERE source_conversation_id=$conversation
              AND (generation_metadata LIKE '%phase01-v%' OR generation_metadata LIKE '%phase02-v%' OR generation_metadata LIKE '%phase03-v%')
              AND bookmarked=0 AND edited_fields='[]'
              AND NOT EXISTS(SELECT 1 FROM fragment_user_tags ut WHERE ut.fragment_id=fragments.fragment_id)
              AND NOT EXISTS(SELECT 1 FROM fragment_tag_exclusions x WHERE x.fragment_id=fragments.fragment_id)
              AND NOT EXISTS(SELECT 1 FROM fragment_folder_assignments a WHERE a.fragment_id=fragments.fragment_id)
              {keepClause};
            """;
        command.Parameters.AddWithValue("$conversation", conversationId);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static int InsertFragment(SqliteConnection connection, SqliteTransaction transaction, FragmentDraft draft, string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO fragments(fragment_id,source_conversation_id,date,title,excerpt,summary,classification,section_type,created_at,updated_at,generation_metadata)
            VALUES($id,$cid,$date,$title,$excerpt,$summary,$class,$section,$now,$now,$meta);
            """;
        command.Parameters.AddWithValue("$id", draft.Id);
        command.Parameters.AddWithValue("$cid", draft.ConversationId);
        command.Parameters.AddWithValue("$date", draft.Date.ToString("O"));
        command.Parameters.AddWithValue("$title", draft.Title);
        command.Parameters.AddWithValue("$excerpt", draft.Excerpt);
        command.Parameters.AddWithValue("$summary", draft.Summary);
        command.Parameters.AddWithValue("$class", draft.Classification.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$section", draft.SectionType.ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$meta", draft.GenerationMetadata);
        var added = command.ExecuteNonQuery();

        if (added == 0)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE fragments SET
                    title=CASE WHEN edited_fields LIKE '%"title"%' THEN title ELSE $title END,
                    excerpt=CASE WHEN edited_fields LIKE '%"excerpt"%' THEN excerpt ELSE $excerpt END,
                    summary=CASE WHEN edited_fields LIKE '%"summary"%' THEN summary ELSE $summary END,
                    classification=$class,
                    section_type=CASE WHEN edited_fields LIKE '%"section_type"%' THEN section_type ELSE $section END,
                    updated_at=$now,
                    generation_metadata=$meta
                WHERE fragment_id=$id;
                """;
            update.Parameters.AddWithValue("$title", draft.Title);
            update.Parameters.AddWithValue("$excerpt", draft.Excerpt);
            update.Parameters.AddWithValue("$summary", draft.Summary);
            update.Parameters.AddWithValue("$class", draft.Classification.ToString().ToUpperInvariant());
            update.Parameters.AddWithValue("$section", draft.SectionType.ToString().ToUpperInvariant());
            update.Parameters.AddWithValue("$now", now);
            update.Parameters.AddWithValue("$meta", draft.GenerationMetadata);
            update.Parameters.AddWithValue("$id", draft.Id);
            update.ExecuteNonQuery();
        }

        var excerptEdited = IsFieldEdited(connection, transaction, draft.Id, "excerpt");
        if (!excerptEdited)
        {
            using var removeLinks = connection.CreateCommand();
            removeLinks.Transaction = transaction;
            removeLinks.CommandText = "DELETE FROM fragment_messages WHERE fragment_id=$id";
            removeLinks.Parameters.AddWithValue("$id", draft.Id);
            removeLinks.ExecuteNonQuery();
        }
        for (var i = 0; i < draft.SourceMessageIds.Count; i++)
        {
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = """
                INSERT INTO fragment_messages(fragment_id,conversation_id,message_id,sequence_no)
                VALUES($f,$c,$m,$s)
                ON CONFLICT(fragment_id,message_id) DO UPDATE SET sequence_no=excluded.sequence_no;
                """;
            link.Parameters.AddWithValue("$f", draft.Id);
            link.Parameters.AddWithValue("$c", draft.ConversationId);
            link.Parameters.AddWithValue("$m", draft.SourceMessageIds[i]);
            link.Parameters.AddWithValue("$s", i);
            link.ExecuteNonQuery();
        }
        if (!IsFieldEdited(connection, transaction, draft.Id, "keywords"))
        {
            using var deleteKeywords = connection.CreateCommand();
            deleteKeywords.Transaction = transaction;
            deleteKeywords.CommandText = "DELETE FROM fragment_keywords WHERE fragment_id=$id";
            deleteKeywords.Parameters.AddWithValue("$id", draft.Id);
            deleteKeywords.ExecuteNonQuery();
            SaveKeywordLinks(connection, transaction, draft.Id, draft.Keywords);
        }
        SaveNamedLinks(connection, transaction, draft.Id, "categories", "category", draft.Categories);
        SaveNamedLinks(connection, transaction, draft.Id, "tags", "tag", draft.Tags);
        RefreshFts(connection, transaction, draft.Id);
        RefreshSearchFts(connection, transaction, draft.Id);
        return added;
    }

    private static bool IsFieldEdited(SqliteConnection connection, SqliteTransaction transaction, string fragmentId, string field)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT edited_fields FROM fragments WHERE fragment_id=$id";
        command.Parameters.AddWithValue("$id", fragmentId);
        var json = command.ExecuteScalar() as string ?? "[]";
        return (JsonSerializer.Deserialize<HashSet<string>>(json) ?? []).Contains(field);
    }

    private static void SaveKeywordLinks(SqliteConnection connection, SqliteTransaction transaction, string fragmentId, IReadOnlyList<string> keywords)
    {
        for (var i = 0; i < keywords.Count; i++)
        {
            var display = keywords[i].Trim();
            if (display.Length == 0) continue;
            using var add = connection.CreateCommand();
            add.Transaction = transaction;
            add.CommandText = "INSERT INTO keywords(normalized,display) VALUES($n,$d) ON CONFLICT(normalized) DO UPDATE SET display=excluded.display;";
            add.Parameters.AddWithValue("$n", display.ToLowerInvariant());
            add.Parameters.AddWithValue("$d", display);
            add.ExecuteNonQuery();
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO fragment_keywords(fragment_id,keyword_id,sequence_no) SELECT $f,keyword_id,$s FROM keywords WHERE normalized=$n";
            link.Parameters.AddWithValue("$f", fragmentId);
            link.Parameters.AddWithValue("$s", i);
            link.Parameters.AddWithValue("$n", display.ToLowerInvariant());
            link.ExecuteNonQuery();

            using var promotion = connection.CreateCommand();
            promotion.Transaction = transaction;
            promotion.CommandText = """
                INSERT OR IGNORE INTO fragment_user_tags(fragment_id,tag_id,source_kind,sequence_no,created_at)
                SELECT $f,p.tag_id,'INDEX',$s,$now
                FROM promoted_index_tags p JOIN tags t ON t.tag_id=p.tag_id
                WHERE t.normalized=$n;
                """;
            promotion.Parameters.AddWithValue("$f", fragmentId);
            promotion.Parameters.AddWithValue("$s", i);
            promotion.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            promotion.Parameters.AddWithValue("$n", display.ToLowerInvariant());
            promotion.ExecuteNonQuery();
        }
    }

    private static void SaveNamedLinks(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fragmentId,
        string pluralTable,
        string singular,
        IReadOnlyList<string> values)
    {
        var linkTable = $"fragment_{pluralTable}";
        var idColumn = $"{singular}_id";
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {linkTable} WHERE fragment_id=$id";
            delete.Parameters.AddWithValue("$id", fragmentId);
            delete.ExecuteNonQuery();
        }
        for (var i = 0; i < values.Count; i++)
        {
            var display = values[i].Trim();
            if (display.Length == 0) continue;
            var normalized = display.ToLowerInvariant();
            using (var add = connection.CreateCommand())
            {
                add.Transaction = transaction;
                add.CommandText = pluralTable == "tags"
                    ? "INSERT INTO tags(normalized,display) VALUES($n,$d) ON CONFLICT(normalized) DO NOTHING"
                    : $"INSERT INTO {pluralTable}(normalized,display) VALUES($n,$d) ON CONFLICT(normalized) DO UPDATE SET display=excluded.display";
                add.Parameters.AddWithValue("$n", normalized);
                add.Parameters.AddWithValue("$d", display);
                add.ExecuteNonQuery();
            }
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = $"INSERT OR IGNORE INTO {linkTable}(fragment_id,{idColumn},sequence_no) SELECT $f,{idColumn},$s FROM {pluralTable} WHERE normalized=$n";
            link.Parameters.AddWithValue("$f", fragmentId);
            link.Parameters.AddWithValue("$s", i);
            link.Parameters.AddWithValue("$n", normalized);
            link.ExecuteNonQuery();
        }
    }

    private static void RefreshFts(SqliteConnection connection, SqliteTransaction transaction, string fragmentId)
    {
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM fragment_fts WHERE fragment_id=$id";
        delete.Parameters.AddWithValue("$id", fragmentId);
        delete.ExecuteNonQuery();
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO fragment_fts(fragment_id,title,excerpt,summary,keywords)
            SELECT f.fragment_id,f.title,f.excerpt,f.summary,COALESCE(group_concat(k.display,' '),'')
            FROM fragments f LEFT JOIN fragment_keywords fk ON fk.fragment_id=f.fragment_id
            LEFT JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE f.fragment_id=$id GROUP BY f.fragment_id;
            """;
        insert.Parameters.AddWithValue("$id", fragmentId);
        insert.ExecuteNonQuery();
    }

    private static void RefreshSearchFts(SqliteConnection connection, SqliteTransaction transaction, string fragmentId)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM fragment_search_fts WHERE fragment_id=$id";
            delete.Parameters.AddWithValue("$id", fragmentId);
            delete.ExecuteNonQuery();
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO fragment_search_fts(fragment_id,title,summary,raw_text,categories,tags,index_words)
            SELECT f.fragment_id, f.title, f.summary,
                   COALESCE((SELECT group_concat(m.raw_text, char(10))
                       FROM fragment_messages fm JOIN messages m
                         ON m.conversation_id=fm.conversation_id AND m.message_id=fm.message_id
                       WHERE fm.fragment_id=f.fragment_id), ''),
                   COALESCE((SELECT group_concat(c.display,' ') FROM fragment_categories fc
                       JOIN categories c ON c.category_id=fc.category_id WHERE fc.fragment_id=f.fragment_id), ''),
                   COALESCE((SELECT group_concat(display,' ') FROM (
                       SELECT t.display,t.normalized FROM fragment_tags ft
                       JOIN tags t ON t.tag_id=ft.tag_id
                       WHERE ft.fragment_id=f.fragment_id AND NOT EXISTS(
                           SELECT 1 FROM fragment_tag_exclusions x
                           WHERE x.fragment_id=ft.fragment_id AND x.tag_id=ft.tag_id)
                       UNION
                       SELECT t.display,t.normalized FROM fragment_user_tags ut
                       JOIN tags t ON t.tag_id=ut.tag_id WHERE ut.fragment_id=f.fragment_id)), ''),
                   COALESCE((SELECT group_concat(k.display,' ') FROM fragment_keywords fk
                       JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE fk.fragment_id=f.fragment_id), '')
            FROM fragments f WHERE f.fragment_id=$id;
            """;
        insert.Parameters.AddWithValue("$id", fragmentId);
        insert.ExecuteNonQuery();
    }

    private static void RebuildSearchFts(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText = """
            DELETE FROM fragment_search_fts;
            INSERT INTO fragment_search_fts(fragment_id,title,summary,raw_text,categories,tags,index_words)
            SELECT f.fragment_id, f.title, f.summary,
                   COALESCE((SELECT group_concat(m.raw_text, char(10))
                       FROM fragment_messages fm JOIN messages m
                         ON m.conversation_id=fm.conversation_id AND m.message_id=fm.message_id
                       WHERE fm.fragment_id=f.fragment_id), ''),
                   COALESCE((SELECT group_concat(c.display,' ') FROM fragment_categories fc
                       JOIN categories c ON c.category_id=fc.category_id WHERE fc.fragment_id=f.fragment_id), ''),
                   COALESCE((SELECT group_concat(display,' ') FROM (
                       SELECT t.display,t.normalized FROM fragment_tags ft
                       JOIN tags t ON t.tag_id=ft.tag_id
                       WHERE ft.fragment_id=f.fragment_id AND NOT EXISTS(
                           SELECT 1 FROM fragment_tag_exclusions x
                           WHERE x.fragment_id=ft.fragment_id AND x.tag_id=ft.tag_id)
                       UNION
                       SELECT t.display,t.normalized FROM fragment_user_tags ut
                       JOIN tags t ON t.tag_id=ut.tag_id WHERE ut.fragment_id=f.fragment_id)), ''),
                   COALESCE((SELECT group_concat(k.display,' ') FROM fragment_keywords fk
                       JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE fk.fragment_id=f.fragment_id), '')
            FROM fragments f;
            """;
        rebuild.ExecuteNonQuery();
    }

    public IReadOnlyList<FragmentRecord> GetFragments(bool includeHidden = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FragmentSelectColumns}
            FROM fragments f
            WHERE ($hidden=1 OR f.hidden=0) AND f.classification IN ('PERSONAL','MIXED') AND f.section_type='MAIN'
            ORDER BY f.date ASC, f.fragment_id ASC;
            """;
        command.Parameters.AddWithValue("$hidden", includeHidden ? 1 : 0);
        using var reader = command.ExecuteReader();
        var result = new List<FragmentRecord>();
        while (reader.Read()) result.Add(ReadFragment(reader));
        return result;
    }

    public FragmentRecord? GetFragment(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FragmentSelectColumns}
            FROM fragments f WHERE f.fragment_id=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadFragment(reader) : null;
    }

    private static FragmentRecord ReadFragment(SqliteDataReader reader)
    {
        var tags = SplitValues(reader.GetString(13));
        return new(reader.GetString(0), reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), Enum.Parse<ArchiveClassification>(reader.GetString(6), true),
            SplitValues(reader.GetString(11)),
            reader.GetInt64(7) != 0, reader.GetInt64(8) != 0, reader.GetString(9), reader.GetString(10))
        {
            Categories = SplitValues(reader.GetString(12)),
            Tags = tags,
            SectionType = Enum.Parse<FragmentSectionType>(reader.GetString(14), true),
            AutoTags = SplitValues(reader.GetString(15)),
            UserTags = SplitValues(reader.GetString(16)),
            ExcludedAutoTags = SplitValues(reader.GetString(17)),
            PromotedIndexTags = SplitValues(reader.GetString(18)),
            BookmarkFolderId = reader.IsDBNull(19) ? null : reader.GetInt64(19),
            BookmarkFolderName = reader.IsDBNull(20) ? null : reader.GetString(20),
            ReferenceFolderId = reader.IsDBNull(21) ? null : reader.GetInt64(21),
            ReferenceFolderName = reader.IsDBNull(22) ? null : reader.GetString(22)
        };
    }

    private static IReadOnlyList<string> SplitValues(string value) =>
        value.Split((char)31, StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public IReadOnlyList<SourceMessage> GetSourceMessages(string fragmentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.conversation_id,c.title,m.message_id,m.parent_message_id,m.timestamp,m.role,m.raw_text,m.source_hash
            FROM fragment_messages fm JOIN messages m ON m.conversation_id=fm.conversation_id AND m.message_id=fm.message_id
            JOIN conversations c ON c.conversation_id=m.conversation_id
            WHERE fm.fragment_id=$id ORDER BY fm.sequence_no;
            """;
        command.Parameters.AddWithValue("$id", fragmentId);
        using var reader = command.ExecuteReader();
        var result = new List<SourceMessage>();
        while (reader.Read())
            result.Add(new SourceMessage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4)), reader.GetString(5), reader.GetString(6), reader.GetString(7)));
        return result;
    }

    public bool? GetChatGptSourceArchiveState(string conversationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_is_archived FROM conversations WHERE conversation_id=$id";
        command.Parameters.AddWithValue("$id", conversationId);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value) != 0;
    }

    public void SaveFragmentText(string fragmentId, string field, string value)
    {
        if (field is not ("title" or "summary" or "excerpt")) throw new ArgumentOutOfRangeException(nameof(field));
        if (field == "excerpt" && !GetSourceMessages(fragmentId).Any(m => m.RawText.Contains(value, StringComparison.Ordinal)))
            throw new InvalidOperationException("代表引用は原文から選んでください。");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string oldValue;
        string editedJson;
        using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = $"SELECT {field},edited_fields FROM fragments WHERE fragment_id=$id";
            get.Parameters.AddWithValue("$id", fragmentId);
            using var reader = get.ExecuteReader();
            if (!reader.Read()) return;
            oldValue = reader.GetString(0);
            editedJson = reader.GetString(1);
        }
        if (oldValue == value) return;
        var edited = JsonSerializer.Deserialize<HashSet<string>>(editedJson) ?? [];
        edited.Add(field);
        using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = "INSERT INTO fragment_revisions(fragment_id,field_name,old_value,new_value,saved_at) VALUES($id,$field,$old,$new,$now)";
            revision.Parameters.AddWithValue("$id", fragmentId);
            revision.Parameters.AddWithValue("$field", field);
            revision.Parameters.AddWithValue("$old", oldValue);
            revision.Parameters.AddWithValue("$new", value);
            revision.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            revision.ExecuteNonQuery();
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"UPDATE fragments SET {field}=$value,edited_fields=$edited,updated_at=$now WHERE fragment_id=$id";
            update.Parameters.AddWithValue("$value", value);
            update.Parameters.AddWithValue("$edited", JsonSerializer.Serialize(edited));
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", fragmentId);
            update.ExecuteNonQuery();
        }
        RefreshFts(connection, transaction, fragmentId);
        RefreshSearchFts(connection, transaction, fragmentId);
        transaction.Commit();
    }

    public void SaveKeywords(string fragmentId, IReadOnlyList<string> keywords)
    {
        var cleaned = keywords.Select(k => k.Trim()).Where(k => k.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (cleaned.Count < 1) throw new InvalidOperationException("索引語を1語以上入力してください。");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM fragment_keywords WHERE fragment_id=$id";
            delete.Parameters.AddWithValue("$id", fragmentId);
            delete.ExecuteNonQuery();
        }
        SaveKeywordLinks(connection, transaction, fragmentId, cleaned);
        MarkEdited(connection, transaction, fragmentId, "keywords");
        RefreshFts(connection, transaction, fragmentId);
        RefreshSearchFts(connection, transaction, fragmentId);
        transaction.Commit();
    }

    public IReadOnlyList<string> GetTagSuggestions(int limit = 80)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.display
            FROM tags t
            LEFT JOIN fragment_user_tags ut ON ut.tag_id=t.tag_id
            LEFT JOIN fragment_tags ft ON ft.tag_id=t.tag_id
            GROUP BY t.tag_id,t.display
            ORDER BY COUNT(ut.fragment_id) DESC,COUNT(ft.fragment_id) DESC,t.display
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    public void AddUserTag(string fragmentId, string value)
    {
        var display = CleanLabel(value, "Tag");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var tagId = GetOrCreateTag(connection, transaction, display);
        using (var add = connection.CreateCommand())
        {
            add.Transaction = transaction;
            add.CommandText = """
                INSERT INTO fragment_user_tags(fragment_id,tag_id,source_kind,sequence_no,created_at)
                VALUES($id,$tag,'MANUAL',
                    COALESCE((SELECT MAX(sequence_no)+1 FROM fragment_user_tags WHERE fragment_id=$id),0),$now)
                ON CONFLICT(fragment_id,tag_id) DO UPDATE SET source_kind='MANUAL';
                """;
            add.Parameters.AddWithValue("$id", fragmentId);
            add.Parameters.AddWithValue("$tag", tagId);
            add.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            add.ExecuteNonQuery();
        }
        MarkEdited(connection, transaction, fragmentId, "tags");
        RefreshSearchFts(connection, transaction, fragmentId);
        transaction.Commit();
    }

    public void RemoveUserTag(string fragmentId, string value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var remove = connection.CreateCommand();
        remove.Transaction = transaction;
        remove.CommandText = """
            DELETE FROM fragment_user_tags
            WHERE fragment_id=$id AND tag_id=(SELECT tag_id FROM tags WHERE normalized=$tag);
            """;
        remove.Parameters.AddWithValue("$id", fragmentId);
        remove.Parameters.AddWithValue("$tag", NormalizeLabel(value));
        if (remove.ExecuteNonQuery() > 0)
        {
            MarkEdited(connection, transaction, fragmentId, "tags");
            RefreshSearchFts(connection, transaction, fragmentId);
        }
        transaction.Commit();
    }

    public void ExcludeAutoTag(string fragmentId, string value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var exclude = connection.CreateCommand();
        exclude.Transaction = transaction;
        exclude.CommandText = """
            INSERT OR IGNORE INTO fragment_tag_exclusions(fragment_id,tag_id,excluded_at)
            SELECT $id,ft.tag_id,$now FROM fragment_tags ft JOIN tags t ON t.tag_id=ft.tag_id
            WHERE ft.fragment_id=$id AND t.normalized=$tag;
            """;
        exclude.Parameters.AddWithValue("$id", fragmentId);
        exclude.Parameters.AddWithValue("$tag", NormalizeLabel(value));
        exclude.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        if (exclude.ExecuteNonQuery() > 0)
        {
            MarkEdited(connection, transaction, fragmentId, "tags");
            RefreshSearchFts(connection, transaction, fragmentId);
        }
        transaction.Commit();
    }

    public void RestoreAutoTag(string fragmentId, string value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var restore = connection.CreateCommand();
        restore.Transaction = transaction;
        restore.CommandText = """
            DELETE FROM fragment_tag_exclusions
            WHERE fragment_id=$id AND tag_id=(SELECT tag_id FROM tags WHERE normalized=$tag);
            """;
        restore.Parameters.AddWithValue("$id", fragmentId);
        restore.Parameters.AddWithValue("$tag", NormalizeLabel(value));
        if (restore.ExecuteNonQuery() > 0)
        {
            MarkEdited(connection, transaction, fragmentId, "tags");
            RefreshSearchFts(connection, transaction, fragmentId);
        }
        transaction.Commit();
    }

    public void MoveUserTagToIndex(string fragmentId, string value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var normalized = NormalizeLabel(value);
        string? display;
        using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = """
                SELECT t.display FROM fragment_user_tags ut JOIN tags t ON t.tag_id=ut.tag_id
                WHERE ut.fragment_id=$id AND t.normalized=$tag;
                """;
            get.Parameters.AddWithValue("$id", fragmentId);
            get.Parameters.AddWithValue("$tag", normalized);
            display = get.ExecuteScalar() as string;
        }
        if (display is null) { transaction.Commit(); return; }
        using (var word = connection.CreateCommand())
        {
            word.Transaction = transaction;
            word.CommandText = "INSERT INTO keywords(normalized,display) VALUES($n,$d) ON CONFLICT(normalized) DO NOTHING";
            word.Parameters.AddWithValue("$n", normalized);
            word.Parameters.AddWithValue("$d", display);
            word.ExecuteNonQuery();
        }
        using (var link = connection.CreateCommand())
        {
            link.Transaction = transaction;
            link.CommandText = """
                INSERT OR IGNORE INTO fragment_keywords(fragment_id,keyword_id,sequence_no)
                SELECT $id,keyword_id,COALESCE((SELECT MAX(sequence_no)+1 FROM fragment_keywords WHERE fragment_id=$id),0)
                FROM keywords WHERE normalized=$tag;
                DELETE FROM fragment_user_tags
                WHERE fragment_id=$id AND tag_id=(SELECT tag_id FROM tags WHERE normalized=$tag);
                """;
            link.Parameters.AddWithValue("$id", fragmentId);
            link.Parameters.AddWithValue("$tag", normalized);
            link.ExecuteNonQuery();
        }
        MarkEdited(connection, transaction, fragmentId, "keywords");
        MarkEdited(connection, transaction, fragmentId, "tags");
        RefreshFts(connection, transaction, fragmentId);
        RefreshSearchFts(connection, transaction, fragmentId);
        transaction.Commit();
    }

    public bool IsIndexWordPromoted(string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM promoted_index_tags p JOIN tags t ON t.tag_id=p.tag_id
            WHERE t.normalized=$tag;
            """;
        command.Parameters.AddWithValue("$tag", NormalizeLabel(value));
        return command.ExecuteScalar() is not null;
    }

    public int PromoteIndexWordToTag(string value)
    {
        var display = CleanLabel(value, "Index Word");
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var tagId = GetOrCreateTag(connection, transaction, display);
        using (var rule = connection.CreateCommand())
        {
            rule.Transaction = transaction;
            rule.CommandText = "INSERT OR IGNORE INTO promoted_index_tags(tag_id,created_at) VALUES($tag,$now)";
            rule.Parameters.AddWithValue("$tag", tagId);
            rule.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            rule.ExecuteNonQuery();
        }
        var affected = new List<string>();
        using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = "SELECT DISTINCT fk.fragment_id FROM fragment_keywords fk JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE k.normalized=$tag";
            get.Parameters.AddWithValue("$tag", NormalizeLabel(display));
            using var reader = get.ExecuteReader();
            while (reader.Read()) affected.Add(reader.GetString(0));
        }
        using (var add = connection.CreateCommand())
        {
            add.Transaction = transaction;
            add.CommandText = """
                INSERT OR IGNORE INTO fragment_user_tags(fragment_id,tag_id,source_kind,sequence_no,created_at)
                SELECT fk.fragment_id,$tag,'INDEX',fk.sequence_no,$now
                FROM fragment_keywords fk JOIN keywords k ON k.keyword_id=fk.keyword_id
                WHERE k.normalized=$normalized;
                """;
            add.Parameters.AddWithValue("$tag", tagId);
            add.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            add.Parameters.AddWithValue("$normalized", NormalizeLabel(display));
            add.ExecuteNonQuery();
        }
        foreach (var id in affected)
        {
            MarkEdited(connection, transaction, id, "tags");
            RefreshSearchFts(connection, transaction, id);
        }
        transaction.Commit();
        return affected.Count;
    }

    public int DemoteIndexWordTag(string value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var normalized = NormalizeLabel(value);
        var affected = new List<string>();
        using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = """
                SELECT ut.fragment_id FROM fragment_user_tags ut JOIN tags t ON t.tag_id=ut.tag_id
                WHERE ut.source_kind='INDEX' AND t.normalized=$tag;
                """;
            get.Parameters.AddWithValue("$tag", normalized);
            using var reader = get.ExecuteReader();
            while (reader.Read()) affected.Add(reader.GetString(0));
        }
        using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = """
                DELETE FROM fragment_user_tags
                WHERE source_kind='INDEX' AND tag_id=(SELECT tag_id FROM tags WHERE normalized=$tag);
                DELETE FROM promoted_index_tags
                WHERE tag_id=(SELECT tag_id FROM tags WHERE normalized=$tag);
                """;
            remove.Parameters.AddWithValue("$tag", normalized);
            remove.ExecuteNonQuery();
        }
        foreach (var id in affected) RefreshSearchFts(connection, transaction, id);
        transaction.Commit();
        return affected.Count;
    }

    private static long GetOrCreateTag(SqliteConnection connection, SqliteTransaction transaction, string display)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tags(normalized,display) VALUES($n,$d)
            ON CONFLICT(normalized) DO NOTHING;
            SELECT tag_id FROM tags WHERE normalized=$n;
            """;
        command.Parameters.AddWithValue("$n", NormalizeLabel(display));
        command.Parameters.AddWithValue("$d", display);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string CleanLabel(string value, string label)
    {
        var display = value.Trim().TrimStart('#').Trim();
        if (display.Length == 0) throw new InvalidOperationException($"{label}を入力してください。");
        if (display.Length > 60) throw new InvalidOperationException($"{label}は60文字以内にしてください。");
        return display;
    }

    private static string NormalizeLabel(string value) => value.Trim().TrimStart('#').Trim().ToLowerInvariant();

    private static void MarkEdited(SqliteConnection connection, SqliteTransaction transaction, string fragmentId, string field)
    {
        string json;
        using (var get = connection.CreateCommand())
        {
            get.Transaction = transaction;
            get.CommandText = "SELECT edited_fields FROM fragments WHERE fragment_id=$id";
            get.Parameters.AddWithValue("$id", fragmentId);
            json = (string?)get.ExecuteScalar() ?? "[]";
        }
        var edited = JsonSerializer.Deserialize<HashSet<string>>(json) ?? [];
        edited.Add(field);
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE fragments SET edited_fields=$e,updated_at=$now WHERE fragment_id=$id";
        update.Parameters.AddWithValue("$e", JsonSerializer.Serialize(edited));
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", fragmentId);
        update.ExecuteNonQuery();
    }

    public bool ToggleFlag(string fragmentId, string field)
    {
        if (field is not ("bookmarked" or "hidden")) throw new ArgumentOutOfRangeException(nameof(field));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE fragments SET {field}=CASE {field} WHEN 0 THEN 1 ELSE 0 END,updated_at=$now WHERE fragment_id=$id; SELECT {field} FROM fragments WHERE fragment_id=$id;";
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", fragmentId);
        var value = Convert.ToInt64(update.ExecuteScalar()) != 0;
        if (field == "bookmarked" && !value)
        {
            using var clearFolder = connection.CreateCommand();
            clearFolder.Transaction = transaction;
            clearFolder.CommandText = "DELETE FROM fragment_folder_assignments WHERE fragment_id=$id AND folder_kind='BOOKMARK'";
            clearFolder.Parameters.AddWithValue("$id", fragmentId);
            clearFolder.ExecuteNonQuery();
        }
        MarkEdited(connection, transaction, fragmentId, field);
        transaction.Commit();
        return value;
    }

    public void MoveFragmentToSection(string fragmentId, FragmentSectionType sectionType)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE fragments SET section_type=$section,updated_at=$now WHERE fragment_id=$id";
        update.Parameters.AddWithValue("$section", sectionType.ToString().ToUpperInvariant());
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", fragmentId);
        if (update.ExecuteNonQuery() == 0) throw new InvalidOperationException("断章が見つかりません。");
        if (sectionType == FragmentSectionType.Main)
        {
            using var clearFolder = connection.CreateCommand();
            clearFolder.Transaction = transaction;
            clearFolder.CommandText = "DELETE FROM fragment_folder_assignments WHERE fragment_id=$id AND folder_kind='REFERENCE'";
            clearFolder.Parameters.AddWithValue("$id", fragmentId);
            clearFolder.ExecuteNonQuery();
        }
        MarkEdited(connection, transaction, fragmentId, "section_type");
        transaction.Commit();
    }

    public long CreateFolder(CollectionFolderKind kind, string name)
    {
        var display = CleanLabel(name, kind == CollectionFolderKind.Bookmark ? "しおりフォルダ" : "資料フォルダ");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO collection_folders(folder_kind,normalized,name,created_at)
            VALUES($kind,$normalized,$name,$now)
            ON CONFLICT(folder_kind,normalized) DO UPDATE SET name=excluded.name;
            SELECT folder_id FROM collection_folders WHERE folder_kind=$kind AND normalized=$normalized;
            """;
        command.Parameters.AddWithValue("$kind", FolderKindValue(kind));
        command.Parameters.AddWithValue("$normalized", NormalizeLabel(display));
        command.Parameters.AddWithValue("$name", display);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void AssignFolder(string fragmentId, CollectionFolderKind kind, long? folderId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (folderId is not null)
        {
            using var validate = connection.CreateCommand();
            validate.Transaction = transaction;
            validate.CommandText = "SELECT 1 FROM collection_folders WHERE folder_id=$folder AND folder_kind=$kind";
            validate.Parameters.AddWithValue("$folder", folderId.Value);
            validate.Parameters.AddWithValue("$kind", FolderKindValue(kind));
            if (validate.ExecuteScalar() is null) throw new InvalidOperationException("フォルダが見つかりません。");
        }
        using (var remove = connection.CreateCommand())
        {
            remove.Transaction = transaction;
            remove.CommandText = "DELETE FROM fragment_folder_assignments WHERE fragment_id=$id AND folder_kind=$kind";
            remove.Parameters.AddWithValue("$id", fragmentId);
            remove.Parameters.AddWithValue("$kind", FolderKindValue(kind));
            remove.ExecuteNonQuery();
        }
        if (folderId is not null)
        {
            using var assign = connection.CreateCommand();
            assign.Transaction = transaction;
            assign.CommandText = """
                INSERT INTO fragment_folder_assignments(fragment_id,folder_kind,folder_id,assigned_at)
                VALUES($id,$kind,$folder,$now);
                """;
            assign.Parameters.AddWithValue("$id", fragmentId);
            assign.Parameters.AddWithValue("$kind", FolderKindValue(kind));
            assign.Parameters.AddWithValue("$folder", folderId.Value);
            assign.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            assign.ExecuteNonQuery();
        }
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = kind == CollectionFolderKind.Bookmark
                ? "UPDATE fragments SET bookmarked=1,updated_at=$now WHERE fragment_id=$id"
                : "UPDATE fragments SET section_type='REFERENCE',updated_at=$now WHERE fragment_id=$id";
            update.Parameters.AddWithValue("$id", fragmentId);
            update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            if (update.ExecuteNonQuery() == 0) throw new InvalidOperationException("断章が見つかりません。");
        }
        MarkEdited(connection, transaction, fragmentId, kind == CollectionFolderKind.Bookmark ? "bookmarked" : "section_type");
        transaction.Commit();
    }

    public CollectionFolderOverview GetFolderOverview(CollectionFolderKind kind)
    {
        using var connection = Open();
        var kindValue = FolderKindValue(kind);
        var baseScope = kind == CollectionFolderKind.Bookmark
            ? "f.hidden=0 AND f.bookmarked=1 AND f.section_type='MAIN' AND f.classification IN ('PERSONAL','MIXED')"
            : "f.hidden=0 AND f.section_type='REFERENCE' AND f.classification IN ('PERSONAL','MIXED')";
        int total;
        int unfiled;
        using (var counts = connection.CreateCommand())
        {
            counts.CommandText = $"""
                SELECT COUNT(*),SUM(CASE WHEN NOT EXISTS(
                    SELECT 1 FROM fragment_folder_assignments a
                    WHERE a.fragment_id=f.fragment_id AND a.folder_kind=$kind) THEN 1 ELSE 0 END)
                FROM fragments f WHERE {baseScope};
                """;
            counts.Parameters.AddWithValue("$kind", kindValue);
            using var reader = counts.ExecuteReader();
            reader.Read();
            total = reader.GetInt32(0);
            unfiled = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        }
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT d.folder_id,d.name,COUNT(f.fragment_id)
            FROM collection_folders d
            LEFT JOIN fragment_folder_assignments a ON a.folder_id=d.folder_id AND a.folder_kind=d.folder_kind
            LEFT JOIN fragments f ON f.fragment_id=a.fragment_id AND {baseScope}
            WHERE d.folder_kind=$kind
            GROUP BY d.folder_id,d.name
            ORDER BY COUNT(f.fragment_id) DESC,d.name;
            """;
        command.Parameters.AddWithValue("$kind", kindValue);
        using var folderReader = command.ExecuteReader();
        var folders = new List<CollectionFolder>();
        while (folderReader.Read()) folders.Add(new CollectionFolder(folderReader.GetInt64(0), folderReader.GetString(1), folderReader.GetInt32(2)));
        return new CollectionFolderOverview(total, unfiled, folders);
    }

    public void DeleteFolder(CollectionFolderKind kind, long folderId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM collection_folders WHERE folder_id=$folder AND folder_kind=$kind";
        command.Parameters.AddWithValue("$folder", folderId);
        command.Parameters.AddWithValue("$kind", FolderKindValue(kind));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static string FolderKindValue(CollectionFolderKind kind) => kind.ToString().ToUpperInvariant();

    public IReadOnlyList<MonthCount> GetMonthCounts()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT strftime('%Y-%m', date, 'localtime'), COUNT(*)
            FROM fragments
            WHERE hidden=0 AND section_type='MAIN' AND classification IN ('PERSONAL','MIXED')
            GROUP BY strftime('%Y-%m', date, 'localtime')
            ORDER BY strftime('%Y-%m', date, 'localtime') DESC;
            """;
        using var reader = command.ExecuteReader();
        var result = new List<MonthCount>();
        while (reader.Read()) result.Add(new MonthCount(reader.GetString(0), reader.GetInt32(1)));
        return result;
    }

    public string? GetLatestMonth()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT strftime('%Y-%m', MAX(date), 'localtime') FROM fragments WHERE hidden=0 AND section_type='MAIN' AND classification IN ('PERSONAL','MIXED')";
        return command.ExecuteScalar() as string;
    }

    public string? GetFragmentMonth(string fragmentId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT strftime('%Y-%m', date, 'localtime') FROM fragments WHERE fragment_id=$id AND hidden=0 AND section_type='MAIN'";
        command.Parameters.AddWithValue("$id", fragmentId);
        return command.ExecuteScalar() as string;
    }

    public FragmentPage QueryFragments(FragmentQuery query)
    {
        using var connection = Open();
        using var count = connection.CreateCommand();
        var where = BuildScope(count, query);
        count.CommandText = $"SELECT COUNT(*) FROM fragments f WHERE {where}";
        var total = Convert.ToInt32(count.ExecuteScalar());

        using var command = connection.CreateCommand();
        where = BuildScope(command, query);
        command.CommandText = $"""
            SELECT {FragmentSelectColumns}
            FROM fragments f
            WHERE {where}
            ORDER BY f.date {(query.NewestFirst ? "DESC" : "ASC")}, f.fragment_id {(query.NewestFirst ? "DESC" : "ASC")}
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 100));
        command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        using var reader = command.ExecuteReader();
        var items = new List<FragmentRecord>();
        while (reader.Read()) items.Add(ReadFragment(reader));
        return new FragmentPage(items, total);
    }

    public int CountFragments(FragmentQuery query)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        var where = BuildScope(command, query);
        command.CommandText = $"SELECT COUNT(*) FROM fragments f WHERE {where}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int GetFragmentOffset(string fragmentId, FragmentQuery query)
    {
        var target = GetFragment(fragmentId);
        if (target is null) return 0;
        using var connection = Open();
        using var command = connection.CreateCommand();
        var scoped = query with { Offset = 0, Limit = 1 };
        var where = BuildScope(command, scoped);
        var comparison = query.NewestFirst
            ? "(f.date>$date OR (f.date=$date AND f.fragment_id>$id))"
            : "(f.date<$date OR (f.date=$date AND f.fragment_id<$id))";
        command.CommandText = $"SELECT COUNT(*) FROM fragments f WHERE {where} AND {comparison}";
        command.Parameters.AddWithValue("$date", target.Date.ToString("O"));
        command.Parameters.AddWithValue("$id", target.Id);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public string? GetAdjacentFragmentId(string fragmentId, FragmentQuery query)
    {
        var target = GetFragment(fragmentId);
        if (target is null) return null;
        using var connection = Open();
        var scoped = query with { Offset = 0, Limit = 1 };
        var next = Find(query.NewestFirst
            ? "(f.date<$date OR (f.date=$date AND f.fragment_id<$id))"
            : "(f.date>$date OR (f.date=$date AND f.fragment_id>$id))", query.NewestFirst ? "DESC" : "ASC");
        return next ?? Find(query.NewestFirst
            ? "(f.date>$date OR (f.date=$date AND f.fragment_id>$id))"
            : "(f.date<$date OR (f.date=$date AND f.fragment_id<$id))", query.NewestFirst ? "ASC" : "DESC");

        string? Find(string comparison, string order)
        {
            using var command = connection.CreateCommand();
            var where = BuildScope(command, scoped);
            command.CommandText = $"SELECT f.fragment_id FROM fragments f WHERE {where} AND {comparison} ORDER BY f.date {order}, f.fragment_id {order} LIMIT 1";
            command.Parameters.AddWithValue("$date", target.Date.ToString("O"));
            command.Parameters.AddWithValue("$id", target.Id);
            return command.ExecuteScalar() as string;
        }
    }

    public IReadOnlyList<FacetCount> GetFacetCounts(
        FragmentQuery query,
        int eachLimit = 14,
        int minimumTagCount = 1,
        int minimumIndexCount = 1)
    {
        using var connection = Open();
        var result = new List<FacetCount>();
        query = string.IsNullOrWhiteSpace(query.SearchText) && !query.BookmarkedOnly && query.FolderId is null
            ? query with { Month = null, FacetKind = null, FacetValue = null }
            : query with { Month = null };
        ReadFacet("category", "fragment_categories", "categories", "category_id", 1);
        ReadTagFacet();
        ReadFacet("index", "fragment_keywords", "keywords", "keyword_id", minimumIndexCount);
        return result;

        void ReadFacet(string kind, string linkTable, string valueTable, string idColumn, int minimumCount)
        {
            using var command = connection.CreateCommand();
            var scopeQuery = query.FacetKind == kind ? query with { FacetKind = null, FacetValue = null } : query;
            var where = BuildScope(command, scopeQuery);
            command.CommandText = $"""
                WITH scoped AS (SELECT f.fragment_id FROM fragments f WHERE {where})
                SELECT v.display, COUNT(*) AS uses
                FROM scoped s JOIN {linkTable} l ON l.fragment_id=s.fragment_id
                JOIN {valueTable} v ON v.{idColumn}=l.{idColumn}
                GROUP BY v.{idColumn},v.display
                HAVING COUNT(*) >= $minimumCount OR v.display=$selectedValue
                ORDER BY uses DESC,v.display
                {FacetLimitClause()};
                """;
            command.Parameters.AddWithValue("$minimumCount", Math.Max(1, minimumCount));
            command.Parameters.AddWithValue("$selectedValue", query.FacetKind == kind ? query.FacetValue ?? (object)DBNull.Value : DBNull.Value);
            AddFacetLimit(command);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new FacetCount(kind, reader.GetString(0), reader.GetInt32(1)));
        }

        void ReadTagFacet()
        {
            using var command = connection.CreateCommand();
            var scopeQuery = query.FacetKind == "tag" ? query with { FacetKind = null, FacetValue = null } : query;
            var where = BuildScope(command, scopeQuery);
            command.CommandText = $"""
                WITH scoped AS (SELECT f.fragment_id FROM fragments f WHERE {where}),
                active_tags AS (
                    SELECT ft.fragment_id,ft.tag_id FROM fragment_tags ft
                    WHERE NOT EXISTS(SELECT 1 FROM fragment_tag_exclusions x
                        WHERE x.fragment_id=ft.fragment_id AND x.tag_id=ft.tag_id)
                    UNION
                    SELECT ut.fragment_id,ut.tag_id FROM fragment_user_tags ut
                )
                SELECT t.display,COUNT(*) AS uses
                FROM scoped s JOIN active_tags a ON a.fragment_id=s.fragment_id
                JOIN tags t ON t.tag_id=a.tag_id
                GROUP BY t.tag_id,t.display
                HAVING COUNT(*) >= $minimumCount OR t.display=$selectedValue
                ORDER BY uses DESC,t.display
                {FacetLimitClause()};
                """;
            command.Parameters.AddWithValue("$minimumCount", Math.Max(1, minimumTagCount));
            command.Parameters.AddWithValue("$selectedValue", query.FacetKind == "tag" ? query.FacetValue ?? (object)DBNull.Value : DBNull.Value);
            AddFacetLimit(command);
            using var reader = command.ExecuteReader();
            while (reader.Read()) result.Add(new FacetCount("tag", reader.GetString(0), reader.GetInt32(1)));
        }

        string FacetLimitClause() => eachLimit > 0 ? "LIMIT $facetLimit" : string.Empty;
        void AddFacetLimit(SqliteCommand command)
        {
            if (eachLimit > 0) command.Parameters.AddWithValue("$facetLimit", eachLimit);
        }
    }

    private static string BuildScope(SqliteCommand command, FragmentQuery query)
    {
        var terms = new List<string>
        {
            "f.hidden=0",
            "f.section_type=$section",
            "f.classification IN ('PERSONAL','MIXED')"
        };
        command.Parameters.AddWithValue("$section", query.SectionType.ToString().ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(query.Month))
        {
            terms.Add("strftime('%Y-%m', f.date, 'localtime')=$month");
            command.Parameters.AddWithValue("$month", query.Month);
        }
        if (query.BookmarkedOnly) terms.Add("f.bookmarked=1");
        if (query.FolderId is not null)
        {
            var kind = query.BookmarkedOnly ? "BOOKMARK" : "REFERENCE";
            if (query.FolderId == -1)
            {
                terms.Add("NOT EXISTS(SELECT 1 FROM fragment_folder_assignments fa WHERE fa.fragment_id=f.fragment_id AND fa.folder_kind=$folderKind)");
            }
            else
            {
                terms.Add("EXISTS(SELECT 1 FROM fragment_folder_assignments fa WHERE fa.fragment_id=f.fragment_id AND fa.folder_kind=$folderKind AND fa.folder_id=$folder)");
                command.Parameters.AddWithValue("$folder", query.FolderId.Value);
            }
            command.Parameters.AddWithValue("$folderKind", kind);
        }
        var search = query.SearchText?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var words = Regex.Split(search, @"[\s\u3000]+")
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var ftsWords = words.Where(value => value.EnumerateRunes().Count() >= 3).ToList();
            if (ftsWords.Count > 0)
            {
                terms.Add("f.fragment_id IN (SELECT fragment_id FROM fragment_search_fts WHERE fragment_search_fts MATCH $match)");
                command.Parameters.AddWithValue("$match", string.Join(" AND ", ftsWords.Select(value => "\"" + value.Replace("\"", "\"\"") + "\"")));
            }
            var shortIndex = 0;
            foreach (var word in words.Where(value => value.EnumerateRunes().Count() < 3))
            {
                var parameter = "$like" + shortIndex++;
                terms.Add($$"""
                    (f.title LIKE {{parameter}} ESCAPE '\' OR f.summary LIKE {{parameter}} ESCAPE '\' OR f.excerpt LIKE {{parameter}} ESCAPE '\'
                     OR EXISTS(SELECT 1 FROM fragment_messages fm JOIN messages m ON m.conversation_id=fm.conversation_id AND m.message_id=fm.message_id WHERE fm.fragment_id=f.fragment_id AND m.raw_text LIKE {{parameter}} ESCAPE '\')
                     OR EXISTS(SELECT 1 FROM fragment_categories fc JOIN categories c ON c.category_id=fc.category_id WHERE fc.fragment_id=f.fragment_id AND c.display LIKE {{parameter}} ESCAPE '\')
                     OR EXISTS(SELECT 1 FROM fragment_tags ft JOIN tags t ON t.tag_id=ft.tag_id WHERE ft.fragment_id=f.fragment_id AND t.display LIKE {{parameter}} ESCAPE '\'
                        AND NOT EXISTS(SELECT 1 FROM fragment_tag_exclusions x WHERE x.fragment_id=ft.fragment_id AND x.tag_id=ft.tag_id))
                     OR EXISTS(SELECT 1 FROM fragment_user_tags ut JOIN tags t ON t.tag_id=ut.tag_id WHERE ut.fragment_id=f.fragment_id AND t.display LIKE {{parameter}} ESCAPE '\')
                     OR EXISTS(SELECT 1 FROM fragment_keywords fk JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE fk.fragment_id=f.fragment_id AND k.display LIKE {{parameter}} ESCAPE '\'))
                    """);
                command.Parameters.AddWithValue(parameter, "%" + EscapeLike(word) + "%");
            }
        }
        if (!string.IsNullOrWhiteSpace(query.FacetKind) && !string.IsNullOrWhiteSpace(query.FacetValue))
        {
            var normalized = query.FacetValue.Trim().ToLowerInvariant();
            switch (query.FacetKind)
            {
                case "category":
                    terms.Add("EXISTS(SELECT 1 FROM fragment_categories fc JOIN categories c ON c.category_id=fc.category_id WHERE fc.fragment_id=f.fragment_id AND c.normalized=$facet)");
                    break;
                case "tag":
                    terms.Add("""
                        (EXISTS(SELECT 1 FROM fragment_tags ft JOIN tags t ON t.tag_id=ft.tag_id
                            WHERE ft.fragment_id=f.fragment_id AND t.normalized=$facet
                              AND NOT EXISTS(SELECT 1 FROM fragment_tag_exclusions x WHERE x.fragment_id=ft.fragment_id AND x.tag_id=ft.tag_id))
                         OR EXISTS(SELECT 1 FROM fragment_user_tags ut JOIN tags t ON t.tag_id=ut.tag_id
                            WHERE ut.fragment_id=f.fragment_id AND t.normalized=$facet))
                        """);
                    break;
                case "index":
                    terms.Add("EXISTS(SELECT 1 FROM fragment_keywords fk JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE fk.fragment_id=f.fragment_id AND k.normalized=$facet)");
                    break;
            }
            command.Parameters.AddWithValue("$facet", normalized);
        }
        return string.Join(" AND ", terms);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    public void SaveReaderState(string? month, bool newestFirst, string? fragmentId, double offset)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Set("last_month", month ?? "");
        Set("sort_order", newestFirst ? "newest" : "oldest");
        Set("scroll_offset", offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(fragmentId)) Set("last_fragment_id", fragmentId);
        transaction.Commit();

        void Set(string key, string value)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<FragmentRecord> FindByKeyword(string keyword) =>
        QueryFragments(new FragmentQuery(null, null, "index", keyword, true, 0, 60)).Items;

    public IReadOnlyList<FragmentRecord> FindRelated(string fragmentId, int limit = 3)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH candidates AS (
                SELECT f.fragment_id, COUNT(*) AS score
                FROM fragment_keywords source
                JOIN fragment_keywords other ON other.keyword_id=source.keyword_id AND other.fragment_id<>source.fragment_id
                JOIN fragments f ON f.fragment_id=other.fragment_id
                WHERE source.fragment_id=$id AND f.hidden=0 AND f.section_type='MAIN'
                  AND f.classification IN ('PERSONAL','MIXED')
                GROUP BY f.fragment_id
                ORDER BY score DESC, f.date DESC
                LIMIT $limit
            )
            SELECT {FragmentSelectColumns}
            FROM candidates x JOIN fragments f ON f.fragment_id=x.fragment_id
            ORDER BY x.score DESC, f.date DESC;
            """;
        command.Parameters.AddWithValue("$id", fragmentId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var result = new List<FragmentRecord>();
        while (reader.Read()) result.Add(ReadFragment(reader));
        return result;
    }

    public bool NeedsPhase01DerivedMigration() => GetMeta("phase01_analysis_version") != "4";

    public bool NeedsPhase03SummaryMigration() => GetMeta("phase03_summary_version") != "2";

    public void MigratePhase03Summaries(HeuristicAnalysisProvider provider, IProgress<(int Completed, int Total)>? progress = null)
    {
        if (!NeedsPhase03SummaryMigration()) return;
        CreatePhase03SafetyBackup();
        var candidates = new List<(string Id, string Title, string Excerpt)>();
        using (var connection = Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT fragment_id,title,excerpt FROM fragments
                WHERE edited_fields NOT LIKE '%"summary"%'
                ORDER BY fragment_id;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read()) candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        var summaries = new List<(string Id, string Summary)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var messages = GetSourceMessages(candidate.Id);
            if (messages.Count > 0)
            {
                summaries.Add((candidate.Id, provider.SummarizeFragment(messages, candidate.Title, candidate.Excerpt)));
            }
            progress?.Report((i + 1, candidates.Count));
        }
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var item in summaries)
            {
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE fragments SET summary=$summary,updated_at=$now
                    WHERE fragment_id=$id AND edited_fields NOT LIKE '%"summary"%' AND summary<>$summary;
                    """;
                update.Parameters.AddWithValue("$summary", item.Summary);
                update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                update.Parameters.AddWithValue("$id", item.Id);
                update.ExecuteNonQuery();
            }
            RebuildLegacyFts(connection, transaction);
            RebuildSearchFts(connection, transaction);
            transaction.Commit();
        }
        SetMeta("phase03_summary_version", "2");
    }

    public void MigratePhase01DerivedData(IAnalysisProvider provider, IProgress<(int Completed, int Total)>? progress = null)
    {
        if (!NeedsPhase01DerivedMigration()) return;
        CreatePhase01SafetyBackup();
        var ids = GetConversationIds();
        for (var i = 0; i < ids.Count; i++)
        {
            var conversation = GetConversation(ids[i]);
            if (conversation is not null)
                SaveAnalysis(conversation.Id, provider.Analyze(conversation));
            progress?.Report((i + 1, ids.Count));
        }
        SetMeta("phase01_analysis_version", "4");
    }

    private static void RebuildLegacyFts(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var rebuild = connection.CreateCommand();
        rebuild.Transaction = transaction;
        rebuild.CommandText = """
            DELETE FROM fragment_fts;
            INSERT INTO fragment_fts(fragment_id,title,excerpt,summary,keywords)
            SELECT f.fragment_id,f.title,f.excerpt,f.summary,
                   COALESCE((SELECT group_concat(k.display,' ') FROM fragment_keywords fk
                       JOIN keywords k ON k.keyword_id=fk.keyword_id WHERE fk.fragment_id=f.fragment_id),'')
            FROM fragments f;
            """;
        rebuild.ExecuteNonQuery();
    }

    private IReadOnlyList<string> GetConversationIds()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_id FROM conversations ORDER BY conversation_id";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids;
    }

    private NormalizedConversation? GetConversation(string conversationId)
    {
        using var connection = Open();
        string title;
        DateTimeOffset updated;
        bool? sourceIsArchived;
        using (var header = connection.CreateCommand())
        {
            header.CommandText = "SELECT title,updated_at,source_is_archived FROM conversations WHERE conversation_id=$id";
            header.Parameters.AddWithValue("$id", conversationId);
            using var reader = header.ExecuteReader();
            if (!reader.Read()) return null;
            title = reader.GetString(0);
            updated = DateTimeOffset.Parse(reader.GetString(1));
            sourceIsArchived = reader.IsDBNull(2) ? null : reader.GetInt64(2) != 0;
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT message_id,parent_message_id,timestamp,role,raw_text,source_hash
            FROM messages WHERE conversation_id=$id
            ORDER BY timestamp,message_id;
            """;
        command.Parameters.AddWithValue("$id", conversationId);
        using var messagesReader = command.ExecuteReader();
        var messages = new List<SourceMessage>();
        while (messagesReader.Read())
            messages.Add(new SourceMessage(conversationId, title, messagesReader.GetString(0),
                messagesReader.IsDBNull(1) ? null : messagesReader.GetString(1),
                DateTimeOffset.Parse(messagesReader.GetString(2)), messagesReader.GetString(3),
                messagesReader.GetString(4), messagesReader.GetString(5)));
        return new NormalizedConversation(conversationId, title, updated, messages)
        {
            SourceIsArchived = sourceIsArchived
        };
    }

    public string CreatePhase01SafetyBackup()
    {
        EnsureWritable();
        var existing = GetMeta("phase01_backup_path");
        if (!string.IsNullOrWhiteSpace(existing) && IsVerifiedDatabase(existing)) return existing;
        var directory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var destination = AllocateBackupPath(directory, "magic-logger-pre-phase01");
        CreateVerifiedSnapshot(destination);
        SetMeta("phase01_backup_path", destination);
        return destination;
    }

    public string? CreatePhase02SafetyBackup()
    {
        EnsureWritable();
        if (!File.Exists(DatabasePath) || Count("messages") == 0) return null;
        var existing = GetMeta("phase02_backup_path");
        if (!string.IsNullOrWhiteSpace(existing) && IsVerifiedDatabase(existing)) return existing;
        var directory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var discovered = Directory.GetFiles(directory, "magic-logger-pre-phase02-*.db")
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault(IsVerifiedDatabase);
        if (discovered is not null)
        {
            SetMeta("phase02_backup_path", discovered);
            return discovered;
        }
        var destination = AllocateBackupPath(directory, "magic-logger-pre-phase02");
        CreateVerifiedSnapshot(destination);
        SetMeta("phase02_backup_path", destination);
        return destination;
    }

    public string? CreatePhase03SafetyBackup()
    {
        EnsureWritable();
        if (!File.Exists(DatabasePath) || Count("messages") == 0) return null;
        var existing = GetMeta("phase03_backup_path");
        if (!string.IsNullOrWhiteSpace(existing) && IsVerifiedDatabase(existing)) return existing;
        var directory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var discovered = Directory.GetFiles(directory, "magic-logger-pre-phase03-*.db")
            .OrderByDescending(File.GetCreationTimeUtc)
            .FirstOrDefault(IsVerifiedDatabase);
        if (discovered is not null)
        {
            SetMeta("phase03_backup_path", discovered);
            return discovered;
        }
        var destination = AllocateBackupPath(directory, "magic-logger-pre-phase03");
        CreateVerifiedSnapshot(destination);
        SetMeta("phase03_backup_path", destination);
        return destination;
    }

    public string? GetMeta(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO meta(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    public string CreateBackup()
    {
        EnsureWritable();
        return ExecuteWithBusyRetry(CreateBackupOnce);
    }

    private string CreateBackupOnce()
    {
        var directory = Path.Combine(DataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var destination = AllocateBackupPath(directory, "magic-logger");
        CreateVerifiedSnapshot(destination);

        // The snapshot is already safely on disk before this small metadata
        // write.  A later metadata failure can at most cause another backup;
        // it cannot invalidate or discard the completed snapshot.
        SetMeta("last_backup_utc", DateTimeOffset.UtcNow.ToString("O"));
        foreach (var old in Directory.GetFiles(directory, "magic-logger-*.db")
                     .Where(path => !Path.GetFileName(path).StartsWith("magic-logger-pre-", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(File.GetCreationTimeUtc).Skip(5))
            File.Delete(old);
        return destination;
    }

    public void AutoBackupIfDue()
    {
        EnsureWritable();
        if (!File.Exists(DatabasePath)) return;
        var value = ExecuteWithBusyRetry(() => GetMeta("last_backup_utc"));
        if (value is null || !DateTimeOffset.TryParse(value, out var last) || DateTimeOffset.UtcNow - last > TimeSpan.FromHours(24))
            CreateBackup();
    }

    private string AllocateBackupPath(string directory, string prefix)
    {
        var stem = Path.Combine(directory, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
        var destination = stem + ".db";
        var suffix = 1;
        while (File.Exists(destination)) destination = $"{stem}-{suffix++:00}.db";
        return destination;
    }

    private void EnsureWritable()
    {
        if (IsReadOnly) throw new InvalidOperationException("実魔道書DBは読取専用です。");
    }

    private void CreateVerifiedSnapshot(string destination)
    {
        var temporary = destination + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var source = Open())
            using (var target = new SqliteConnection($"Data Source={temporary};Mode=ReadWriteCreate;Cache=Private;Pooling=False"))
            {
                target.Open();
                source.BackupDatabase(target);
                using var stabilize = target.CreateCommand();
                stabilize.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                stabilize.ExecuteNonQuery();
                stabilize.CommandText = "PRAGMA journal_mode=DELETE;";
                if (!string.Equals(stabilize.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("バックアップを単独で復元可能な形式に確定できませんでした。");
            }
            VerifyDatabaseIntegrity(temporary);
            DeleteSnapshotSidecars(temporary);
            File.Move(temporary, destination);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            DeleteSnapshotSidecars(temporary);
        }
    }

    private static void DeleteSnapshotSidecars(string path)
    {
        foreach (var sidecar in new[] { path + "-wal", path + "-shm", path + "-journal" })
            if (File.Exists(sidecar)) File.Delete(sidecar);
    }

    private static void VerifyDatabaseIntegrity(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Cache=Private;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.OrdinalIgnoreCase))
            throw new IOException("バックアップの整合性を確認できませんでした。");
    }

    public int Count(string table)
    {
        if (table is not ("conversations" or "messages" or "fragments" or "import_runs" or "import_run_files" or "fragment_revisions" or "categories" or "tags" or "fragment_search_fts"
            or "fragment_tags" or "fragment_user_tags" or "fragment_tag_exclusions" or "collection_folders" or "fragment_folder_assignments" or "promoted_index_tags"))
            throw new ArgumentOutOfRangeException(nameof(table));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public string? GetLatestImportStatus()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM import_runs ORDER BY import_id DESC LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    public void AttemptRawTextUpdateForTest(string conversationId, string messageId, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET raw_text=$raw WHERE conversation_id=$cid AND message_id=$mid";
        command.Parameters.AddWithValue("$raw", value);
        command.Parameters.AddWithValue("$cid", conversationId);
        command.Parameters.AddWithValue("$mid", messageId);
        command.ExecuteNonQuery();
    }
}
