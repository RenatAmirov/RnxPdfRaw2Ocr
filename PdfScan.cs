using System.Runtime.InteropServices;
using PdfSharp.Drawing;
using TesseractOCR.Pix;

namespace RnxPdfRaw2Ocr;

internal readonly record struct PagePlacement(XPoint TopLeft, XPoint TopRight, XPoint BottomLeft);

internal sealed partial class PdfScan : IDisposable
{
    private const int TargetDpi = 300;
    private const int MaxSidePixels = 8000;
    private const int MaxPagePixels = 32_000_000;
    private const int BitmapGray = 1;
    private const int RenderAnnotations = 0x01;
    private const int RenderGrayscale = 0x08;
    private const int PasswordError = 4;
    private nint _document;

    // PDFium is initialized once for the lifetime of this process.
    static PdfScan() => FPDF_InitLibrary();

    public PdfScan(string path)
    {
        _document = FPDF_LoadDocument(path, 0);
        if (_document == 0)
        {
            throw new InvalidDataException(FPDF_GetLastError() == PasswordError
                ? "PDF защищён паролем. Для распознавания нужна открытая копия файла."
                : "Не удалось прочитать исходный PDF. Проверьте, открывается ли он в программе просмотра.");
        }

        PageCount = FPDF_GetPageCount(_document);
    }

    public int PageCount { get; }

    public Image Render(int index, out PagePlacement placement)
    {
        nint page = FPDF_LoadPage(_document, index);
        if (page == 0)
            throw new InvalidDataException($"Не удалось открыть страницу {index + 1}.");

        try
        {
            double pageWidth = FPDF_GetPageWidth(page);
            double pageHeight = FPDF_GetPageHeight(page);
            if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight) || pageWidth <= 0 || pageHeight <= 0)
                throw new InvalidDataException($"У страницы {index + 1} недопустимый размер.");

            double scale = Math.Min(TargetDpi / 72.0, MaxSidePixels / Math.Max(pageWidth, pageHeight));
            scale = Math.Min(scale, Math.Sqrt(MaxPagePixels / (pageWidth * pageHeight)));
            int width = Math.Max(1, (int)Math.Round(pageWidth * scale));
            int height = Math.Max(1, (int)Math.Round(pageHeight * scale));
            int dpi = Math.Max(70, (int)Math.Round(72 * scale));
            placement = new PagePlacement(
                ToPagePoint(page, width, height, 0, 0),
                ToPagePoint(page, width, height, width, 0),
                ToPagePoint(page, width, height, 0, height));

            var image = Image.Create(width, height, depth: 8);
            try
            {
                var pixels = image.GetData();
                nint bitmap = FPDFBitmap_CreateEx(width, height, BitmapGray, pixels.PixData, pixels.WordsPerLine * 4);
                if (bitmap == 0)
                    throw new IOException($"Не удалось подготовить изображение страницы {index + 1}.");

                try
                {
                    FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                    FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, RenderAnnotations | RenderGrayscale);
                }
                finally
                {
                    FPDFBitmap_Destroy(bitmap);
                }

                // Leptonica stores 8-bit pixels in big-endian order within each 32-bit word.
                pixels.EndianByteSwap();
                image.XRes = dpi;
                image.YRes = dpi;
                return image;
            }
            catch
            {
                image.Dispose();
                throw;
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    private static XPoint ToPagePoint(nint page, int width, int height, int x, int y)
    {
        if (FPDF_DeviceToPage(page, 0, 0, width, height, 0, x, y, out double pageX, out double pageY) == 0)
            throw new InvalidDataException("Не удалось определить координаты текстового слоя.");

        return new XPoint(pageX, pageY);
    }

    public void Dispose()
    {
        if (_document == 0)
            return;

        FPDF_CloseDocument(_document);
        _document = 0;
    }

    [LibraryImport("pdfium")]
    private static partial void FPDF_InitLibrary();

    [LibraryImport("pdfium", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint FPDF_LoadDocument(string path, nint password);

    [LibraryImport("pdfium")]
    private static partial uint FPDF_GetLastError();

    [LibraryImport("pdfium")]
    private static partial int FPDF_GetPageCount(nint document);

    [LibraryImport("pdfium")]
    private static partial nint FPDF_LoadPage(nint document, int index);

    [LibraryImport("pdfium")]
    private static partial double FPDF_GetPageWidth(nint page);

    [LibraryImport("pdfium")]
    private static partial double FPDF_GetPageHeight(nint page);

    [LibraryImport("pdfium")]
    private static partial int FPDF_DeviceToPage(nint page, int startX, int startY, int width, int height,
        int rotation, int deviceX, int deviceY, out double pageX, out double pageY);

    [LibraryImport("pdfium")]
    private static partial nint FPDFBitmap_CreateEx(int width, int height, int format, nint pixels, int stride);

    [LibraryImport("pdfium")]
    private static partial int FPDFBitmap_FillRect(nint bitmap, int left, int top, int width, int height, uint color);

    [LibraryImport("pdfium")]
    private static partial void FPDF_RenderPageBitmap(nint bitmap, nint page, int startX, int startY,
        int width, int height, int rotation, int flags);

    [LibraryImport("pdfium")]
    private static partial void FPDFBitmap_Destroy(nint bitmap);

    [LibraryImport("pdfium")]
    private static partial void FPDF_ClosePage(nint page);

    [LibraryImport("pdfium")]
    private static partial void FPDF_CloseDocument(nint document);
}
