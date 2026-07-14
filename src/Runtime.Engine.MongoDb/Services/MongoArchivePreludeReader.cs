using System.Buffers.Binary;
using System.IO.Compression;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services;

/// <summary>
/// Reads the prelude of a mongodump <c>--archive</c> file (magic number, header document, one
/// CollectionMetadata BSON document per collection) to determine the database name(s) the archive
/// was dumped from. The prelude sits at the start of the (optionally gzipped) stream, so only the
/// first few kilobytes are read regardless of archive size. Detection is best-effort: any
/// unexpected shape yields the databases collected so far (or an empty list), never an exception.
/// </summary>
internal static class MongoArchivePreludeReader
{
    private const uint ArchiveMagic = 0x8199e26d;
    private const int MaxDocumentSize = 16 * 1024 * 1024;
    private const int MaxPreludeDocuments = 100_000;

    public static IReadOnlyList<string> TryReadSourceDatabases(string archiveFilePath)
    {
        try
        {
            using var fileStream = File.OpenRead(archiveFilePath);
            return TryReadSourceDatabases(fileStream);
        }
        catch
        {
            return [];
        }
    }

    public static IReadOnlyList<string> TryReadSourceDatabases(Stream stream)
    {
        var databases = new List<string>();
        Stream? payload = null;
        try
        {
            payload = WrapIfGzip(stream);

            var lengthBuffer = new byte[4];
            if (!TryReadExactly(payload, lengthBuffer))
            {
                return databases;
            }

            if (BinaryPrimitives.ReadUInt32LittleEndian(lengthBuffer) != ArchiveMagic)
            {
                return databases;
            }

            for (var i = 0; i < MaxPreludeDocuments; i++)
            {
                if (!TryReadExactly(payload, lengthBuffer))
                {
                    break;
                }

                var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
                if (length == -1)
                {
                    // 0xFFFFFFFF separator — end of the prelude, data blocks follow
                    break;
                }

                if (length < 5 || length > MaxDocumentSize)
                {
                    break;
                }

                // Re-assemble the full BSON document (its length prefix included)
                var documentBytes = new byte[length];
                lengthBuffer.CopyTo(documentBytes, 0);
                if (!TryReadExactly(payload, documentBytes.AsSpan(4)))
                {
                    break;
                }

                var document = BsonSerializer.Deserialize<BsonDocument>(documentBytes);
                if (document.TryGetValue("db", out var db) && db.IsString && !databases.Contains(db.AsString))
                {
                    databases.Add(db.AsString);
                }
            }
        }
        catch
        {
            // Best-effort detection — unknown or corrupt archives yield what was read so far.
        }
        finally
        {
            // Dispose only the gzip wrapper (created with leaveOpen) — never the caller's stream.
            if (payload != null && !ReferenceEquals(payload, stream))
            {
                payload.Dispose();
            }
        }

        return databases;
    }

    private static Stream WrapIfGzip(Stream stream)
    {
        if (!stream.CanSeek)
        {
            return stream;
        }

        var position = stream.Position;
        var b0 = stream.ReadByte();
        var b1 = stream.ReadByte();
        stream.Position = position;

        if (b0 == 0x1f && b1 == 0x8b)
        {
            return new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        }

        return stream;
    }

    private static bool TryReadExactly(Stream stream, Span<byte> buffer)
    {
        return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length;
    }
}
