using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DinkToPdf;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage.Blob;
using Scriban;
using IPdfConverter = DinkToPdf.Contracts.IConverter;

namespace JustDoMyPdf
{
    public static class PdfIt
    {
        static IPdfConverter _pdfConverter = new SynchronizedConverter(new PdfTools());

        [FunctionName("PDFIt")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequestMessage req, TraceWriter log)
        {
            // Setup
            var templateUrl = "http://www.rypens.be/upload/files/Factuur_template.pdf";
            var properties = new
            {
                klantnaam = "Belfius",
                straat = "Kandijstraat",
                nr = "6",
                postcode = "2650",
                gemeente = "Edegem",
                btwnr = "156.65.26.612"
            };

            // Download template
            var template = await DownloadTemplate(templateUrl);

            // Write temp pdf
            var fileName = Path.GetRandomFileName();
            var pdfFileName = $"{fileName}.pdf";
            File.WriteAllBytes(pdfFileName, template);

            // Make html
            await MakeHtml(pdfFileName);

            // Read html file
            var htmlFileName = $"{fileName}.html";
            var html = File.ReadAllText(htmlFileName);

            // Bind properties
            var mappedHtml = BindProperties(html, properties);
            var htmlBytes = Encoding.UTF8.GetBytes(mappedHtml);
            
            File.WriteAllBytes(htmlFileName, htmlBytes);

            // Html to pdf
            await MakePdf(htmlFileName, pdfFileName);

            var pdfBytes = File.ReadAllBytes($"{pdfFileName}");
            /*
             * C:\Users\Aidan\Desktop\temp\phantom\html2pdf.it\bin>phantomjs rasterize.js TemplateTest.html Test.pdf format=A4 orientation=portrait margin=1cm
             */
            
            // Delete temp files

            // Return response
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(pdfBytes)
            };

            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

            return response;
        }

        public static async Task<byte[]> DownloadTemplate(string url)
        {
            using (var client = new HttpClient())
            {
                using (var result = await client.GetAsync(url))
                {
                    if (result.IsSuccessStatusCode)
                    {
                        return await result.Content.ReadAsByteArrayAsync();
                    }
                }
            }

            return null;
        }

        public static Task MakeHtml(string filePath)
        {
            var process = new Process
            {
                StartInfo =
                {
                    FileName = "pdf2htmlEX.exe",
                    Arguments = $"--zoom 1.4 {filePath}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();

            return process.WaitForExitAsync();
        }

        public static Task MakePdf(string filePath, string pdfFileName)
        {
            var arguments = $"rasterize.js {filePath} {pdfFileName} format=A4 orientation=portrait margin=1cm";

            var process = new Process
            {
                StartInfo =
                {
                    FileName = "phantomjs",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };

            process.Start();

            return process.WaitForExitAsync();
        }

        public static void DeleteTempFiles(string filePath)
        {
            File.Delete($"{filePath}.html");
            File.Delete($"{filePath}.pdf");
        }

        public static string BindProperties(string html, object properties)
        {
            var htmlNoSpans = Regex.Replace(html, @"</?span( [^>]*|/)?>", String.Empty);
            var htmlNoScript = Regex.Replace(htmlNoSpans, @"</?script( [^>]*|/)?>", String.Empty);
            var junkScripts = Regex.Replace(htmlNoScript, "</style>(.*)<title>", String.Empty);

            var template = Template.Parse(junkScripts);
            var templateErrors = template.Messages;

            var result = template.Render(properties);

            return result;
        }

        public static Task WaitForExitAsync(this Process process,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var tcs = new TaskCompletionSource<object>();
            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => tcs.TrySetResult(null);

            if (cancellationToken != default(CancellationToken))
                cancellationToken.Register(tcs.SetCanceled);

            return tcs.Task;
        }
    }

}