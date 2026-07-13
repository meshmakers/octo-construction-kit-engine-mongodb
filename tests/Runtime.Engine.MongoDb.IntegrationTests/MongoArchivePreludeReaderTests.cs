using System.IO.Compression;

using FluentAssertions;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

using MongoDB.Bson;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

// AB#4367 — the mongodump archive prelude carries one CollectionMetadata BSON document per
// collection (with the source "db" name) before any data blocks. The reader must extract the
// distinct source database names and NEVER throw on unknown or corrupt input.
public class MongoArchivePreludeReaderTests
{
    private const uint ArchiveMagic = 0x8199e26d;

    [Fact]
    public void TryReadSourceDatabases_GzippedSingleDatabaseArchive_ReturnsThatDatabase()
    {
        using var stream = BuildArchive(gzip: true, ("wwc26", "RtEntity_SystemTenant"), ("wwc26", "CkModel"));

        var result = MongoArchivePreludeReader.TryReadSourceDatabases(stream);

        result.Should().Equal("wwc26");
    }

    [Fact]
    public void TryReadSourceDatabases_UncompressedArchive_ReturnsDatabase()
    {
        using var stream = BuildArchive(gzip: false, ("plaindb", "SomeCollection"));

        var result = MongoArchivePreludeReader.TryReadSourceDatabases(stream);

        result.Should().Equal("plaindb");
    }

    [Fact]
    public void TryReadSourceDatabases_MultiDatabaseArchive_ReturnsAllDatabases()
    {
        using var stream = BuildArchive(gzip: true, ("dba", "C1"), ("dbb", "C2"), ("dba", "C3"));

        var result = MongoArchivePreludeReader.TryReadSourceDatabases(stream);

        result.Should().BeEquivalentTo("dba", "dbb");
    }

    [Fact]
    public void TryReadSourceDatabases_NonArchiveGarbage_ReturnsEmpty()
    {
        using var stream = new MemoryStream("this is not a mongodump archive at all"u8.ToArray());

        var result = MongoArchivePreludeReader.TryReadSourceDatabases(stream);

        result.Should().BeEmpty();
    }

    [Fact]
    public void TryReadSourceDatabases_GzippedGarbage_ReturnsEmpty()
    {
        using var inner = new MemoryStream();
        using (var gz = new GZipStream(inner, CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write("gzipped but wrong magic"u8);
        }

        inner.Position = 0;
        var result = MongoArchivePreludeReader.TryReadSourceDatabases(inner);

        result.Should().BeEmpty();
    }

    [Fact]
    public void TryReadSourceDatabases_TruncatedPrelude_DoesNotThrow()
    {
        using var full = BuildArchive(gzip: false, ("wwc26", "C1"));
        var bytes = ((MemoryStream)full).ToArray();
        using var truncated = new MemoryStream(bytes, 0, bytes.Length / 2);

        var act = () => MongoArchivePreludeReader.TryReadSourceDatabases(truncated);

        act.Should().NotThrow();
    }

    [Fact]
    public void TryReadSourceDatabases_FromFile_ReadsArchive()
    {
        var path = Path.Combine(Path.GetTempPath(), $"prelude_{Guid.NewGuid():N}.tar.gz");
        try
        {
            using (var stream = BuildArchive(gzip: true, ("filedb", "C1")))
            using (var file = File.Create(path))
            {
                stream.CopyTo(file);
            }

            var result = MongoArchivePreludeReader.TryReadSourceDatabases(path);

            result.Should().Equal("filedb");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void TryReadSourceDatabases_MissingFile_ReturnsEmpty()
    {
        var result = MongoArchivePreludeReader.TryReadSourceDatabases(
            Path.Combine(Path.GetTempPath(), $"missing_{Guid.NewGuid():N}.tar.gz"));

        result.Should().BeEmpty();
    }

    private static Stream BuildArchive(bool gzip, params (string Db, string Collection)[] namespaces)
    {
        var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ArchiveMagic);

            WriteBsonDocument(writer, new BsonDocument
            {
                { "concurrent_collections", 4 },
                { "version", "0.1" },
                { "server_version", "8.0.13" },
                { "tool_version", "100.14.0" }
            });

            foreach (var (db, collection) in namespaces)
            {
                WriteBsonDocument(writer, new BsonDocument
                {
                    { "db", db },
                    { "collection", collection },
                    { "metadata", "{}" },
                    { "size", 0 },
                    { "type", "collection" }
                });
            }

            writer.Write(0xFFFFFFFF); // prelude terminator
        }

        payload.Position = 0;
        if (!gzip)
        {
            return payload;
        }

        var compressed = new MemoryStream();
        using (var gz = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            payload.CopyTo(gz);
        }

        compressed.Position = 0;
        return compressed;
    }

    private static void WriteBsonDocument(BinaryWriter writer, BsonDocument document)
    {
        // ToBson() emits the full BSON document including its own int32 length prefix —
        // exactly the on-disk shape mongodump uses inside the archive.
        writer.Write(document.ToBson());
    }
}
