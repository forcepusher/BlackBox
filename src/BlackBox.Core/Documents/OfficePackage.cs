using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace BlackBox.Core.Documents;

/// <summary>The parts of an Office file (.docx, .xlsx, .pptx) being made, zipped up with their content types.</summary>
internal sealed class OfficePackage
{
    public static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string OfficeDocument = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
    public const string CoreProperties = "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties";
    public const string ImageRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
    public const string HyperlinkRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";

    private readonly List<(string Name, byte[] Data, string? ContentType)> _parts = [];

    public void Add(string name, XDocument xml, string contentType) => _parts.Add((name, Bytes(xml), contentType));

    /// <summary>A picture or other part whose content type follows from its extension.</summary>
    public void Add(string name, byte[] data) => _parts.Add((name, data, null));

    /// <summary>A part's relationships ("word/_rels/document.xml.rels" for "word/document.xml").</summary>
    public void AddRelationships(string forPart, IEnumerable<(string Id, string Type, string Target, bool External)> relations)
    {
        var folder = Path.GetDirectoryName(forPart)?.Replace('\\', '/') ?? "";
        var name = (folder.Length > 0 ? folder + "/" : "") + "_rels/" + Path.GetFileName(forPart) + ".rels";
        Add(name, new XDocument(new XElement(Rel + "Relationships",
            relations.Select(r => new XElement(Rel + "Relationship",
                new XAttribute("Id", r.Id), new XAttribute("Type", r.Type), new XAttribute("Target", r.Target),
                r.External ? new XAttribute("TargetMode", "External") : null)))),
            "application/vnd.openxmlformats-package.relationships+xml");
    }

    /// <summary>docProps/core.xml with the document's title.</summary>
    public void AddCoreProperties(string? title)
    {
        XNamespace cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace dcterms = "http://purl.org/dc/terms/";
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        Add("docProps/core.xml", new XDocument(new XElement(cp + "coreProperties",
            new XAttribute(XNamespace.Xmlns + "cp", cp), new XAttribute(XNamespace.Xmlns + "dc", dc),
            new XAttribute(XNamespace.Xmlns + "dcterms", dcterms), new XAttribute(XNamespace.Xmlns + "xsi", xsi),
            string.IsNullOrWhiteSpace(title) ? null : new XElement(dc + "title", title.Trim()),
            new XElement(dcterms + "created", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now),
            new XElement(dcterms + "modified", new XAttribute(xsi + "type", "dcterms:W3CDTF"), now))),
            "application/vnd.openxmlformats-package.core-properties+xml");
    }

    public byte[] Build()
    {
        XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
        var types = new XElement(ct + "Types",
            new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
        foreach (var ext in _parts.Where(p => p.ContentType is null).Select(p => Path.GetExtension(p.Name).TrimStart('.').ToLowerInvariant()).Distinct())
            types.Add(new XElement(ct + "Default", new XAttribute("Extension", ext), new XAttribute("ContentType", ext switch
            {
                "png" => "image/png",
                "jpg" or "jpeg" => "image/jpeg",
                "gif" => "image/gif",
                _ => "application/octet-stream",
            })));
        foreach (var part in _parts.Where(p => p.ContentType is not null && !p.Name.EndsWith(".rels", StringComparison.Ordinal)))
            types.Add(new XElement(ct + "Override", new XAttribute("PartName", "/" + part.Name), new XAttribute("ContentType", part.ContentType!)));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", Bytes(new XDocument(types)));
            foreach (var part in _parts)
                Write(zip, part.Name, part.Data);
        }
        return buffer.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(data);
    }

    public static byte[] Bytes(XDocument xml)
    {
        using var buffer = new MemoryStream();
        using (var writer = XmlWriter.Create(buffer, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
        {
            new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), xml.Root).Save(writer);
        }
        return buffer.ToArray();
    }
}

/// <summary>A picture to put in a document: PNG or JPEG as it was, anything else turned into PNG.</summary>
internal sealed record DocPicture(byte[] Data, string Extension, int Width, int Height)
{
    /// <summary>The picture at <paramref name="path"/>; throws <see cref="InvalidDataException"/> when it isn't one.</summary>
    public static DocPicture Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidDataException($"no picture at {path}");
        var data = File.ReadAllBytes(path);
        using var codec = SKCodec.Create(new MemoryStream(data)) ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a picture");
        var (width, height) = (codec.Info.Width, codec.Info.Height);
        switch (codec.EncodedFormat)
        {
            case SKEncodedImageFormat.Png:
                return new DocPicture(data, "png", width, height);
            case SKEncodedImageFormat.Jpeg:
                return new DocPicture(data, "jpeg", width, height);
            default:
                using (var bitmap = SKBitmap.Decode(data) ?? throw new InvalidDataException($"{Path.GetFileName(path)} could not be decoded"))
                using (var png = bitmap.Encode(SKEncodedImageFormat.Png, 100))
                    return new DocPicture(png.ToArray(), "png", width, height);
        }
    }

    /// <summary>Its size in points (at 96 pixels per inch), made smaller to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/>.</summary>
    public (double Width, double Height) Fit(double maxWidth, double maxHeight)
    {
        double w = Width * 0.75, h = Height * 0.75;
        var scale = Math.Min(1, Math.Min(maxWidth / w, maxHeight / h));
        return (w * scale, h * scale);
    }
}
