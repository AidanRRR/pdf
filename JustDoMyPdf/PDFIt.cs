using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Http;
using DinkToPdf;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Scriban;
using Scriban.Parsing;
using Scriban.Runtime;
using IPdfConverter = DinkToPdf.Contracts.IConverter;

namespace JustDoMyPdf
{
    public static class PdfIt
    {
        static IPdfConverter _pdfConverter = new SynchronizedConverter(new PdfTools());

        [FunctionName("PDFIt")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequestMessage req, TraceWriter log)
        {
            var requestContent = await req.Content.ReadAsStringAsync();
            dynamic requestJson = JObject.Parse(requestContent);

            var properties = new Dictionary<string, string>();
            foreach (var jToken in (JToken) requestJson)
            {
                var prop = (JProperty) jToken;
                properties.Add(prop.Name, prop.Value.Value<string>());
            }

            // Download template
            var template = await DownloadTemplate(properties["templateUrl"]);

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
            
            // Delete temp files
            DeleteTempFiles(fileName);

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

        public static string BindProperties(string html, IDictionary<string, string> properties)
        {
            var htmlNoSpans = Regex.Replace(html, @"<span class=""_ _0""></span>", String.Empty);
            var junkScripts = Regex.Replace(htmlNoSpans, "<script>(.*)</script>",String.Empty, RegexOptions.Singleline);

            var template = Template.Parse(junkScripts);

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