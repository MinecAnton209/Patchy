using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Tar;
using Newtonsoft.Json;
using Patchy.Models;

namespace Patchy
{
    public class PatchyUpdater
    {
        private readonly HttpClient _httpClient;
        private readonly string _infoUrl;
        private readonly string _publicKeyPem;
        private readonly Func<Task<bool>> _confirmFullDownload;

        public PatchyUpdater(string infoUrl, string publicKeyPem, Func<Task<bool>> confirmFullDownloadCallback)
        {
            var handler = new HttpClientHandler() { AllowAutoRedirect = true };
            _infoUrl = infoUrl;
            _publicKeyPem = publicKeyPem;
            _httpClient = new HttpClient(handler);
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Patchy-Updater");
            _confirmFullDownload = confirmFullDownloadCallback ?? throw new ArgumentNullException(nameof(confirmFullDownloadCallback));
        }
        
        public void Dispose()
        {
            _httpClient?.Dispose();
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
            var jObj = Newtonsoft.Json.Linq.JObject.Parse(jsonContent);
            var signature = jObj["Signature"]?.ToString();
    
            if (string.IsNullOrEmpty(signature))
                throw new InvalidDataException("Signature is missing.");
            jObj.Remove("Signature");
            // Use Formatting.Indented to match the format used during signing
            string dataToVerify = jObj.ToString(Formatting.Indented);
            
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
            if (!await VerifyFileHash(downloadedFilePath, updateInfo.FileHash))
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

        private async Task<bool> VerifyFileHash(string filePath, string expectedHash)
        {
            string actualHash = await CalculateFileHash(filePath);
            
            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
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
                    using Stream oldFileStream = File.OpenRead(oldFilePath);
                    using Stream newFileStream = File.Create(newFilePath);
                    BsDiff.BinaryPatch.Apply(oldFileStream, () => File.OpenRead(patchFilePath), newFileStream);
                    Debug.WriteLine($"Successfully created new file: {newFilePath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"!!! FAILED to apply patch: {ex.Message}");
                    throw;
                }
            });
        }
        
        public async Task PerformUpdateAsync(string currentVersionDirectory, long currentVersionId, IProgress<double>? progress = null, IProgress<string>? status = null)
        {
            Debug.WriteLine("Downloading and verifying release manifest...");
            status?.Report("Checking for updates...");
            progress?.Report(0);
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
            status?.Report("Preparing local files...");
            progress?.Report(5);
            string oldArchiveFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");
            try
            {
                await CreateTarArchiveAsync(currentVersionDirectory, oldArchiveFile);
                string localSourceHash = await CalculateFileHash(oldArchiveFile);

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

            string installerUrl = new Uri(new Uri(manifest.PatchUrlBase), manifest.InstallerFile).ToString();
            string packageUrl = new Uri(new Uri(manifest.PatchUrlBase), packageToDownload).ToString();

            Debug.WriteLine("Downloading updater components...");
            status?.Report("Downloading installer components...");
            
            string installerPath = await DownloadFileWithResumeAsync(
                installerUrl, 
                manifest.InstallerFile, 
                progress, 
                startProgress: 10, 
                endProgress: 20
            );
            
            status?.Report("Downloading update files...");
            
            string packagePath = await DownloadFileWithResumeAsync(
                packageUrl, 
                packageToDownload, 
                progress, 
                startProgress: 20, 
                endProgress: 90
            );
            
            status?.Report("Integrity check...");
            progress?.Report(95);
            
            try
            {
                Debug.WriteLine("Verifying downloaded component hashes...");
                
                string actualInstallerHash = await CalculateFileHash(installerPath);
                
                if (string.IsNullOrEmpty(manifest.InstallerFileHash))
                {
                    throw new CryptographicException("Installer hash is missing in manifest!");
                }
                
                if (!string.Equals(actualInstallerHash, manifest.InstallerFileHash, StringComparison.OrdinalIgnoreCase))
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
                    string localPackageHash = await CalculateFileHash(packagePath);
                    if (!string.Equals(localPackageHash, packageHash, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CryptographicException($"Downloaded package hash mismatch! Expected '{packageHash}', got '{localPackageHash}'.");
                    }
                    Debug.WriteLine("Update package hash is VALID.");
                }
                Debug.WriteLine("All component hashes are VALID.");
                
                progress?.Report(100);
                status?.Report("Restarting...");

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
        
        private async Task<string> DownloadFileWithResumeAsync(
            string url, 
            string fileName, 
            IProgress<double>? progressReporter = null, 
            double startProgress = 0, 
            double endProgress = 100)
        {
            string cacheDir = Path.Combine(Path.GetTempPath(), "PatchyDownloads");
            Directory.CreateDirectory(cacheDir);
            string filePath = Path.Combine(cacheDir, fileName);

            int maxRetries = 5;
            int delayMs = 2000;

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    long existingLength = File.Exists(filePath) ? new FileInfo(filePath).Length : 0;

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    if (existingLength > 0)
                    {
                        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingLength, null);
                    }

                    using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        progressReporter?.Report(endProgress);
                        return filePath; 
                    }

                    if (response.StatusCode == System.Net.HttpStatusCode.OK && existingLength > 0)
                    {
                        existingLength = 0;
                        File.Delete(filePath);
                    }
                    else
                    {
                        response.EnsureSuccessStatusCode();
                    }

                    long? totalBytes = response.Content.Headers.ContentLength;
                    if (totalBytes.HasValue) totalBytes += existingLength;
                    
                    FileMode mode = response.StatusCode == System.Net.HttpStatusCode.PartialContent ? FileMode.Append : FileMode.Create;

                    await using var contentStream = await response.Content.ReadAsStreamAsync();
                    await using var fileStream = new FileStream(filePath, mode, FileAccess.Write, FileShare.None, 8192, true);

                    var buffer = new byte[8192];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        existingLength += bytesRead;

                        if (progressReporter != null && totalBytes.HasValue)
                        {
                            double filePercentage = (double)existingLength / totalBytes.Value;
                            double totalProgress = startProgress + (filePercentage * (endProgress - startProgress));
                            progressReporter.Report(totalProgress);
                        }
                    }

                    return filePath;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                {
                    if (attempt == maxRetries) 
                    {
                        Debug.WriteLine($"Failed to download {fileName} after {maxRetries} attempts.");
                        throw;
                    }
                    
                    Debug.WriteLine($"Network error: {ex.Message}. Retrying {attempt}/{maxRetries} in {delayMs}ms...");
                    await Task.Delay(delayMs);
                    delayMs *= 2;
                }
            }
            
            throw new Exception("Unreachable");
        }
    
        private Task ExtractTarArchiveAsync(string tarFilePath, string destinationDirectory)
        {
            return Task.Run(() => 
            {
                using FileStream fs = File.OpenRead(tarFilePath);
                using TarInputStream tarStream = new TarInputStream(fs, Encoding.UTF8);
                
                TarEntry entry;
                while ((entry = tarStream.GetNextEntry()) != null)
                {
                    if (entry.IsDirectory) continue;
                    
                    string destPath = Path.Combine(destinationDirectory, entry.Name);
                    
                    string? dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    
                    using FileStream destStream = File.Create(destPath);
                    tarStream.CopyEntryContents(destStream);
                }
            });
        }
        
        /// <summary>
        /// Downloads the release manifest and verifies its digital signature.
        /// </summary>
        /// <returns>A valid and trusted SinglePatchManifest object.</returns>
        public async Task<SinglePatchManifest> CheckForSimplifiedUpdateAsync()
        {
            string jsonContent = await _httpClient.GetStringAsync(_infoUrl);
            var jObj = Newtonsoft.Json.Linq.JObject.Parse(jsonContent);
            var signature = jObj["Signature"]?.ToString();

            if (string.IsNullOrEmpty(signature))
                throw new InvalidDataException("Signature is missing in manifest.");
            
            jObj.Remove("Signature");
            
            var serializerSettings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                DateTimeZoneHandling = DateTimeZoneHandling.Utc,
                StringEscapeHandling = StringEscapeHandling.Default
            };
            
            string dataToVerify = JsonConvert.SerializeObject(jObj, serializerSettings);
            
            dataToVerify = dataToVerify.Replace("\r\n", "\n");
            
            if (!VerifySignature(dataToVerify, signature))
            {
                Debug.WriteLine($"Data used for verify:\n{dataToVerify}");
                throw new CryptographicException("SIGNATURE VERIFICATION FAILED!");
            }

            Debug.WriteLine("Manifest signature is VALID.");
            return JsonConvert.DeserializeObject<SinglePatchManifest>(jsonContent);
        }

        /// <summary>
        /// Creates a TAR archive from a directory. Runs on a background thread.
        /// </summary>
        private Task CreateTarArchiveAsync(string sourceDirectory, string tarFilePath)
        {
            return Task.Run(() =>
            {
                using FileStream fs = new FileStream(tarFilePath, FileMode.Create, FileAccess.Write, FileShare.None,
                    8192);
                using TarOutputStream tarStream = new TarOutputStream(fs, Encoding.UTF8);
                var files = Directory.GetFiles(sourceDirectory, "*.*", SearchOption.AllDirectories);
                Array.Sort(files);

                foreach (string filename in files)
                {
                    string relativePath = Path.GetRelativePath(sourceDirectory, filename);

                    TarEntry entry = TarEntry.CreateEntryFromFile(filename);
                    entry.Name = relativePath.Replace(Path.DirectorySeparatorChar, '/');

                    tarStream.PutNextEntry(entry);

                    using FileStream inputFileStream = File.OpenRead(filename);
                    inputFileStream.CopyTo(tarStream);

                    tarStream.CloseEntry();
                }
            });
        }

        /// <summary>
        /// Calculates the SHA256 hash of a file.
        /// </summary>
        /// <returns>A lowercase hex string of the hash.</returns>
        private async Task<string> CalculateFileHash(string filePath)
        {
            using var sha256 = SHA256.Create();
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        
        private async Task<string> ApplyPatchAndUpdateAsync(SinglePatchManifest manifest, string oldArchiveFile)
        {
            string patchFile = "";
            string newArchiveFile = "";
            try
            {
                patchFile = await DownloadFileWithResumeAsync(manifest.PatchUrlBase + manifest.PatchFile, manifest.PatchFile);
        
                Debug.WriteLine("Verifying patch hash...");
                string actualPatchHash = await CalculateFileHash(patchFile);
                if (!string.Equals(actualPatchHash, manifest.PatchHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Patch hash mismatch!");
                }
                Debug.WriteLine("Patch hash OK.");
        
                newArchiveFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");
                Debug.WriteLine("Applying patch...");
                await ApplyPatchAsync(oldArchiveFile, patchFile, newArchiveFile);

                Debug.WriteLine("Verifying target archive hash...");
                string actualTargetHash = await CalculateFileHash(newArchiveFile);
                if (!string.Equals(actualTargetHash, manifest.TargetArchiveHash, StringComparison.OrdinalIgnoreCase))
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
            string downloadedZip = await DownloadFileWithResumeAsync(fullPackageUrl, manifest.FullPackageFile);

            Debug.WriteLine("Verifying full package hash...");
            if (!string.IsNullOrEmpty(manifest.FullPackageHash))
            {
                string actualFullHash = await CalculateFileHash(downloadedZip);
                if (!string.Equals(actualFullHash, manifest.FullPackageHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Full package hash mismatch!");
                }
            }
            Debug.WriteLine("Full package hash OK.");
    
            return downloadedZip;
        }
        
        /// <summary>
        /// Applies an update package (update.pkg) to a target directory.
        /// Verifies the manifest signature and all file hashes.
        /// </summary>
        /// <param name="packagePath">Path to the update.pkg file.</param>
        /// <param name="targetDirectory">Directory to apply the update to.</param>
        /// <returns>The UpdatePackageManifest that was applied.</returns>
        /// <exception cref="CryptographicException">Thrown if signature or hash verification fails.</exception>
        public async Task<UpdatePackageManifest> ApplyUpdatePackageAsync(string packagePath, string targetDirectory)
        {
            Debug.WriteLine($"Applying update package: {packagePath}");
            Debug.WriteLine($"Target directory: {targetDirectory}");
            
            string extractDir = Path.Combine(Path.GetTempPath(), "patchy_apply_" + Guid.NewGuid().ToString("N"));
            
            try
            {
                // 1. Extract package
                Debug.WriteLine("Extracting package...");
                ZipFile.ExtractToDirectory(packagePath, extractDir);
                
                // 2. Read and verify manifest
                string manifestPath = Path.Combine(extractDir, "meta.json");
                if (!File.Exists(manifestPath))
                {
                    throw new InvalidDataException("Package does not contain meta.json manifest.");
                }
                
                string jsonContent = await File.ReadAllTextAsync(manifestPath);
                var manifest = JsonConvert.DeserializeObject<UpdatePackageManifest>(jsonContent);
                
                if (manifest == null || string.IsNullOrEmpty(manifest.Signature))
                {
                    throw new InvalidDataException("Manifest is malformed or signature is missing.");
                }
                
                // Verify signature
                var signature = manifest.Signature;
                manifest.Signature = null;
                string dataToVerify = JsonConvert.SerializeObject(manifest, new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore
                });
                dataToVerify = dataToVerify.Replace("\r\n", "\n");
                manifest.Signature = signature;
                
                if (!VerifySignature(dataToVerify, signature))
                {
                    throw new CryptographicException("SIGNATURE VERIFICATION FAILED! The update manifest has been tampered with.");
                }
                Debug.WriteLine("Manifest signature is VALID.");
                
                // 3. Apply file actions
                int applied = 0;
                foreach (var fileAction in manifest.Files)
                {
                    string targetPath = Path.Combine(targetDirectory, fileAction.Path.Replace('/', Path.DirectorySeparatorChar));
                    
                    switch (fileAction.Action.ToLower())
                    {
                        case "modified":
                            await ApplyModifiedFileAsync(extractDir, targetPath, fileAction);
                            applied++;
                            break;
                            
                        case "added":
                            await ApplyAddedFileAsync(extractDir, targetPath, fileAction);
                            applied++;
                            break;
                            
                        case "removed":
                            ApplyRemovedFile(targetPath, fileAction);
                            applied++;
                            break;
                            
                        default:
                            Debug.WriteLine($"Unknown action type: {fileAction.Action} for {fileAction.Path}");
                            break;
                    }
                }
                
                Debug.WriteLine($"Applied {applied} file actions successfully.");
                return manifest;
            }
            finally
            {
                // Cleanup
                if (Directory.Exists(extractDir))
                {
                    try { Directory.Delete(extractDir, true); } catch { }
                }
            }
        }
        
        private async Task ApplyModifiedFileAsync(string extractDir, string targetPath, FileAction fileAction)
        {
            Debug.WriteLine($"Applying patch to: {fileAction.Path}");
            
            if (!File.Exists(targetPath))
            {
                throw new FileNotFoundException($"Target file not found for patching: {fileAction.Path}");
            }
            
            // Verify source hash
            if (!string.IsNullOrEmpty(fileAction.SourceHash))
            {
                string actualSourceHash = await CalculateFileHash(targetPath);
                if (!string.Equals(actualSourceHash, fileAction.SourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException($"Source hash mismatch for {fileAction.Path}. Expected: {fileAction.SourceHash}, Got: {actualSourceHash}");
                }
            }
            
            // Apply patch
            string patchPath = Path.Combine(extractDir, fileAction.PatchFile!.Replace('/', Path.DirectorySeparatorChar));
            string tempOutputPath = targetPath + ".patched";
            
            if (!string.IsNullOrEmpty(fileAction.PackageFileHash))
            {
                string actualPatchHash = await CalculateFileHash(patchPath);
                if (!string.Equals(actualPatchHash, fileAction.PackageFileHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException($"Corrupted patch file in package: {fileAction.PatchFile}");
                }
            }
            
            await ApplyPatchAsync(targetPath, patchPath, tempOutputPath);
            
            // Verify target hash
            if (!string.IsNullOrEmpty(fileAction.TargetHash))
            {
                string actualTargetHash = await CalculateFileHash(tempOutputPath);
                if (!actualTargetHash.Equals(fileAction.TargetHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempOutputPath);
                    throw new CryptographicException($"Target hash mismatch after patching {fileAction.Path}. Expected: {fileAction.TargetHash}, Got: {actualTargetHash}");
                }
            }
            
            // Replace original with patched
            File.Delete(targetPath);
            File.Move(tempOutputPath, targetPath, true);
            
        }
        
        private async Task ApplyAddedFileAsync(string extractDir, string targetPath, FileAction fileAction)
        {
            Debug.WriteLine($"Adding new file: {fileAction.Path}");
            
            string sourcePath = Path.Combine(extractDir, fileAction.AddFile!.Replace('/', Path.DirectorySeparatorChar));
            
            if (!string.IsNullOrEmpty(fileAction.PackageFileHash))
            {
                string actualHash = await CalculateFileHash(sourcePath);
                if (!string.Equals(actualHash, fileAction.PackageFileHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException($"Corrupted file in package: {fileAction.AddFile}");
                }
            }
            
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException($"Added file not found in package: {fileAction.AddFile}");
            }
            
            // Verify hash before copying
            if (!string.IsNullOrEmpty(fileAction.TargetHash))
            {
                string actualHash = await CalculateFileHash(sourcePath);
                if (!actualHash.Equals(fileAction.TargetHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CryptographicException($"Hash mismatch for added file {fileAction.Path}. Expected: {fileAction.TargetHash}, Got: {actualHash}");
                }
            }
            
            // Create directory if needed
            string? directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            await Task.Run(() => File.Copy(sourcePath, targetPath, true));
        }
        
        private void ApplyRemovedFile(string targetPath, FileAction fileAction)
        {
            Debug.WriteLine($"Removing file: {fileAction.Path}");
            
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
            else
            {
                Debug.WriteLine($"  File already removed or doesn't exist: {fileAction.Path}");
            }
        }
    }
}