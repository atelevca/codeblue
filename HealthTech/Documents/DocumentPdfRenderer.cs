using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Fonts;

namespace HealthTech.Documents;

public interface IDocumentPdfRenderer
{
    byte[] Render(SavedDocument document);
}

/// <summary>Offline PDF renderer for the saved Quill Delta, including inline and paragraph formatting.</summary>
public sealed class DocumentPdfRenderer(IConfiguration configuration) : IDocumentPdfRenderer
{
    private static readonly object RenderLock = new();

    public byte[] Render(SavedDocument saved)
    {
        // PDFsharp has process-wide font state; serialize initialization and rendering.
        lock (RenderLock)
        {
            GlobalFontSettings.FontResolver ??= new DocumentFontResolver(configuration["Documents:FontDirectory"]);
            var document = new Document();
            document.Info.Title = "Proces-verbal";
            document.Info.Author = "HealthTech";
            var normal = document.Styles[StyleNames.Normal]!;
            normal.Font.Name = "DocumentSans";
            normal.Font.Size = 10;
            normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
            normal.ParagraphFormat.WidowControl = true;
            for (var level = 1; level <= 6; level++)
            {
                var style = document.Styles["Heading" + level]!;
                style.Font.Name = "DocumentSans";
                style.Font.Size = level == 1 ? 20 : 13;
                style.Font.Bold = true;
                style.ParagraphFormat.SpaceBefore = Unit.FromPoint(12);
                style.ParagraphFormat.SpaceAfter = Unit.FromPoint(6);
                style.ParagraphFormat.KeepWithNext = true;
            }

            var section = document.AddSection();
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = Unit.FromCentimeter(1.8);
            section.PageSetup.BottomMargin = Unit.FromCentimeter(1.8);
            section.PageSetup.LeftMargin = Unit.FromCentimeter(1.8);
            section.PageSetup.RightMargin = Unit.FromCentimeter(1.8);
            var footer = section.Footers.Primary.AddParagraph();
            footer.Format.Font.Size = 8;
            footer.Format.Alignment = ParagraphAlignment.Right;
            footer.AddText("Pagina ");
            footer.AddPageField();
            footer.AddText(" / ");
            footer.AddNumPagesField();

            var status = section.AddParagraph(saved.Verification.IsConsistent
                ? "Verificare automată: fără discrepanțe raportate."
                : $"Verificare automată: {saved.Verification.Findings.Count} discrepanțe - necesită revizuire.");
            status.Format.Font.Size = 8;
            status.Format.Font.Color = Colors.Gray;
            AddDelta(section, saved.Delta);

            var renderer = new PdfDocumentRenderer { Document = document };
            renderer.RenderDocument();
            using var output = new MemoryStream();
            renderer.PdfDocument.Save(output, closeStream: false);
            return output.ToArray();
        }
    }

    private static void AddDelta(Section section, QuillDelta delta)
    {
        var numbering = new int[9];
        foreach (var line in QuillDocument.Read(delta))
        {
            var paragraph = section.AddParagraph();
            if (line.Header > 0)
                paragraph.Style = "Heading" + line.Header;
            paragraph.Format.LeftIndent = Unit.FromCentimeter(line.Indent * 0.6);
            paragraph.Format.Alignment = line.Align switch
            {
                "center" => ParagraphAlignment.Center,
                "right" => ParagraphAlignment.Right,
                "justify" => ParagraphAlignment.Justify,
                _ => ParagraphAlignment.Left
            };
            paragraph.AddText(QuillDocument.ListPrefix(line, numbering));
            foreach (var run in line.Runs)
            {
                var text = run.Link == null
                    ? paragraph.AddFormattedText(run.Text)
                    : paragraph.AddHyperlink(run.Link, HyperlinkType.Web).AddFormattedText(run.Text);
                // Preserve heading style when a run does not explicitly apply inline bold.
                if (run.Bold) text.Font.Bold = true;
                if (run.Italic) text.Font.Italic = true;
                if (run.Underline) text.Font.Underline = Underline.Single;
            }
        }
    }
}

internal sealed class DocumentFontResolver(string? configuredDirectory) : IFontResolver
{
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) =>
        new(bold ? "document-bold" : "document-regular", false, italic);

    public byte[] GetFont(string faceName)
    {
        var windows = OperatingSystem.IsWindows();
        var directory = configuredDirectory ?? (windows
            ? Environment.GetFolderPath(Environment.SpecialFolder.Fonts)
            : "/usr/share/fonts/truetype/dejavu");
        var name = windows
            ? (faceName == "document-bold" ? "arialbd.ttf" : "arial.ttf")
            : (faceName == "document-bold" ? "DejaVuSans-Bold.ttf" : "DejaVuSans.ttf");
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            throw new DocumentException(503, "Fonturile PDF lipsesc. Configurați Documents:FontDirectory (Arial pe Windows, DejaVu Sans pe Linux).");
        return File.ReadAllBytes(path);
    }
}
