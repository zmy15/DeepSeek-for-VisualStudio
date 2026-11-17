using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 本地 PowerPoint 解析服务：负责导出幻灯片图片与无 OCR 时的文本提取。
    /// </summary>
    public static class PresentationRenderService
    {
        public const int MaxSlides = 20;
        private const int ExportWidth = 1600;
        private const int ExportHeight = 900;
        private const int LibreOfficeTimeoutMs = 120_000;

        private sealed class PresentationPageRenderBatch
        {
            public int TotalPages { get; init; }
            public List<string> Files { get; init; } = new();
        }

        public static readonly string[] SupportedExtensions = { ".ppt", ".pptx" };

        public static bool IsPresentationFile(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            return SupportedExtensions.Contains(
                Path.GetExtension(filePath),
                StringComparer.OrdinalIgnoreCase);
        }

        public static async Task<List<ChatContentPart>?> BuildPresentationVisionPartsAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            List<byte[]> pages = await RenderPresentationToPngAsync(filePath, cancellationToken);
            if (pages.Count == 0) return null;

            return pages.Select(png => new ChatContentPart
            {
                Type = "image_url",
                ImageUrl = new ChatImageUrl
                {
                    Url = "data:image/png;base64," + Convert.ToBase64String(png),
                },
            }).ToList();
        }

        public static async Task<List<byte[]>> RenderPresentationToPngAsync(
            string filePath,
            CancellationToken cancellationToken = default,
            int maxPages = MaxSlides)
        {
            RenderedDocumentPageBatch batch = await RenderPresentationPageBatchToPngAsync(
                filePath,
                cancellationToken,
                startPage: 1,
                maxPages);
            return batch.Pages.ToList();
        }

        public static async Task<RenderedDocumentPageBatch> RenderPresentationPageBatchToPngAsync(
            string filePath,
            CancellationToken cancellationToken = default,
            int startPage = 1,
            int maxPages = MaxSlides)
        {
            if (!File.Exists(filePath) || !IsPresentationFile(filePath))
                return new RenderedDocumentPageBatch();

            int normalizedStartPage = Math.Max(1, startPage);
            int pageLimit = Math.Max(1, maxPages);
            string workDir = Path.Combine(
                Path.GetTempPath(),
                "DeepSeekVS_Presentation",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                PresentationPageRenderBatch rendered = await TryRenderWithPowerPointAsync(
                    filePath,
                    workDir,
                    normalizedStartPage,
                    pageLimit,
                    cancellationToken);
                if (rendered.Files.Count == 0 && rendered.TotalPages <= 0)
                {
                    rendered = await TryRenderWithLibreOfficeAsync(
                        filePath,
                        workDir,
                        normalizedStartPage,
                        pageLimit,
                        cancellationToken);
                }

                var pages = new List<byte[]>();
                foreach (string pngFile in rendered.Files.Take(pageLimit))
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    try
                    {
                        pages.Add(File.ReadAllBytes(pngFile));
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"[PPT] failed to read rendered slide image: {ex.Message}");
                    }
                }
                return new RenderedDocumentPageBatch
                {
                    TotalPages = rendered.TotalPages,
                    StartPage = normalizedStartPage,
                    Pages = pages,
                };
            }
            finally
            {
                TryDeleteDirectory(workDir);
            }
        }

        public static async Task<string?> ExtractPresentationTextAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            if (!File.Exists(filePath) || !IsPresentationFile(filePath))
                return null;

            if (string.Equals(Path.GetExtension(filePath), ".pptx", StringComparison.OrdinalIgnoreCase))
            {
                string? text = TryExtractPptxText(filePath);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await TryExtractTextWithPowerPointAsync(filePath, cancellationToken);
        }

        internal static string? TryExtractPptxText(string filePath)
        {
            try
            {
                using var archive = new ZipArchive(File.OpenRead(filePath), ZipArchiveMode.Read);
                var slideEntries = archive.Entries
                    .Select(entry => new
                    {
                        Entry = entry,
                        Match = Regex.Match(
                            entry.FullName,
                            @"^ppt/slides/slide(\d+)\.xml$",
                            RegexOptions.IgnoreCase),
                    })
                    .Where(item => item.Match.Success)
                    .OrderBy(item => int.Parse(item.Match.Groups[1].Value))
                    .ToList();

                var builder = new StringBuilder();
                int totalSlides = slideEntries.Count;
                for (int slideIndex = 0; slideIndex < slideEntries.Count; slideIndex++)
                {
                    var slide = slideEntries[slideIndex];
                    using Stream stream = slide.Entry.Open();
                    XDocument document = XDocument.Load(stream);
                    string[] paragraphs = document
                        .Descendants()
                        .Where(node => node.Name.LocalName == "p")
                        .Select(node => string.Concat(node.Descendants()
                            .Where(text => text.Name.LocalName == "t")
                            .Select(text => text.Value)))
                        .Where(text => !string.IsNullOrWhiteSpace(text))
                        .Select(text => text.Trim())
                        .ToArray();

                    builder.AppendLine($"--- Slide {slideIndex + 1}/{totalSlides} ---");
                    foreach (string paragraph in paragraphs)
                        builder.AppendLine(paragraph);
                    builder.AppendLine();
                }
                return builder.ToString().Trim();
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PPT] PPTX text extraction failed: {ex.Message}");
                return null;
            }
        }

        private static async Task<PresentationPageRenderBatch> TryRenderWithPowerPointAsync(
            string filePath,
            string workDir,
            int startPage,
            int maxPages,
            CancellationToken cancellationToken)
        {
            Type? type = Type.GetTypeFromProgID("PowerPoint.Application");
            if (type == null) return new PresentationPageRenderBatch();

            string exportDir = Path.Combine(workDir, "powerpoint");
            Directory.CreateDirectory(exportDir);
            try
            {
                return await RunStaAsync(() =>
                {
                    dynamic? application = null;
                    dynamic? presentation = null;
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        application = Activator.CreateInstance(type);
                        presentation = application.Presentations.Open(filePath, -1, 0, 0);
                        int totalSlides = (int)presentation.Slides.Count;
                        int slideCount = startPage > totalSlides
                            ? 0
                            : Math.Min(totalSlides - startPage + 1, maxPages);
                        for (int index = startPage; index < startPage + slideCount; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string output = Path.Combine(exportDir, $"slide-{index:D3}.png");
                            presentation.Slides[index].Export(
                                output, "PNG", ExportWidth, ExportHeight);
                        }
                        return new PresentationPageRenderBatch
                        {
                            TotalPages = totalSlides,
                            Files = Directory.GetFiles(exportDir, "*.png", SearchOption.AllDirectories)
                                .OrderBy(path => NaturalSortKey(path))
                                .ToList(),
                        };
                    }
                    finally
                    {
                        if (presentation != null)
                        {
                            try { presentation.Close(); } catch { }
                            try { Marshal.FinalReleaseComObject(presentation); } catch { }
                        }
                        if (application != null)
                        {
                            try { application.Quit(); } catch { }
                            try { Marshal.FinalReleaseComObject(application); } catch { }
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PPT] PowerPoint rendering failed: {ex.Message}");
                return new PresentationPageRenderBatch();
            }
        }

        private static async Task<PresentationPageRenderBatch> TryRenderWithLibreOfficeAsync(
            string filePath,
            string workDir,
            int startPage,
            int maxPages,
            CancellationToken cancellationToken)
        {
            string? soffice = FindLibreOfficeExecutable();
            if (soffice == null) return new PresentationPageRenderBatch();

            string outputDir = Path.Combine(workDir, "libreoffice");
            string profileDir = Path.Combine(workDir, "profile");
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(profileDir);

            string profileUri = new Uri(profileDir).AbsoluteUri;
            string arguments =
                $"-env:UserInstallation={profileUri} --headless --nologo --nodefault " +
                $"--convert-to pdf --outdir \"{outputDir}\" \"{filePath}\"";
            var psi = new ProcessStartInfo
            {
                FileName = soffice,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = psi };
            try
            {
                process.Start();
                cancellationToken.ThrowIfCancellationRequested();
                bool exited = await Task.Run(() => process.WaitForExit(LibreOfficeTimeoutMs));
                if (!exited)
                {
                    try { process.Kill(); } catch { }
                    Logger.Warn("[PPT] LibreOffice conversion timed out");
                    return new PresentationPageRenderBatch();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                {
                    Logger.Warn($"[PPT] LibreOffice conversion failed with exit code {process.ExitCode}");
                    return new PresentationPageRenderBatch();
                }

                string pdfPath = Path.Combine(
                    outputDir,
                    Path.GetFileNameWithoutExtension(filePath) + ".pdf");
                if (!File.Exists(pdfPath)) return new PresentationPageRenderBatch();

                RenderedDocumentPageBatch pdfPages =
                    await PdfRenderService.RenderPdfPageBatchToPngAsync(
                        pdfPath,
                        cancellationToken,
                        startPage,
                        maxPages);
                var files = new List<string>();
                for (int index = 0; index < pdfPages.Pages.Count; index++)
                {
                    int slideNumber = startPage + index;
                    string pngPath = Path.Combine(outputDir, $"slide-{slideNumber:D3}.png");
                    File.WriteAllBytes(pngPath, pdfPages.Pages[index]);
                    files.Add(pngPath);
                }
                return new PresentationPageRenderBatch
                {
                    TotalPages = pdfPages.TotalPages,
                    Files = files,
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PPT] LibreOffice rendering failed: {ex.Message}");
                return new PresentationPageRenderBatch();
            }
        }

        private static async Task<string?> TryExtractTextWithPowerPointAsync(
            string filePath,
            CancellationToken cancellationToken)
        {
            Type? type = Type.GetTypeFromProgID("PowerPoint.Application");
            if (type == null) return null;

            try
            {
                return await RunStaAsync(() =>
                {
                    dynamic? application = null;
                    dynamic? presentation = null;
                    var builder = new StringBuilder();
                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        application = Activator.CreateInstance(type);
                        presentation = application.Presentations.Open(filePath, -1, 0, 0);
                        int totalSlides = (int)presentation.Slides.Count;
                        for (int index = 1; index <= totalSlides; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string slideText = ExtractShapesText(presentation.Slides[index].Shapes);
                            builder.AppendLine($"--- Slide {index}/{totalSlides} ---");
                            if (!string.IsNullOrWhiteSpace(slideText))
                                builder.AppendLine(slideText);
                            builder.AppendLine();
                        }
                        return builder.ToString().Trim();
                    }
                    finally
                    {
                        if (presentation != null)
                        {
                            try { presentation.Close(); } catch { }
                            try { Marshal.FinalReleaseComObject(presentation); } catch { }
                        }
                        if (application != null)
                        {
                            try { application.Quit(); } catch { }
                            try { Marshal.FinalReleaseComObject(application); } catch { }
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[PPT] PowerPoint text extraction failed: {ex.Message}");
                return null;
            }
        }

        private static string ExtractShapesText(dynamic shapes)
        {
            var builder = new StringBuilder();
            try
            {
                int count = shapes.Count;
                for (int i = 1; i <= count; i++)
                {
                    dynamic shape = shapes[i];
                    try
                    {
                        if ((int)shape.HasTextFrame != -1) continue;
                        dynamic textFrame = shape.TextFrame;
                        if ((int)textFrame.HasText != -1) continue;
                        string text = ((string)textFrame.TextRange.Text).Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                            builder.AppendLine(text);
                    }
                    catch
                    {
                        // Some media and group shapes do not expose text frames.
                    }
                }
            }
            catch
            {
            }
            return builder.ToString().Trim();
        }

        private static Task<T> RunStaAsync<T>(
            Func<T> action,
            CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<T>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    tcs.TrySetResult(action());
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(cancellationToken);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "DeepSeekPPT-STA",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return tcs.Task;
        }

        private static string? FindLibreOfficeExecutable()
        {
            string[] candidates =
            {
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
            };
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }

            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path)) return null;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim(), "soffice.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch
                {
                }
            }
            return null;
        }

        private static int NaturalSortKey(string path)
        {
            Match match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
            return match.Success && int.TryParse(match.Groups[1].Value, out int value)
                ? value
                : int.MaxValue;
        }

        private static void TryDeleteDirectory(string directory)
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (Exception ex)
            {
                Logger.Info($"[PPT] cleanup failed: {ex.Message}");
            }
        }
    }
}
