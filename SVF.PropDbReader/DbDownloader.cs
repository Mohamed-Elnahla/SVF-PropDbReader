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
    public class DbDownloader
    {
        private readonly string _accessToken;
        private readonly string _clientSecret;
        private readonly string _region;
        private readonly string _cacheDir;
        private readonly ModelDerivativeClient modelDerivativeClient = null!;

        /// <summary>
        /// Initializes a new instance of the <see cref="DbDownloader"/> class.
        /// </summary>
        /// <param name="clientId">Autodesk client ID.</param>
        /// <param name="clientSecret">Autodesk client secret.</param>
        /// <param name="region">APS region (e.g., "US").</param>
        /// <param name="cacheDir">Directory for caching property databases.</param>
        public DbDownloader(string accessToken, string region = "US", string? cacheDir = null)
        {
            _accessToken = accessToken;
            _region = region;
            _cacheDir = cacheDir ?? Path.Combine(AppContext.BaseDirectory, "dbcache");
            Directory.CreateDirectory(_cacheDir);
            // Instantiate SDK manager as below.  
            SDKManager sdkManager = SdkManagerBuilder
                                  .Create()
                                  .Add(new ApsConfiguration())
                                  .Add(ResiliencyConfiguration.CreateDefault())
                                  .Build();
            // Instantiate ModelDerivativeApi using the created SDK manager
            modelDerivativeClient = new ModelDerivativeClient(sdkManager);
        }

        /// <summary>
        /// Downloads (or retrieves from cache) the property database for the given URN.
        /// Returns a path to a request-specific temporary copy.
        /// </summary>
        public async Task<string?> DownloadPropertiesDatabaseAsync(string urn)
        {
            string safeUrn = SanitizeFilename(urn);
            string cacheFile = Path.Combine(_cacheDir, $"{safeUrn}_properties.sdb");

            if (!await IsFileValidAsync(cacheFile))
            {
                var token = _accessToken;
                var derivativeUrn = await FindPropertyDatabaseDerivativeUrnAsync(urn, token);

                if (derivativeUrn == null)
                    return null;

                string tempDownloadPath = Path.Combine(_cacheDir, $"{safeUrn}_temp.sdb");
                await DownloadDerivativeWithCookiesAsync(urn, derivativeUrn, token, tempDownloadPath);

                if (await IsFileValidAsync(tempDownloadPath))
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

            // Create a request-specific temp copy
            string tempFile = Path.Combine(Path.GetTempPath(), $"{safeUrn}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid().ToString("N")}_properties.sdb");
            File.Copy(cacheFile, tempFile, true);
            return tempFile;
        }

        private static string SanitizeFilename(string urn)
        {
            return string.Concat(urn.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        }

        private static async Task<bool> IsFileValidAsync(string filePath)
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
            var manifest = await modelDerivativeClient.GetManifestAsync(urn: urn, accessToken:accessToken);
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

            var resp = await http.SendAsync(req);
            resp.EnsureSuccessStatusCode();

            var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var fileUrl = json.RootElement.GetProperty("url").GetString();

            var cookies = string.Join("; ",
                resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
                    ? setCookies.Select(c => c.Split(';')[0])
                    : Array.Empty<string>());

            var fileReq = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            if (!string.IsNullOrEmpty(cookies))
                fileReq.Headers.Add("Cookie", cookies);

            using var fileResp = await http.SendAsync(fileReq, HttpCompletionOption.ResponseHeadersRead);
            fileResp.EnsureSuccessStatusCode();

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await fileResp.Content.CopyToAsync(fs);
        }
    }
}
