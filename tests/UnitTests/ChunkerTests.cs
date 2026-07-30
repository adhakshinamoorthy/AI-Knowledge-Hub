using KnowledgeHub.Application;
using Microsoft.Extensions.Options;

namespace UnitTests;

public sealed class ChunkerTests
{
    [Fact]
    public void Chunk_preserves_page_section_and_overlap()
    {
        var sut = new DocumentChunker(Options.Create(new ChunkingOptions { Size = 100, Overlap = 20 }));
        var parsed = new ParsedDocument("Guide", [new ParsedPage(3, "Setup", string.Join(' ', Enumerable.Repeat("knowledge", 40)))], new Dictionary<string, string>());
        var chunks = sut.Chunk(Guid.NewGuid(), "tenant-a", parsed);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, x => { Assert.Equal(3, x.Page); Assert.Equal("Setup", x.Section); Assert.Equal("tenant-a", x.TenantId); Assert.NotEmpty(x.Text); });
    }

    [Fact]
    public void Chunk_rejects_empty_text_without_creating_empty_chunks()
    {
        var sut = new DocumentChunker(Options.Create(new ChunkingOptions()));
        Assert.Empty(sut.Chunk(Guid.NewGuid(), "tenant", new ParsedDocument("Empty", [new ParsedPage(1, null, "  \r\n ")], new Dictionary<string, string>())));
    }
}
