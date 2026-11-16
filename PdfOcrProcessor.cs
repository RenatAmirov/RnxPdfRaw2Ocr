using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using TesseractOCR;
using TesseractOCR.Enums;
using TesseractOCR.Renderers;

namespace RnxPdfRaw2Ocr;

internal readonly record struct OcrProgress(int CompletedPages, int TotalPages);

internal static class PdfOcrProcessor
{
    public static int Convert(string sourcePath, string outputPath, string language,
        IProgress<OcrProgress> progress, CancellationToken cancellationToken)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        outputPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Путь для сохранения совпадает с исходным PDF.");

        cancellationToken.ThrowIfCancellationRequested();
        using var input = File.OpenRead(sourcePath);
        using var scan = new PdfScan(sourcePath);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        if (scan.PageCount == 0 || scan.PageCount != document.PageCount)
            throw new InvalidDataException("Не удалось прочитать страницы PDF.");

        string tessdata = Path.Combine(AppContext.BaseDirectory, "tessdata");
        string workDir = Directory.CreateTempSubdirectory("rnx-pdf-ocr-").FullName;
        string overlayBase = Path.Combine(workDir, "layer");
        string overlayPath = overlayBase + ".pdf";
        try
        {
            var placements = new PagePlacement?[scan.PageCount];
            int recognized = 0;
            using (var engine = new Engine(tessdata, language, EngineMode.LstmOnly))
            using (var renderer = new PdfResult(overlayBase, tessdata, textOnly: true))
            using (renderer.BeginDocument("OCR"))
            {
                for (int index = 0; index < scan.PageCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress.Report(new OcrProgress(index, scan.PageCount));

                    using var image = scan.Render(index, out var placement);
                    using var ocrPage = engine.Process(image, PageSegMode.Auto);
                    if (!string.IsNullOrWhiteSpace(ocrPage.Text))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!renderer.AddPage(ocrPage))
                            throw new IOException($"Не удалось создать текстовый слой страницы {index + 1}.");

                        placements[index] = placement;
                        recognized++;
                    }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (recognized == 0)
                return 0;

            progress.Report(new OcrProgress(scan.PageCount, scan.PageCount));
            using var overlayStream = File.OpenRead(overlayPath);
            using var overlay = XPdfForm.FromStream(overlayStream);
            if (overlay.PageCount != recognized)
                throw new InvalidDataException("Созданный текстовый слой повреждён.");

            int overlayPage = 0;
            for (int index = 0; index < placements.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (placements[index] is not { } placement)
                    continue;

                overlay.PageNumber = ++overlayPage;
                AddOverlay(document.Pages[index], overlay, placement);
            }

            SaveOutput(document, outputPath, cancellationToken);
            return recognized;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void AddOverlay(PdfPage target, XPdfForm overlay, PagePlacement placement)
    {
        NormalizePageContentDecodeParameters(target);
        using var graphics = XGraphics.FromPdfPage(target, XGraphicsPdfPageOptions.Append);
        XPoint topLeft = placement.TopLeft;
        XVector across = placement.TopRight - topLeft;
        XVector down = placement.BottomLeft - topLeft;

        // PDFium reports PDF user coordinates. XGraphics draws downwards from the page top.
        var matrix = new XMatrix(
            across.X / overlay.PointWidth, -across.Y / overlay.PointWidth,
            down.X / overlay.PointHeight, -down.Y / overlay.PointHeight,
            topLeft.X, graphics.PageSize.Height - topLeft.Y);
        graphics.MultiplyTransform(matrix);
        graphics.DrawImage(overlay, 0, 0, overlay.PointWidth, overlay.PointHeight);
    }

    private static void NormalizePageContentDecodeParameters(PdfPage page)
    {
        var contents = Dereference(page.Elements["/Contents"]);
        if (contents is PdfArray streams)
        {
            foreach (var stream in streams.Elements)
                NormalizeStreamDecodeParameters(Dereference(stream));
        }
        else
        {
            NormalizeStreamDecodeParameters(contents);
        }
    }

    private static void NormalizeStreamDecodeParameters(PdfItem? item)
    {
        if (item is not PdfDictionary { Stream: not null } stream)
            return;

        // PDFsharp 6.2.4 rejects PDF nulls in /DecodeParms (empira/PDFsharp#399).
        // An empty parameter dictionary has the same default values as a null array entry.
        var decodeParameters = Dereference(stream.Elements["/DecodeParms"]);
        if (decodeParameters is PdfNull or PdfNullObject)
        {
            stream.Elements.Remove("/DecodeParms");
            return;
        }

        if (decodeParameters is not PdfArray parameters)
            return;

        PdfArray? normalized = null;
        for (int index = 0; index < parameters.Elements.Count; index++)
        {
            if (Dereference(parameters.Elements[index]) is PdfNull or PdfNullObject)
            {
                normalized ??= parameters.Clone();
                normalized.Elements[index] = new PdfDictionary();
            }
        }

        if (normalized is not null)
            stream.Elements["/DecodeParms"] = normalized;
    }

    private static PdfItem? Dereference(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

    private static void SaveOutput(PdfDocument document, string outputPath, CancellationToken cancellationToken)
    {
        // Write beside the destination so a failed or cancelled conversion cannot truncate it.
        string tempPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $".ocr-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                document.Save(stream, closeStream: false);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, outputPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
