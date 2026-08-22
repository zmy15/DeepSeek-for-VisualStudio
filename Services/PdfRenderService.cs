using DeepSeek_v4_for_VisualStudio.Models;
using DeepSeek_v4_for_VisualStudio.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeepSeek_v4_for_VisualStudio.Services
{
    /// <summary>
    /// 将 PDF 文档逐页渲染为 PNG 图片，供视觉模型（deepseek-v4-flash-vision-exp）直接读取。
    /// 底层使用 Windows 10+ 内置的 Windows.Data.Pdf 渲染引擎，无需额外原生依赖。
    /// </summary>
    public static class PdfRenderService
    {
        /// <summary>目标渲染密度（DPI），约合 2.08x 缩放，保证文字可读。</summary>
        private const float RenderDpi = 150f;

        /// <summary>单页长边像素上限，确保在视觉模型 8192px（15+ 图时为 4096px）限制之内。</summary>
        private const uint MaxLongEdgePx = 2048;

        /// <summary>单份 PDF 最多直传的页数，超出部分丢弃并记录日志。</summary>
        private const int MaxPages = 20;

        /// <summary>
        /// 同步版本，供重试/回退恢复等同步调用链使用。内部切换到后台线程执行，避免阻塞 UI 线程。
        /// </summary>
        public static List<ChatContentPart>? BuildPdfVisionParts(string pdfPath)
            => Task.Run(async () => await BuildPdfVisionPartsAsync(pdfPath)).GetAwaiter().GetResult();

        /// <summary>
        /// 将 PDF 渲染为 image_url 视觉内容块列表。
        /// 渲染失败或文档无页时返回 null，调用方应回退到 PdfPig 文本解析。
        /// </summary>
        public static async Task<List<ChatContentPart>?> BuildPdfVisionPartsAsync(string pdfPath)
        {
            try
            {
                List<string> uris = await RenderPdfToPngDataUrisAsync(pdfPath);
                if (uris.Count == 0)
                    return null;

                var parts = new List<ChatContentPart>(uris.Count);
                foreach (string uri in uris)
                {
                    parts.Add(new ChatContentPart
                    {
                        Type = "image_url",
                        ImageUrl = new ChatImageUrl { Url = uri },
                    });
                }
                return parts;
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Vision] PDF 直传渲染失败，回退文本解析: {Path.GetFileName(pdfPath)} - {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 将 PDF 逐页渲染为 PNG data URI 列表。受 <see cref="MaxPages"/> 页数上限约束。
        /// </summary>
        private static async Task<List<string>> RenderPdfToPngDataUrisAsync(string pdfPath)
        {
            var uris = new List<string>();

            StorageFile file = await StorageFile.GetFileFromPathAsync(pdfPath);
            PdfDocument pdf = await PdfDocument.LoadFromFileAsync(file);

            int total = (int)pdf.PageCount;
            int count = Math.Min(total, MaxPages);

            for (int i = 0; i < count; i++)
            {
                using (PdfPage page = pdf.GetPage((uint)i))
                {
                    byte[] png = await RenderPageToPngAsync(page);
                    uris.Add("data:image/png;base64," + Convert.ToBase64String(png));
                }
            }

            if (total > MaxPages)
            {
                Logger.Warn($"[Vision] PDF 共 {total} 页，仅直传前 {MaxPages} 页 ← {Path.GetFileName(pdfPath)}");
            }

            return uris;
        }

        /// <summary>
        /// 渲染单个 PDF 页。RenderToStreamAsync 的输出可被 BitmapDecoder 解码，
        /// 此处统一通过 BitmapEncoder 转码为 PNG，避免依赖其内部格式。
        /// </summary>
        private static async Task<byte[]> RenderPageToPngAsync(PdfPage page)
        {
            PdfPageRenderOptions options = BuildRenderOptions(page);

            using (InMemoryRandomAccessStream rendered = new InMemoryRandomAccessStream())
            {
                await page.RenderToStreamAsync(rendered, options);
                rendered.Seek(0);

                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(rendered);
                using (SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync())
                {
                    using (InMemoryRandomAccessStream output = new InMemoryRandomAccessStream())
                    {
                        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
                        encoder.SetSoftwareBitmap(bitmap);
                        await encoder.FlushAsync();
                        return await ReadStreamBytesAsync(output);
                    }
                }
            }
        }

        /// <summary>
        /// 依据页面尺寸计算渲染目标尺寸：按 <see cref="RenderDpi"/> 缩放，并限制长边不超过 <see cref="MaxLongEdgePx"/>。
        /// </summary>
        private static PdfPageRenderOptions BuildRenderOptions(PdfPage page)
        {
            double width = page.Size.Width;
            double height = page.Size.Height;
            if (width <= 0) width = 612;
            if (height <= 0) height = 792;

            double scale = RenderDpi / 72.0;
            double longEdge = Math.Max(width, height) * scale;
            if (longEdge > MaxLongEdgePx)
                scale *= MaxLongEdgePx / longEdge;

            return new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(1, (int)(width * scale)),
                DestinationHeight = (uint)Math.Max(1, (int)(height * scale)),
            };
        }

        /// <summary>
        /// 将随机访问流全部内容读入字节数组。
        /// </summary>
        private static async Task<byte[]> ReadStreamBytesAsync(IRandomAccessStream stream)
        {
            stream.Seek(0);
            using (DataReader reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                uint size = (uint)stream.Size;
                await reader.LoadAsync(size);
                byte[] buffer = new byte[size];
                reader.ReadBytes(buffer);
                return buffer;
            }
        }
    }
}