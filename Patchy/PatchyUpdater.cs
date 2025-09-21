using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Tar;
using Newtonsoft.Json;

namespace Patchy
{
    public class PatchyUpdater
    {
        private readonly HttpClient _httpClient;
        private readonly string _infoUrl;
        private readonly string _publicKeyPem;
        private readonly Func<Task<bool>> _confirmFullDownload;

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
        
        /// <summary>
        /// Applies a binary patch to an old file to create a new file.
        /// </summary>
        /// <param name="oldFilePath">The path to the original file.</param>
        /// <param name="patchFilePath">The path to the downloaded patch file.</param>
        /// <param name="newFilePath">The path where the new, patched file will be created.</param>
        public Task ApplyPatchAsync(string oldFilePath, string patchFilePath, string newFilePath)
        {
            return Task.Run(() =>
            {
                Debug.WriteLine($"Applying patch '{Path.GetFileName(patchFilePath)}' to '{Path.GetFileName(oldFilePath)}'...");
                try
                {
                    using (Stream oldFileStream = File.OpenRead(oldFilePath))
                    using (Stream newFileStream = File.Create(newFilePath))
                    {
                        BsDiff.BinaryPatch.Apply(oldFileStream, () => File.OpenRead(patchFilePath), newFileStream);
                    }
                    Debug.WriteLine($"Successfully created new file: {newFilePath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"!!! FAILED to apply patch: {ex.Message}");
                    throw;
                }
            });
        }
        
        public async Task PerformUpdateAsync(string currentVersionDirectory, long currentVersionId)
        {
            Debug.WriteLine("Downloading and verifying release manifest...");
            var manifest = await CheckForSimplifiedUpdateAsync();

            if (manifest.VersionId <= currentVersionId)
            {
                Debug.WriteLine($"You are on the latest version. Server: {manifest.VersionId}, Client: {currentVersionId}.");
                return;
            }
            Debug.WriteLine($"New version available! Server: {manifest.VersionId}, Client: {currentVersionId}.");

            string packageToDownload;
            string packageHash;
            string updateMode;

            Debug.WriteLine("Creating archive of the current version...");
            string oldArchiveFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");
            try
            {
                await CreateTarArchiveAsync(currentVersionDirectory, oldArchiveFile);
                string localSourceHash = CalculateFileHash(oldArchiveFile);

                if (localSourceHash.Equals(manifest.SourceArchiveHash, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine("Source hash OK. Preparing for patch update.");
                    packageToDownload = manifest.PatchFile;
                    packageHash = manifest.PatchHash;
                    updateMode = "/patch";
                }
                else
                {
                    Debug.WriteLine($"Source hash mismatch! Expected '{manifest.SourceArchiveHash}', but got '{localSourceHash}'.");
                    if (string.IsNullOrEmpty(manifest.FullPackageFile))
                    {
                        throw new Exception("Local files are modified and no full package is available for recovery.");
                    }
                    
                    bool userConfirmed = await _confirmFullDownload();
                    if (!userConfirmed)
                    {
                        Debug.WriteLine("User declined full download. Aborting update.");
                        return;
                    }
                    packageToDownload = manifest.FullPackageFile;
                    packageHash = manifest.FullPackageHash;
                    updateMode = "/full";
                }
            }
            finally
            {
                if (File.Exists(oldArchiveFile)) File.Delete(oldArchiveFile);
            }

            Debug.WriteLine("Downloading updater components...");
            string installerUrl = manifest.PatchUrlBase + manifest.InstallerFile;
            string packageUrl = manifest.PatchUrlBase + packageToDownload;

            Task<string> downloadInstallerTask = DownloadFileAsync(installerUrl, manifest.InstallerFile);
            Task<string> downloadPackageTask = DownloadFileAsync(packageUrl, packageToDownload);
            await Task.WhenAll(downloadInstallerTask, downloadPackageTask);
            
            string installerPath = downloadInstallerTask.Result;
            string packagePath = downloadPackageTask.Result;
            
            try
            {
                Debug.WriteLine("Verifying downloaded component hashes...");
                
                if (string.IsNullOrEmpty(manifest.InstallerFileHash) || 
                    !CalculateFileHash(installerPath).Equals(manifest.InstallerFileHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException("Installer hash mismatch!");
                }
                Debug.WriteLine("Installer hash is VALID.");

                if (string.IsNullOrEmpty(packageHash))
                {
                     Debug.WriteLine("Warning: No hash provided for the update package. Skipping verification.");
                }
                else
                {
                    string localPackageHash = CalculateFileHash(packagePath);
                    if (!localPackageHash.Equals(packageHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CryptographicException($"Downloaded package hash mismatch! Expected '{packageHash}', got '{localPackageHash}'.");
                    }
                    Debug.WriteLine("Update package hash is VALID.");
                }
                Debug.WriteLine("All component hashes are VALID.");

                int currentProcessId = Process.GetCurrentProcess().Id;
                string arguments = $"{updateMode} \"{packagePath}\" /pid {currentProcessId} /path \"{currentVersionDirectory}\"";
                
                Debug.WriteLine($"Launching updater: {installerPath} {arguments}");

                var processInfo = new ProcessStartInfo(installerPath, arguments) { UseShellExecute = true };
                Process.Start(processInfo);

                Debug.WriteLine("Updater launched. Waiting for termination signal from updater...");
            }
            catch
            {
                File.Delete(installerPath);
                File.Delete(packagePath);
                throw;
            }
        }
        
        private async Task<string> DownloadFileAsync(string url, string fileName)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), fileName);
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            using (var fs = new FileStream(tempPath, FileMode.Create))
            {
                await response.Content.CopyToAsync(fs);
            }
            return tempPath;
        }
            
        private Task ExtractTarArchiveAsync(string tarFilePath, string destinationDirectory)
        {
            return Task.Run(() => 
            {
                using (FileStream fs = File.OpenRead(tarFilePath))
                using (TarInputStream tarStream = new TarInputStream(fs, System.Text.Encoding.UTF8))
                {
                    TarEntry entry;
                    while ((entry = tarStream.GetNextEntry()) != null)
                    {
                        if (entry.IsDirectory) continue;
                        string destPath = Path.Combine(destinationDirectory, entry.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                        using (FileStream destStream = File.Create(destPath))
                        {
                            tarStream.CopyEntryContents(destStream);
                        }
                    }
                }
            });
        }
        
        /// <summary>
        /// Downloads the release manifest and verifies its digital signature.
        /// </summary>
        /// <returns>A valid and trusted SinglePatchManifest object.</returns>
        private async Task<SinglePatchManifest> CheckForSimplifiedUpdateAsync()
        {
            string jsonContent = await _httpClient.GetStringAsync(_infoUrl);
            var manifest = JsonConvert.DeserializeObject<SinglePatchManifest>(jsonContent);

            if (manifest == null || string.IsNullOrEmpty(manifest.Signature))
            {
                throw new InvalidDataException("Update manifest is malformed or signature is missing.");
            }

            var signature = manifest.Signature;
            manifest.Signature = null;
            
            string dataToVerify = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            dataToVerify = dataToVerify.Replace("\r\n", "\n");

            manifest.Signature = signature; 

            if (!VerifySignature(dataToVerify, signature))
            {
                throw new CryptographicException("SIGNATURE VERIFICATION FAILED! The update manifest has been tampered with.");
            }

            Debug.WriteLine("Manifest signature is VALID.");
            return manifest;
        }

        /// <summary>
        /// Creates a TAR archive from a directory. Runs on a background thread.
        /// </summary>
        private Task CreateTarArchiveAsync(string sourceDirectory, string tarFilePath)
        {
            return Task.Run(() =>
            {
                using (FileStream fs = new FileStream(tarFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                using (TarOutputStream tarStream = new TarOutputStream(fs, System.Text.Encoding.UTF8))
                {
                    var files = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);
                    Array.Sort(files);

                    foreach (string filename in files)
                    {
                        FileInfo fileInfo = new FileInfo(filename);
                        string relativePath = Path.GetRelativePath(sourceDirectory, filename);
                        
                        TarEntry entry = TarEntry.CreateEntryFromFile(filename);
                        entry.Name = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                        
                        tarStream.PutNextEntry(entry);

                        using (FileStream inputFileStream = File.OpenRead(filename))
                        {
                            inputFileStream.CopyTo(tarStream);
                        }
                        
                        tarStream.CloseEntry();
                    }
                }
            });
        }

        /// <summary>
        /// Calculates the SHA256 hash of a file.
        /// </summary>
        /// <returns>A lowercase hex string of the hash.</returns>
        private string CalculateFileHash(string filePath)
        {
            using (var sha256 = SHA256.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    var hashBytes = sha256.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }
        }
        
        private async Task<string> ApplyPatchAndUpdateAsync(SinglePatchManifest manifest, string oldArchiveFile)
        {
            string patchFile = "";
            string newArchiveFile = "";
            try
            {
                patchFile = await DownloadFileAsync(manifest.PatchUrlBase + manifest.PatchFile, manifest.PatchFile);
        
                Debug.WriteLine("Verifying patch hash...");
                if (!CalculateFileHash(patchFile).Equals(manifest.PatchHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Patch hash mismatch!");
                }
                Debug.WriteLine("Patch hash OK.");
        
                newArchiveFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");
                Debug.WriteLine("Applying patch...");
                await ApplyPatchAsync(oldArchiveFile, patchFile, newArchiveFile);

                Debug.WriteLine("Verifying target archive hash...");
                if (!CalculateFileHash(newArchiveFile).Equals(manifest.TargetArchiveHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Target hash mismatch after applying patch!");
                }
                Debug.WriteLine("Target hash OK.");
        
                return newArchiveFile;
            }
            catch
            {
                if (File.Exists(newArchiveFile)) File.Delete(newArchiveFile);
                throw;
            }
            finally
            {
                if (File.Exists(patchFile)) File.Delete(patchFile);
            }
        }
        
        private async Task<string> DownloadFullPackageAsync(SinglePatchManifest manifest)
        {
            string fullPackageUrl = manifest.PatchUrlBase + manifest.FullPackageFile;
            Debug.WriteLine($"Downloading full package from {fullPackageUrl}...");
            string downloadedZip = await DownloadFileAsync(fullPackageUrl, manifest.FullPackageFile);

            Debug.WriteLine("Verifying full package hash...");
            if (!string.IsNullOrEmpty(manifest.FullPackageHash) && 
                !CalculateFileHash(downloadedZip).Equals(manifest.FullPackageHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Full package hash mismatch!");
            }
            Debug.WriteLine("Full package hash OK.");
    
            return downloadedZip;
        }
        
        public PatchyUpdater(string infoUrl, string publicKeyPem, Func<Task<bool>> confirmFullDownloadCallback)
        {
            _infoUrl = infoUrl;
            _publicKeyPem = publicKeyPem;
            _httpClient = new HttpClient();
            _confirmFullDownload = confirmFullDownloadCallback;
        }
    }
}