namespace RockBot.Subagent.Tests;

[TestClass]
public class InMemoryWhiteboardMemoryTests
{
    [TestMethod]
    public async Task WriteAsync_CanBeReadBack()
    {
        var wb = new InMemoryWhiteboardMemory();
        await wb.WriteAsync("board1", "key1", "value1");
        var result = await wb.ReadAsync("board1", "key1");
        Assert.AreEqual("value1", result);
    }

    [TestMethod]
    public async Task WriteAsync_OverwritesExisting()
    {
        var wb = new InMemoryWhiteboardMemory();
        await wb.WriteAsync("board1", "key1", "first");
        await wb.WriteAsync("board1", "key1", "second");
        var result = await wb.ReadAsync("board1", "key1");
        Assert.AreEqual("second", result);
    }

    [TestMethod]
    public async Task ReadAsync_NonexistentKey_ReturnsNull()
    {
        var wb = new InMemoryWhiteboardMemory();
        var result = await wb.ReadAsync("board1", "missing");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteAsync_RemovesKey()
    {
        var wb = new InMemoryWhiteboardMemory();
        await wb.WriteAsync("board1", "key1", "value");
        await wb.DeleteAsync("board1", "key1");
        var result = await wb.ReadAsync("board1", "key1");
        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task DeleteAsync_NonexistentKey_DoesNotThrow()
    {
        var wb = new InMemoryWhiteboardMemory();
        // Should not throw
        await wb.DeleteAsync("board1", "nonexistent");
    }

    [TestMethod]
    public async Task ListAsync_ReturnsAllKeysForBoard()
    {
        var wb = new InMemoryWhiteboardMemory();
        await wb.WriteAsync("board1", "a", "1");
        await wb.WriteAsync("board1", "b", "2");
        await wb.WriteAsync("board1", "c", "3");
        // Different board — should not appear
        await wb.WriteAsync("board2", "x", "99");

        var entries = await wb.ListAsync("board1");

        Assert.AreEqual(3, entries.Count);
        Assert.AreEqual("1", entries["a"]);
        Assert.AreEqual("2", entries["b"]);
        Assert.AreEqual("3", entries["c"]);
    }

    [TestMethod]
    public async Task ListAsync_EmptyBoard_ReturnsEmptyDictionary()
    {
        var wb = new InMemoryWhiteboardMemory();
        var entries = await wb.ListAsync("empty-board");
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task ClearBoardAsync_RemovesAllEntries()
    {
        var wb = new InMemoryWhiteboardMemory();
        await wb.WriteAsync("board1", "a", "1");
        await wb.WriteAsync("board1", "b", "2");
        await wb.ClearBoardAsync("board1");
        var entries = await wb.ListAsync("board1");
        Assert.AreEqual(0, entries.Count);
    }

    [TestMethod]
    public async Task ClearBoardAsync_NonexistentBoard_DoesNotThrow()
    {
        var wb = new InMemoryWhiteboardMemory();
        // Should not throw
        await wb.ClearBoardAsync("nonexistent");
    }

    [TestMethod]
    public async Task ClearBoardAsync_OnlyAffectsTargetBoard()
    {
        var wb = new InMemoryWhiteboardMemory();
        await wb.WriteAsync("board1", "key", "value1");
        await wb.WriteAsync("board2", "key", "value2");

        await wb.ClearBoardAsync("board1");

        var board1 = await wb.ListAsync("board1");
        var board2 = await wb.ListAsync("board2");

        Assert.AreEqual(0, board1.Count);
        Assert.AreEqual(1, board2.Count);
        Assert.AreEqual("value2", board2["key"]);
    }

    [TestMethod]
    public async Task ConcurrentWrites_DoNotCorruptState()
    {
        var wb = new InMemoryWhiteboardMemory();
        const string boardId = "concurrent-board";

        // Launch 100 concurrent writes to the same board
        var tasks = Enumerable.Range(0, 100)
            .Select(i => wb.WriteAsync(boardId, $"key{i}", $"value{i}"))
            .ToArray();

        await Task.WhenAll(tasks);

        var entries = await wb.ListAsync(boardId);
        Assert.AreEqual(100, entries.Count);

        // Verify all keys are present with correct values
        for (var i = 0; i < 100; i++)
            Assert.AreEqual($"value{i}", entries[$"key{i}"]);
    }
}
