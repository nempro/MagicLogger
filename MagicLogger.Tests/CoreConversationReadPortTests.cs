using System.IO;
using System.Security.Cryptography;
using MagicLogger;

internal static class CoreConversationReadPortTests
{
    private static byte[] Hash(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(file);
    }

    internal static void Run(string root, Action<bool, string> assert)
    {
        var database = new LocalDatabase(Path.Combine(root, "core-read-port"));
        database.Initialize();
        var time = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var original = new NormalizedConversation("core-port-fixture", "合成会話", time.AddMinutes(2),
        [
            new SourceMessage("core-port-fixture", "合成会話", "user-1", null, time,
                "user", "会話を保存してください。", "synthetic-user-hash"),
            new SourceMessage("core-port-fixture", "合成会話", "assistant-1", "user-1", time.AddMinutes(2),
                "assistant", "保存しました。", "synthetic-assistant-hash")
        ]) { SourceIsArchived = true };
        database.SaveRawConversation(original);
        assert(database.Count("fragments") == 0, "Core Read Port用会話には断章がない");

        var before = Hash(database.DatabasePath);
        IConversationReadPort port = new SqliteConversationReadPort(database.DatabasePath);
        var loaded = port.Read(original.Id);
        assert(loaded is not null && loaded.Id == original.Id && loaded.Title == original.Title &&
            loaded.UpdatedAt == original.UpdatedAt && loaded.SourceIsArchived == true &&
            loaded.Messages.SequenceEqual(original.Messages),
            "断章なしでも会話・全発言・role・時刻・原文・出典hashをCore経由で取得する");
        assert(port.Read("missing") is null, "存在しない会話は取得不可として返す");

        assert(port.ListConversations().Any(item => item.Id == original.Id && item.Title == original.Title),
            "Core Read Portで会話の一覧を取得する");
        assert(port.ListConversations("合成会話").Single().Id == original.Id &&
            port.ListConversations("missing").Count == 0,
            "Core Read Portで会話を題から探す");

        assert(before.SequenceEqual(Hash(database.DatabasePath)),
            "Core Read Portは元会話DBのバイト列を書き換えない");
    }
}
