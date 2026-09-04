using System.IO.Compression;
using System.Text;
using AccountantApp.Api.Shared.Data;
using AccountantApp.Api.Slices.Documents.Core;
using AccountantApp.Api.Slices.Documents.ExternalInterfaces;
using AccountantApp.Api.Slices.Documents.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AccountantApp.Tests.Documents;

/// <summary>
/// Sample files, one context factory, and one store helper.
///
/// The sample files are built rather than checked in, because a fixture file in the repository is a
/// binary nobody reviews: a "real .docx" that is actually a renamed .zip would make the OOXML tests
/// pass by accident, and no reader could tell from the diff. Everything here is constructed from its
/// leading bytes and its entry names, which are exactly what the validator inspects.
/// </summary>
internal static class DocumentsTestHarness
{
    public static DocumentsDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DocumentsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // NewApi rather than Api: inside AccountantApp.Tests.Documents a static method called Api is
    // shadowed by the AccountantApp.Api NAMESPACE, and every call site fails to compile.
    public static DocumentApi NewApi(DocumentsDbContext db, IRequestTransaction? transaction = null) =>
        new(db, transaction ?? new TestDoubles.NoOpRequestTransaction());

    public static StoreDocumentRequest Upload(
        byte[] content,
        string fileName,
        Guid? ticketId = null,
        Guid? customerId = null,
        string origin = DocumentOrigin.CustomerUpload,
        string declaredContentType = "application/octet-stream",
        Guid? uploadedBy = null) =>
        new(
            ticketId ?? Guid.NewGuid(),
            customerId ?? Guid.NewGuid(),
            origin,
            fileName,
            declaredContentType,
            new MemoryStream(content),
            uploadedBy ?? Guid.NewGuid());

    // --- Sample files ---

    /// <summary>%PDF, then enough of a body to be a plausible file.</summary>
    public static byte[] Pdf(int padding = 64) =>
        Concat("%PDF-1.7\n1 0 obj\n"u8.ToArray(), Filler(padding));

    public static byte[] Png() => Concat(
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], Filler(32));

    public static byte[] Jpeg() => Concat([0xFF, 0xD8, 0xFF, 0xE0], Filler(32));

    public static byte[] TiffLittleEndian() => Concat([0x49, 0x49, 0x2A, 0x00], Filler(32));

    public static byte[] TiffBigEndian() => Concat([0x4D, 0x4D, 0x00, 0x2A], Filler(32));

    /// <summary>The OLE2 compound-file header, shared by .doc, .xls, .ppt and others.</summary>
    public static byte[] Ole2() => Concat(
        [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1], Filler(64));

    public static byte[] Html() =>
        Encoding.UTF8.GetBytes("<!DOCTYPE html>\n<html><body><script>alert(1)</script></body></html>");

    public static byte[] Svg() => Encoding.UTF8.GetBytes(
        "<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

    /// <summary>A plain archive. Same leading bytes as every OOXML file, and rejected.</summary>
    public static byte[] PlainZip() => BuildZip(("readme.txt", "hello"u8.ToArray()));

    /// <summary>
    /// A real OOXML word package: [Content_Types].xml at the root and word/document.xml. Entry
    /// CONTENTS are irrelevant because the validator never inflates one -- which is the point.
    /// </summary>
    public static byte[] Docx() => BuildZip(
        ("[Content_Types].xml", "<Types/>"u8.ToArray()),
        ("_rels/.rels", "<Relationships/>"u8.ToArray()),
        ("word/document.xml", "<document/>"u8.ToArray()));

    public static byte[] Xlsx() => BuildZip(
        ("[Content_Types].xml", "<Types/>"u8.ToArray()),
        ("_rels/.rels", "<Relationships/>"u8.ToArray()),
        ("xl/workbook.xml", "<workbook/>"u8.ToArray()));

    /// <summary>An archive with the Office markers but no [Content_Types].xml -- not a package.</summary>
    public static byte[] ZipWithoutContentTypes() => BuildZip(
        ("word/document.xml", "<document/>"u8.ToArray()));

    /// <summary>The ZIP signature followed by rubbish. Client input, so a 422 and never a 500.</summary>
    public static byte[] MalformedZip() =>
        Concat([0x50, 0x4B, 0x03, 0x04], Filler(200));

    /// <summary>
    /// A small archive whose DECLARED uncompressed size is far past the ceiling. Zeros compress to
    /// almost nothing, so this is the real shape of a bomb: a few hundred kilobytes on the wire.
    ///
    /// It is written in chunks so building it does not allocate the expanded size either -- a test that
    /// runs out of memory constructing the payload proves nothing about the validator.
    /// </summary>
    public static byte[] ZipBomb(long uncompressedBytes = 150L * 1024 * 1024)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("[Content_Types].xml").Open();
            var chunk = new byte[1024 * 1024];
            for (long written = 0; written < uncompressedBytes; written += chunk.Length)
                entry.Write(chunk, 0, (int)Math.Min(chunk.Length, uncompressedBytes - written));
        }

        return output.ToArray();
    }

    public static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content, 0, content.Length);
            }

        return output.ToArray();
    }

    public static byte[] Concat(byte[] first, byte[] second) => [.. first, .. second];

    private static byte[] Filler(int length)
    {
        var filler = new byte[length];
        for (var index = 0; index < length; index++)
            filler[index] = (byte)('a' + index % 26);
        return filler;
    }
}

/// <summary>
/// Records what DocumentApi did with the request's transaction. The two things worth asserting are that
/// it ENLISTED -- otherwise the bytes commit independently of the ticket change they were supposed to be
/// atomic with -- and that it NEVER COMMITTED, because the caller owns the commit and a write method
/// that commits on its own has silently ended the caller's transaction early.
/// </summary>
internal sealed class RecordingRequestTransaction : IRequestTransaction
{
    public int BeginCount { get; private set; }
    public int EnlistCount { get; private set; }
    public int CommitCount { get; private set; }

    public Task<IAsyncDisposable> BeginAsync(DbContext context, CancellationToken ct)
    {
        BeginCount++;
        return Task.FromResult<IAsyncDisposable>(NoopScope.Instance);
    }

    public Task EnlistAsync(DbContext context, CancellationToken ct)
    {
        EnlistCount++;
        return Task.CompletedTask;
    }

    public Task CommitAsync(CancellationToken ct)
    {
        CommitCount++;
        return Task.CompletedTask;
    }

    private sealed class NoopScope : IAsyncDisposable
    {
        public static readonly NoopScope Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// A stream of a given length that produces its bytes on demand and counts how many were actually read.
///
/// It exists for one assertion: a 26 MB upload must be rejected WITHOUT the body being buffered first.
/// A MemoryStream cannot show that, because constructing it has already allocated everything -- the
/// test would pass against an implementation that read the whole thing and then measured it.
/// </summary>
internal sealed class CountingStream : Stream
{
    private readonly long _length;

    public CountingStream(long length) => _length = length;

    public long BytesRead { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _length - BytesRead;
        if (remaining <= 0)
            return 0;

        var read = (int)Math.Min(count, remaining);
        Array.Clear(buffer, offset, read);
        BytesRead += read;
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
