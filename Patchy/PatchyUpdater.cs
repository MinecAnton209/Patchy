using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Patchy
{
    public class PatchyUpdater
    {
        private readonly HttpClient _httpClient;
        private readonly string _infoUrl;
        private readonly string _publicKeyPem;

        public PatchyUpdater(string infoUrl, string publicKeyPem)
        {
            _infoUrl = infoUrl;
            _publicKeyPem = publicKeyPem;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Patchy-Updater");
        }

        /// <summary>
        /// Checks for updates, verifies the signature of the update information.
        /// </summary>
        /// <returns>UpdateInfo object if a valid update is found.</returns>
        /// <exception cref="HttpRequestException">Thrown on network errors.</exception>
        /// <exception cref="JsonException">Thrown if the info file is malformed.</exception>
        /// <exception cref="CryptographicException">Thrown if the signature is invalid.</exception>
        public async Task<UpdateInfo> CheckForUpdatesAsync()
        {
            // 1. Download the info.json content
            string jsonContent = await _httpClient.GetStringAsync(_infoUrl);
            var updateInfo = JsonConvert.DeserializeObject<UpdateInfo>(jsonContent);

            if (updateInfo == null || string.IsNullOrEmpty(updateInfo.Signature))
            {
                throw new InvalidDataException("Update information is malformed or signature is missing.");
            }

            // 2. Prepare data for verification (the entire JSON object without the "Signature" field)
            var tempObj = JsonConvert.DeserializeObject<dynamic>(jsonContent);
            if (tempObj == null)
            {
                throw new InvalidDataException("Update information is malformed.");
            }
            tempObj.Signature = null;
            // Use Formatting.Indented to match the format used during signing
            string dataToVerify = JsonConvert.SerializeObject(tempObj, Formatting.Indented);
            
            // 3. Verify the signature using the public key
            if (!VerifySignature(dataToVerify, updateInfo.Signature))
            {
                throw new CryptographicException("SIGNATURE VERIFICATION FAILED! The update info file has been tampered with.");
            }

            return updateInfo;
        }

        /// <summary>
        /// Downloads the update file, verifies its hash, and saves it to a temporary path.
        /// </summary>
        /// <param name="updateInfo">The validated UpdateInfo object from CheckForUpdatesAsync.</param>
        /// <returns>The path to the downloaded and verified temporary file.</returns>
        /// <exception cref="HttpRequestException">Thrown on network errors.</exception>
        /// <exception cref="CryptographicException">Thrown if the file hash does not match.</exception>
        public async Task<string> DownloadUpdateAsync(UpdateInfo updateInfo)
        {
            string downloadedFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".zip");

            // 1. Download the update file
            var response = await _httpClient.GetAsync(updateInfo.DownloadUrl);
            response.EnsureSuccessStatusCode();
            using (var fs = new FileStream(downloadedFilePath, FileMode.Create))
            {
                await response.Content.CopyToAsync(fs);
            }

            // 2. Verify the SHA256 hash of the downloaded file
            if (!VerifyFileHash(downloadedFilePath, updateInfo.FileHash))
            {
                File.Delete(downloadedFilePath);
                throw new CryptographicException("FILE HASH VERIFICATION FAILED! The update file is corrupt or has been tampered with.");
            }
            
            return downloadedFilePath;
        }

        private bool VerifySignature(string data, string signature)
        {
            using (var ecdsa = ECDsa.Create())
            {
                ecdsa.ImportFromPem(_publicKeyPem);
                var dataBytes = Encoding.UTF8.GetBytes(data);
                var signatureBytes = Convert.FromBase64String(signature);
                return ecdsa.VerifyData(dataBytes, signatureBytes, HashAlgorithmName.SHA256);
            }
        }

        private bool VerifyFileHash(string filePath, string expectedHash)
        {
            using (var sha256 = SHA256.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    string actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    return actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}