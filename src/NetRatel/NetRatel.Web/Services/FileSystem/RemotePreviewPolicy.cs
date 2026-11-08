using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using ExcelDataReader;
using Microsoft.VisualBasic.FileIO;

namespace NetRatel.Web.Services.FileSystem;

public sealed record RemotePreviewDocument(string Kind, string Title, string? Text = null,
    IReadOnlyList<string[]>? Rows = null);

/// <summary>Bounded, inert local inspection. No external document service or content execution.</summary>
public static class RemotePreviewPolicy
{
    public const int MaximumTextCharacters = 512 * 1024;
    public const int MaximumArchiveEntries = 2048;
    public const long MaximumExpandedBytes = 32 * 1024 * 1024;
    public const long MaximumImagePixels = 16_000_000;

    public static RemotePreviewDocument Inspect(string fileName, Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length == 0) return new("text", "Empty file", "This file is empty (0 bytes).");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (extension == ".pdf")
        {
            stream.Position = 0;
            Span<byte> signature = stackalloc byte[5];
            stream.ReadExactly(signature);
            stream.Position = 0;
            if (!signature.SequenceEqual("%PDF-"u8)) throw new InvalidDataException("This file does not have a PDF signature.");
            return new("pdf", "Local PDF · one bounded page at a time; scripts, links and forms disabled");
        }
        if (extension is ".cer" or ".crt" or ".der" or ".pem") return InspectCertificate(stream);
        if (extension is ".sqlite" or ".sqlite3" or ".db") return InspectDatabase(stream, cancellationToken);
        if (extension == ".xls") return InspectSpreadsheet(stream, cancellationToken);
        if (FileBrowserContentClassifier.TryGetPreviewImageContentType(fileName, out _))
        {
            ValidateImage(stream, extension);
            return new("image", "Image");
        }
        if (extension is ".zip" or ".docx" or ".xlsx" or ".pptx" or ".epub" or ".odt" or ".ods" or ".odp")
            return InspectZip(extension, stream, cancellationToken);
        stream.Position = 0;
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: true);
        var characters = new char[MaximumTextCharacters + 1];
        var length = reader.ReadBlock(characters, 0, characters.Length);
        var excerpt = length > MaximumTextCharacters;
        var text = new string(characters, 0, Math.Min(length, MaximumTextCharacters));
        if (text.Contains('\0')) return new("unsupported", "Binary file", "No local renderer is enabled for this binary format. Use Download.");
        if (extension == ".csv")
        {
            using var parser = new TextFieldParser(new StringReader(text)) { HasFieldsEnclosedInQuotes = true };
            parser.SetDelimiters(",");
            var rows = new List<string[]>();
            while (!parser.EndOfData && rows.Count < 1000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = parser.ReadFields() ?? [];
                if (row.Length > 100) throw new InvalidDataException("CSV preview supports at most 100 columns.");
                rows.Add(row);
            }
            return new("table", "CSV · first 1,000 rows / 100 columns", Rows: rows);
        }
        if (extension == ".gpx") return InspectGpx(text, cancellationToken);
        if (extension == ".obj") return InspectObj(text, cancellationToken);
        if (extension is ".ics" or ".vcf")
            return new("table", "Local calendar/contact fields · no URI resolution", Rows: text.Replace("\r\n ", "", StringComparison.Ordinal)
                .Split('\n').Take(2000).Select(line => line.TrimEnd('\r').Split(':', 2)).ToArray());
        if (extension == ".har")
        {
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("log", out var log) ||
                log.ValueKind != JsonValueKind.Object || !log.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("HAR requires a log.entries array.");
            var rows = entries.EnumerateArray().Take(1000).Select(entry => new[]
                { HarField(entry, "request", "method"), HarField(entry, "request", "url"), HarField(entry, "response", "status") }).ToArray();
            return new("table", "HAR · first 1,000 requests (no replay)", Rows: rows);
        }
        // Active markup is inspected as inert source. No renderer-selected HTML,
        // email images, Markdown includes, map tiles, SQL or patch execution.
        return new("text", excerpt ? "Text excerpt · first 512 Ki characters" : "Read-only text", text);
    }

    private static string HarField(JsonElement entry, string section, string field) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(section, out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty(field, out var value)
            ? value.ToString() : throw new InvalidDataException($"HAR entry is missing {section}.{field}.");

    private static RemotePreviewDocument InspectZip(string extension, Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count > MaximumArchiveEntries) throw new InvalidDataException("Archive preview entry limit exceeded (2,048).");
        long expanded = 0;
        long observedExpanded = 0;
        var expansionBuffer = new byte[16 * 1024];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Length > MaximumExpandedBytes - expanded) throw new InvalidDataException("Archive expanded-size limit exceeded (32 MiB).");
            expanded += entry.Length;
            // ZIP headers can lie about expanded length. Validate observed
            // decompression before handing bounded bytes to a browser/parser.
            using var content = entry.Open();
            long observedEntry = 0;
            int read;
            while ((read = content.Read(expansionBuffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedEntry += read;
                observedExpanded += read;
                if (observedExpanded > MaximumExpandedBytes) throw new InvalidDataException("Archive observed expanded-size limit exceeded (32 MiB).");
            }
            if (observedEntry != entry.Length) throw new InvalidDataException("Archive expanded length does not match its metadata.");
        }
        if (extension == ".zip") return new("table", "ZIP directory · no extraction / nested expansion", Rows:
            archive.Entries.Select(entry => new[] { entry.FullName, $"{entry.Length:N0} bytes" }).ToArray());
        if (extension == ".xlsx") return InspectSpreadsheet(stream, cancellationToken);
        if (extension is ".docx" or ".pptx") ValidateDocumentWork(archive, cancellationToken);
        var output = new StringBuilder();
        foreach (var entry in archive.Entries.Where(entry => IsDocumentPart(extension, entry.FullName)).Take(256))
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.AppendLine(entry.FullName);
            using var content = entry.Open();
            using var xml = XmlReader.Create(content, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumTextCharacters, IgnoreWhitespace = true
            });
            while (xml.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (xml.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                {
                    if (output.Length + xml.Value.Length > MaximumTextCharacters)
                        throw new InvalidDataException("Decoded document text limit exceeded (512 Ki characters).");
                    output.Append(xml.Value).Append(' ');
                }
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName is "p" or "row" or "tr") output.AppendLine();
            }
            output.AppendLine();
        }
        if (extension == ".docx" && output.Length > 0)
            return new("docx", "Local DOCX · isolated renderer, no external resources or active content", output.ToString());
        return output.Length == 0
            ? new("unsupported", "Document", "No supported text parts were found. Use Download for a full-fidelity view.")
            : new("text", "Local document text · layout, formulas, macros and external references are not executed", output.ToString());
    }

    private static bool IsDocumentPart(string extension, string name) => extension switch
    {
        ".docx" => name == "word/document.xml",
        ".xlsx" => name == "xl/sharedStrings.xml" || name.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal),
        ".pptx" => name.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && name.EndsWith(".xml", StringComparison.Ordinal),
        ".epub" => name.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase),
        ".odt" or ".ods" or ".odp" => name == "content.xml", _ => false
    };

    private static void ValidateDocumentWork(ZipArchive archive, CancellationToken cancellationToken)
    {
        var nodes = 0;
        long decodedPixels = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Embedded SVG document graphics require Download; they are not enabled in this renderer.");
            if (FileBrowserContentClassifier.TryGetPreviewImageContentType(entry.FullName, out _))
            {
                if (entry.Length > 8 * 1024 * 1024) throw new InvalidDataException("Embedded image exceeds the 8 MiB limit.");
                using var input = entry.Open();
                using var image = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    if (image.Length + read > 8 * 1024 * 1024) throw new InvalidDataException("Embedded image expanded beyond its limit.");
                    image.Write(buffer, 0, read);
                }
                decodedPixels += ValidateImage(image, Path.GetExtension(entry.Name).ToLowerInvariant());
                if (decodedPixels > MaximumImagePixels) throw new InvalidDataException("Document images exceed the combined decoded-pixel budget.");
            }
            if (!entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            using var content = entry.Open();
            using var xml = XmlReader.Create(content, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumTextCharacters });
            while (xml.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++nodes > 30_000 || xml.Depth > 64) throw new InvalidDataException("Document exceeds the XML work/depth budget.");
            }
        }
    }

    private static RemotePreviewDocument InspectSpreadsheet(Stream stream, CancellationToken cancellationToken)
    {
        stream.Position = 0;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using var reader = ExcelReaderFactory.CreateReader(stream, new ExcelReaderConfiguration { LeaveOpen = true });
        var rows = new List<string[]>();
        while (rows.Count < 1000 && reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.FieldCount > 100) throw new InvalidDataException("Spreadsheet preview supports at most 100 columns.");
            var row = new string[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                var value = Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture) ?? "";
                row[index] = value.Length <= 4096 ? value : value[..4096] + "…";
            }
            rows.Add(row);
        }
        return new("table", $"{reader.Name} · first sheet, first 1,000 rows / 100 columns · stored values, no formula execution", Rows: rows);
    }

    private static RemotePreviewDocument InspectCertificate(Stream stream)
    {
        if (stream.Length > 1024 * 1024) throw new InvalidDataException("Certificate preview is limited to 1 MiB.");
        stream.Position = 0;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        try
        {
            using var certificate = bytes.AsSpan().StartsWith("-----BEGIN "u8)
                ? X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(bytes)) : X509CertificateLoader.LoadCertificate(bytes);
            return new("table", "Certificate metadata · no chain/network trust check", Rows: new string[][]
            {
                ["Subject", certificate.Subject], ["Issuer", certificate.Issuer], ["Serial", certificate.SerialNumber],
                ["Valid from (UTC)", certificate.NotBefore.ToUniversalTime().ToString("O")],
                ["Valid until (UTC)", certificate.NotAfter.ToUniversalTime().ToString("O")],
                ["Signature algorithm", certificate.SignatureAlgorithm.FriendlyName ?? certificate.SignatureAlgorithm.Value ?? "unknown"],
                ["SHA-256", certificate.GetCertHashString(HashAlgorithmName.SHA256)]
            });
        }
        catch (CryptographicException) { return new("unsupported", "Certificate", "This is not a supported DER or PEM certificate. Private-key containers are not opened."); }
    }

    private static RemotePreviewDocument InspectDatabase(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.Length > 4 * 1024 * 1024) throw new InvalidDataException("SQLite preview is limited to 4 MiB.");
        // SQLite needs random access to a private bounded parser copy. This is
        // never a remote provider, public path, pooled connection or writeback.
        var temporary = Path.Combine(Path.GetTempPath(), $"netratel-preview-{Guid.NewGuid():N}.sqlite");
        try
        {
            var creation = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) creation.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var output = new FileStream(temporary, creation)) { stream.Position = 0; stream.CopyTo(output); }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = temporary, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false, DefaultTimeout = 2 }.ToString());
            connection.Open();
            using var schema = connection.CreateCommand();
            schema.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type='table' AND name NOT LIKE 'sqlite_%' AND length(name)<=256 LIMIT 100";
            var names = new List<string>();
            using (var reader = schema.ExecuteReader()) while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); names.Add(reader.GetString(0)); }
            var rows = names.Select(name => new[] { "Table", name }).ToList();
            if (names.Count > 0)
            {
                using var columns = connection.CreateCommand();
                columns.CommandText = "SELECT name FROM pragma_table_info($table) WHERE length(name)<=256 LIMIT 20";
                columns.Parameters.AddWithValue("$table", names[0]);
                var fields = new List<string>();
                using (var reader = columns.ExecuteReader()) while (reader.Read()) fields.Add(reader.GetString(0));
                if (fields.Count > 0)
                {
                    static string Quote(string name) => '"' + name.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
                    using var sample = connection.CreateCommand();
                    sample.CommandText = "SELECT " + string.Join(',', fields.Select(field => $"substr(CAST({Quote(field)} AS TEXT),1,1024)")) + " FROM " + Quote(names[0]) + " LIMIT 20";
                    rows.Add(fields.ToArray());
                    using var reader = sample.ExecuteReader();
                    while (reader.Read())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        rows.Add(Enumerable.Range(0, fields.Count).Select(index => reader.IsDBNull(index) ? "NULL" : reader.GetString(index)).ToArray());
                    }
                }
            }
            return new("table", "SQLite · up to 100 ordinary tables, first table 20 rows / 20 columns · read only, no extensions or user SQL", Rows: rows);
        }
        catch (SqliteException) { return new("unsupported", "SQLite", "The database is malformed, encrypted, uses an unavailable extension, or is not SQLite. Use Download."); }
        finally { File.Delete(temporary); }
    }

    private static RemotePreviewDocument InspectGpx(string text, CancellationToken cancellationToken)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumTextCharacters });
        var points = new List<string>();
        while (points.Count < 5000 && reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName is "trkpt" or "rtept" or "wpt" &&
                double.TryParse(reader.GetAttribute("lat"), CultureInfo.InvariantCulture, out var latitude) &&
                double.TryParse(reader.GetAttribute("lon"), CultureInfo.InvariantCulture, out var longitude) &&
                latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180)
                points.Add(FormattableString.Invariant($"{longitude + 180:F5},{90 - latitude:F5}"));
        }
        return new("map", "GPX 1.0/1.1 · first 5,000 points · local equirectangular trace, no map tiles", string.Join(' ', points));
    }

    private static RemotePreviewDocument InspectObj(string text, CancellationToken cancellationToken)
    {
        var vertices = new List<(double X, double Y)>();
        var edges = new List<(int A, int B)>();
        foreach (var line in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts is ["v", var x, var y, var z, ..] && vertices.Count < 2000 &&
                double.TryParse(x, CultureInfo.InvariantCulture, out var px) && double.TryParse(y, CultureInfo.InvariantCulture, out var py) &&
                double.TryParse(z, CultureInfo.InvariantCulture, out var pz) && double.IsFinite(px) && double.IsFinite(py) && double.IsFinite(pz) &&
                Math.Max(Math.Abs(px), Math.Max(Math.Abs(py), Math.Abs(pz))) <= 1e9)
                vertices.Add((px + pz * 0.5, py + pz * 0.3));
            if (parts.Length >= 4 && parts[0] == "f" && edges.Count < 4000)
            {
                var face = parts.Skip(1).Take(16).Select(part => int.TryParse(part.Split('/')[0], out var index) ? index - 1 : -1).ToArray();
                for (var index = 0; index < face.Length && edges.Count < 4000; index++) edges.Add((face[index], face[(index + 1) % face.Length]));
            }
        }
        if (vertices.Count == 0) return new("unsupported", "OBJ", "No supported vertices were found. Use Download.");
        var minX = vertices.Min(point => point.X); var minY = vertices.Min(point => point.Y);
        var scale = 340 / Math.Max(1e-9, Math.Max(vertices.Max(point => point.X) - minX, vertices.Max(point => point.Y) - minY));
        string Point(int index) => FormattableString.Invariant($"{10 + (vertices[index].X - minX) * scale:F3},{170 - (vertices[index].Y - minY) * scale / 2:F3}");
        return new("mesh", "OBJ wireframe · positive indices, first 2,000 vertices / 4,000 edges · no materials or external references", Rows:
            edges.Where(edge => edge.A >= 0 && edge.B >= 0 && edge.A < vertices.Count && edge.B < vertices.Count)
                .Select(edge => new[] { Point(edge.A) + " " + Point(edge.B) }).ToArray());
    }

    private static long ValidateImage(Stream stream, string extension)
    {
        stream.Position = 0;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray().AsSpan();
        long width = 0, height = 0, frames = 1;
        if (extension == ".png" && data.Length >= 24 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            width = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
            height = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
            for (var offset = 8; offset <= data.Length - 12;)
            {
                var size = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
                if (size > data.Length - offset - 12) throw new InvalidDataException("Malformed PNG chunk.");
                if (data.Slice(offset + 4, 4).SequenceEqual("acTL"u8) && size >= 8)
                    frames = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8, 4));
                offset += checked((int)size + 12);
            }
        }
        else if (extension == ".gif" && data.Length >= 10 && data[..3].SequenceEqual("GIF"u8))
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
            frames = Math.Max(1, data.Count((byte)0x2c)); // Conservative bound includes embedded marker bytes.
        }
        else if (extension is ".jpg" or ".jpeg" && data.Length > 4 && data[0] == 0xff && data[1] == 0xd8)
        {
            for (var offset = 2; offset < data.Length - 9;)
            {
                if (data[offset++] != 0xff) continue;
                var marker = data[offset++];
                if (marker is 0xff or 0x00 or 0xd8 or 0xd9 or >= 0xd0 and <= 0xd7) continue;
                var size = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
                if (size < 2 || offset + size > data.Length) break;
                if (marker is 0xc0 or 0xc1 or 0xc2)
                { height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2)); width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2)); break; }
                offset += size;
            }
        }
        else if (extension == ".webp" && data.Length >= 30 && data.Slice(12, 4).SequenceEqual("VP8X"u8))
        {
            width = 1 + data[24] + (data[25] << 8) + (data[26] << 16);
            height = 1 + data[27] + (data[28] << 8) + (data[29] << 16);
            if ((data[20] & 2) != 0) throw new InvalidDataException("Animated WebP is not enabled for preview.");
        }
        if (width <= 0 || height <= 0 || frames > 256 || width > MaximumImagePixels / height / frames)
            throw new InvalidDataException("Image dimensions/animation exceed the 16-million-pixel decoded budget or use an unsupported header.");
        stream.Position = 0;
        return width * height * frames;
    }
}
