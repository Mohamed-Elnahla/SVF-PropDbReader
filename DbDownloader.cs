using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.ModelDerivative;
using Autodesk.ModelDerivative.Model;
using Autodesk.SDKManager;

namespace SVF.PropDbReader
{
    /// <summary>
    /// Handles downloading and caching of the Autodesk property database derivative.
    /// </summary>
    public class DbDownloader : IDisposable
    {
        private static readonly HttpClient SharedHttpClient = new();

        private readonly string _accessToken;
        private readonly string _region;
        private readonly string _cacheDir;
        private readonly ModelDerivativeClient _modelDerivativeClient;
        private readonly SDKManager? _sdkManager;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="DbDownloader"/> class.
        /// </summary>
        /// <param name="accessToken">Autodesk access token.</param>
        /// <param name="region">APS region (e.g., "US").</param>
        public DbDownloader(string accessToken, string region = "US")
        {
            ArgumentNullException.ThrowIfNull(accessToken);

            _accessToken = accessToken;
            _region = region;
            _cacheDir = Path.Combine(Path.GetTempPath(), "dbcache");
            Directory.CreateDirectory(_cacheDir);
            _sdkManager = SdkManagerBuilder
                                  .Create()
                                  .Add(new ApsConfiguration())
                                  .Add(ResiliencyConfiguration.CreateDefault())
                                  .Build();
            _modelDerivativeClient = new ModelDerivativeClient(_sdkManager);
        }

        /// <summary>
        /// Downloads (or retrieves from cache) the property database for the given URN.
        /// Returns a path to the cached file.
        /// </summary>
        /// <param name="urn">The model URN.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The local path to the property database, or null if the derivative could not be found.</returns>
        public async Task<string?> DownloadPropertiesDatabaseAsync(string urn, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(urn);

            string safeUrn = SanitizeFilename(urn);
            string cacheFile = Path.Combine(_cacheDir, $"{safeUrn}_properties.sdb");

            if (!IsFileValid(cacheFile))
            {
                var derivativeUrn = await FindPropertyDatabaseDerivativeUrnAsync(urn, _accessToken).ConfigureAwait(false);

                if (derivativeUrn == null)
                    return null;

                string tempDownloadPath = Path.Combine(_cacheDir, $"{safeUrn}_temp.sdb");
                await DownloadDerivativeWithCookiesAsync(urn, derivativeUrn, _accessToken, tempDownloadPath, cancellationToken).ConfigureAwait(false);

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

        /// <summary>
        /// Sanitizes a URN string into a safe filename by replacing non-alphanumeric characters with underscores.
        /// </summary>
        public static string SanitizeFilename(string urn)
        {
            return string.Create(urn.Length, urn, static (span, s) =>
            {
                for (int i = 0; i < s.Length; i++)
                    span[i] = char.IsLetterOrDigit(s[i]) ? char.ToLowerInvariant(s[i]) : '_';
            });
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
            var manifest = await _modelDerivativeClient.GetManifestAsync(urn: urn, accessToken: accessToken).ConfigureAwait(false);
            var manifestHelper = new ManifestHelper(manifest);
            var pdb = manifestHelper.Search(
                type: "resource",
                role: "Autodesk.CloudPlatform.PropertyDatabase"
                ).FirstOrDefault();

            return pdb?.Urn;
        }

        /// <summary>
        /// Downloads the derivative using signed cookies.
        /// </summary>
        private static async Task DownloadDerivativeWithCookiesAsync(string urn, string derivativeUrn, string accessToken, string outputPath, CancellationToken cancellationToken)
        {
            string cookiesUrl = $"https://developer.api.autodesk.com/modelderivative/v2/designdata/{Uri.EscapeDataString(urn)}/manifest/{Uri.EscapeDataString(derivativeUrn)}/signedcookies";

            var req = new HttpRequestMessage(HttpMethod.Get, cookiesUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);

            using var resp = await SharedHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            // Read only the JSON metadata as a string (small)
            using var jsonStream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var fileUrl = doc.RootElement.GetProperty("url").GetString();

            var cookies = string.Join("; ",
                resp.Headers.TryGetValues("Set-Cookie", out var setCookies)
                    ? setCookies.Select(c => c.Split(';')[0])
                    : Array.Empty<string>());

            var fileReq = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            if (!string.IsNullOrEmpty(cookies))
                fileReq.Headers.Add("Cookie", cookies);

            using var fileResp = await SharedHttpClient.SendAsync(fileReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            fileResp.EnsureSuccessStatusCode();

            // Stream the file directly to disk, no RAM buffering
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
            using var stream = await fileResp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Disposes managed resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases managed and/or unmanaged resources.
        /// </summary>
        /// <param name="disposing">True if called from <see cref="Dispose()"/>; false if called from the finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                if (_modelDerivativeClient is IDisposable disposableClient)
                    disposableClient.Dispose();
                if (_sdkManager is IDisposable disposableSdk)
                    disposableSdk.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// Destructor.
        /// </summary>
        ~DbDownloader()
        {
            Dispose(disposing: false);
        }
    }
}
