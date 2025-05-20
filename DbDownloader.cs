using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Authentication;
using Autodesk.ModelDerivative;
using Autodesk.ModelDerivative.Model;
using Microsoft.Data.Sqlite;
using System.Net.Http;
using System.Net;
using System.Text.Json;
using Autodesk.SDKManager;

namespace SVF.PropDbReader
{
    /// <summary>
    /// Handles downloading and caching of the Autodesk property database derivative.
    /// </summary>
    public class DbDownloader : IDisposable
    {
        private readonly string _accessToken;
        private readonly string _region;
        private readonly string _cacheDir;
        private readonly ModelDerivativeClient modelDerivativeClient = null!;
        private readonly SDKManager? _sdkManager;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DbDownloader"/> class.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="region">APS region (e.g., "US").</param>
        public DbDownloader(string accessToken, string region = "US")
        {
            _accessToken = accessToken;
            _region = region;
            _cacheDir = Path.Combine(Path.GetTempPath(), "dbcache");
            Directory.CreateDirectory(_cacheDir);
            _sdkManager = SdkManagerBuilder
                                  .Create()
                                  .Add(new ApsConfiguration())
                                  .Add(ResiliencyConfiguration.CreateDefault())
                                  .Build();
            modelDerivativeClient = new ModelDerivativeClient(_sdkManager);
        }

        /// <summary>
        /// Downloads (or retrieves from cache) the property database for the given URN.
        /// Returns a path to a request-specific temporary copy.
        /// </summary>
        public async Task<string?> DownloadPropertiesDatabaseAsync(string urn)
        {
            string safeUrn = SanitizeFilename(urn);
            string cacheFile = Path.Combine(_cacheDir, $"{safeUrn}_properties.sdb");

            if (!IsFileValid(cacheFile))
            {
                var token = _accessToken;
                var derivativeUrn = await FindPropertyDatabaseDerivativeUrnAsync(urn, token);

                if (derivativeUrn == null)
                    return null;

                string tempDownloadPath = Path.Combine(_cacheDir, $"{safeUrn}_temp.sdb");
                await DownloadDerivativeWithCookiesAsync(urn, derivativeUrn, token, tempDownloadPath);

                if (IsFileValid(tempDownloadPath))
                {
                    if (File.Exists(cacheFile))
                        File.Delete(cacheFile);
                    File.Move(tempDownloadPath, cacheFile);
                }
                else
                {
                    if (File.Exists(tempDownloadPath))
                        File.Delete(tempDownloadPath);
                    return null;
                }
            }
            return cacheFile;
        }

        private static string SanitizeFilename(string urn)
        {
            return string.Concat(urn.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        }

        private static bool IsFileValid(string filePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                return info.Exists && info.Length > 100;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string?> FindPropertyDatabaseDerivativeUrnAsync(string urn, string accessToken)
        {
            var manifest = await modelDerivativeClient.GetManifestAsync(urn: urn, accessToken: accessToken);
            var mainfestHelper = new ManifestHelper(manifest);
            var pdb = mainfestHelper.Search(
                type: "resource",
                role: "Autodesk.CloudPlatform.PropertyDatabase"
                ).FirstOrDefault();

            return pdb?.Urn;
        }

        /// <summary>
        /// Downloads the derivative using signed cookies.
        /// </summary>
        private async Task DownloadDerivativeWithCookiesAsync(string urn, string derivativeUrn, string accessToken, string outputPath)
        {
            string cookiesUrl = $"https://developer.api.autodesk.com/modelderivative/v2/designdata/{Uri.EscapeDataString(urn)}/manifest/{Uri.EscapeDataString(derivativeUrn)}/signedcookies";
            using var http = new HttpClient();

            var req = new HttpRequestMessage(HttpMethod.Get, cookiesUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            // Read only the JSON metadata as a string (small)
            using var jsonStream = await resp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(jsonStream);
            var fileUrl = doc.RootElement.GetProperty("url").GetString();

            var cookies = string.Join("; ",
                resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
                    ? setCookies.Select(c => c.Split(';')[0])
                    : Array.Empty<string>());

            var fileReq = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            if (!string.IsNullOrEmpty(cookies))
                fileReq.Headers.Add("Cookie", cookies);

            using var fileResp = await http.SendAsync(fileReq, HttpCompletionOption.ResponseHeadersRead);
            fileResp.EnsureSuccessStatusCode();

            // Stream the file directly to disk, no RAM buffering
            using (var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            using (var stream = await fileResp.Content.ReadAsStreamAsync())
            {
                await stream.CopyToAsync(fs);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (modelDerivativeClient is IDisposable disposableClient)
                disposableClient.Dispose();
            if (_sdkManager is IDisposable disposableSdk)
                disposableSdk.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }
        ~DbDownloader()
        {
            Dispose();
        }
    }
}
