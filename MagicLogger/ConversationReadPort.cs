namespace MagicLogger;

public sealed record ConversationOverview(string Id, string Title, DateTimeOffset UpdatedAt);

// A read-only Core boundary for complete source conversations, including those
// that have no fragment. Consumers do not need to know the conversation DB schema.
public interface IConversationReadPort
{
    NormalizedConversation? Read(string conversationId);
    IReadOnlyList<ConversationOverview> ListConversations(string? query = null, int limit = 100, int offset = 0)
        => throw new NotSupportedException("This conversation source does not support listing.");
    IReadOnlyList<string> ReadExportPaths() => [];
}

public sealed class SqliteConversationReadPort(string databasePath, bool immutable = false) : IConversationReadPort
{
    public IReadOnlyList<ConversationOverview> ListConversations(string? query = null, int limit = 100, int offset = 0)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        using var connection = ReadOnlySourceConnection.Open(databasePath, immutable);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conversation_id,title,updated_at FROM conversations
            WHERE $query='' OR conversation_id LIKE $pattern ESCAPE '\' OR title LIKE $pattern ESCAPE '\'
            ORDER BY updated_at DESC,conversation_id LIMIT $limit OFFSET $offset;
            """;
        var search = query?.Trim() ?? "";
        var escaped = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        command.Parameters.AddWithValue("$query", search);
        command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        var conversations = new List<ConversationOverview>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) conversations.Add(new(reader.GetString(0), reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2))));
        return conversations;
    }

    public IReadOnlyList<string> ReadExportPaths()
    {
        using var connection = ReadOnlySourceConnection.Open(databasePath, immutable);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_path FROM import_run_files ORDER BY import_id DESC,source_path;";
        List<string> paths=[];
        using(var reader=command.ExecuteReader()) while(reader.Read()) paths.Add(reader.GetString(0));
        command.CommandText="SELECT source_path FROM import_runs ORDER BY import_id DESC;";
        using(var reader=command.ExecuteReader()) while(reader.Read())
        {
            var value=reader.GetString(0);
            if(value.StartsWith('['))
            {
                try { paths.AddRange(System.Text.Json.JsonSerializer.Deserialize<string[]>(value) ?? []); }
                catch(System.Text.Json.JsonException) { }
            }
            else paths.Add(value);
        }
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public NormalizedConversation? Read(string conversationId)
    {
        using var connection = ReadOnlySourceConnection.Open(databasePath, immutable);
        using var snapshot = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = snapshot;
        command.CommandText = "SELECT title,updated_at,source_is_archived FROM conversations WHERE conversation_id=$id;";
        command.Parameters.AddWithValue("$id", conversationId);
        string title;
        DateTimeOffset updated;
        bool? sourceIsArchived;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return null;
            title = reader.GetString(0);
            updated = DateTimeOffset.Parse(reader.GetString(1));
            sourceIsArchived = reader.IsDBNull(2) ? null : reader.GetInt64(2) != 0;
        }
        command.CommandText = """
            SELECT message_id,parent_message_id,timestamp,role,raw_text,source_hash FROM messages
            WHERE conversation_id=$id ORDER BY timestamp,message_id;
            """;
        var messages = new List<SourceMessage>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                messages.Add(new SourceMessage(conversationId, title, reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1), DateTimeOffset.Parse(reader.GetString(2)),
                    reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        snapshot.Commit();
        return new NormalizedConversation(conversationId, title, updated, messages)
        {
            SourceIsArchived = sourceIsArchived
        };
    }
}
