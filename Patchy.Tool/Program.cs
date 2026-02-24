using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BsDiff;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Patchy.Models;

namespace Patchy.Tool
{
    public class Program
    {
        // Use UTF8 without BOM to avoid cross-platform signature verification issues.
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// Main entry point for the Patchy.Tool CLI.
        /// Parses commands and executes the corresponding update building logic.
        /// </summary>
        public static async Task Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            string command = args[0].ToLower();
            try
            {
                switch (command)
                {
                    case "generate-keys":
                        await GenerateKeysAsync();
                        break;
                        
                    case "create-patch":
                        if (args.Length < 4) { PrintUsage(); return; }
                        await CreatePatchAsync(args[1], args[2], args[3]);
                        break;
                        
                    case "hash":
                        if (args.Length < 2) { Console.WriteLine("Error: Missing file path."); return; }
                        Console.WriteLine(await CalculateFileHashAsync(args[1]));
                        break;
                        
                    case "create-update-package":
                        if (args.Length < 4)
                        {
                            Console.WriteLine("Error: Missing arguments for 'create-update-package' command.");
                            PrintUsage();
                            return;
                        }
                        string privateKeyPath = args.Length > 4 ? args[4] : "privateKey.pem";
                        string? configPath = args.Length > 5 ? args[5] : null;
                        await CreateUpdatePackageAsync(args[1], args[2], args[3], privateKeyPath, configPath);
                        break;
                        
                    default:
                        Console.WriteLine($"Error: Unknown command '{command}' or command was removed.");
                        PrintUsage();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ERROR] An error occurred: {ex.Message}");
                Console.ResetColor();
            }
        }

        /// <summary>
        /// Prints the usage instructions for the command-line tool.
        /// </summary> 
        private static void PrintUsage()
        {
            Console.WriteLine("Patchy.Tool - A utility for creating and signing binary patch releases.");
            Console.WriteLine("\nUsage: Patchy.Tool.exe <command> [arguments]\n");
            
            Console.WriteLine("--- Main Commands ---");
            Console.WriteLine("  create-update-package <old_dir> <new_dir> <output_dir> [private_key_path] [config.json]");
            Console.WriteLine("    Creates a file-level update package with per-file patches.");
            Console.WriteLine("    Output: update.pkg (ZIP with meta.json, diffs/, add/)\n");

            Console.WriteLine("--- Utility Commands ---");
            Console.WriteLine("  generate-keys");
            Console.WriteLine("    Generates a new private/public key pair (privateKey.pem, publicKey.pem).\n");
            Console.WriteLine("  create-patch <old_file> <new_file> <patch_output>");
            Console.WriteLine("    Creates a single binary patch from an old file to a new file.");
            Console.WriteLine("  hash <file_path>");
            Console.WriteLine("    Calculates the SHA256 hash of a file.\n");
        }

        /// <summary>
        /// Generates a new ECDsa private/public key pair and saves them to PEM files.
        /// </summary>
        private static async Task GenerateKeysAsync()
        {
            Console.WriteLine("Generating ECDsa key pair using curve nistP256...");
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            
            // Export and save the private key
            string privateKeyPem = ecdsa.ExportECPrivateKeyPem();
            await File.WriteAllTextAsync("privateKey.pem", privateKeyPem, Utf8NoBom);
            Console.WriteLine("Private key saved to privateKey.pem (KEEP THIS SECRET!)");

            // Export and save the public key
            string publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
            await File.WriteAllTextAsync("publicKey.pem", publicKeyPem, Utf8NoBom);
            Console.WriteLine("Public key saved to publicKey.pem (Embed this in your application)");
        }

        /// <summary>
        /// Helper method to asynchronously calculate the SHA256 hash of a file.
        /// </summary>
        /// <param name="filePath">Path to the file.</param>
        /// <returns>A lowercase hex string of the hash.</returns>
        private static async Task<string> CalculateFileHashAsync(string filePath)
        {
            using var sha256 = SHA256.Create();
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        /// <summary>
        /// Creates a bsdiff patch file asynchronously.
        /// </summary>
        private static async Task CreatePatchAsync(string oldFilePath, string newFilePath, string patchFilePath)
        {
            Console.WriteLine($"  -> Creating patch: '{Path.GetFileName(patchFilePath)}'...");
            if (!File.Exists(oldFilePath)) throw new FileNotFoundException("Old file not found.", oldFilePath);
            if (!File.Exists(newFilePath)) throw new FileNotFoundException("New file not found.", newFilePath);

            string? directory = Path.GetDirectoryName(patchFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // BsDiff requires data in memory to build the suffix tree.
            // Using Async for non-blocking IO reads.
            byte[] oldFileBytes = await File.ReadAllBytesAsync(oldFilePath);
            byte[] newFileBytes = await File.ReadAllBytesAsync(newFilePath);

            await using var outputStream = new FileStream(patchFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);
            
            // BsDiff is CPU intensive and synchronous, so we offload it to a background thread
            await Task.Run(() => BinaryPatch.Create(oldFileBytes, newFileBytes, outputStream));
        }

        /// <summary>
        /// Creates a file-level update package by comparing two directories.
        /// Generates per-file bsdiff patches for modified files, copies new files, and builds a signed manifest.
        /// </summary>
        private static async Task CreateUpdatePackageAsync(string oldDir, string newDir, string outputDir, string privateKeyPath, string? configPath)
        {
            Console.WriteLine("--- Creating Update Package ---");
            Console.WriteLine($"Old directory: {oldDir}");
            Console.WriteLine($"New directory: {newDir}");
            Console.WriteLine($"Output directory: {outputDir}");
            
            if (!Directory.Exists(oldDir)) throw new DirectoryNotFoundException($"Old directory not found: {oldDir}");
            if (!Directory.Exists(newDir)) throw new DirectoryNotFoundException($"New directory not found: {newDir}");
            if (!File.Exists(privateKeyPath)) throw new FileNotFoundException("Private key not found.", privateKeyPath);
            
            // 1. Load release configuration if provided
            ReleaseConfig? config = null;
            if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
            {
                string configContent = await File.ReadAllTextAsync(configPath, Utf8NoBom);
                config = JsonConvert.DeserializeObject<ReleaseConfig>(configContent);
                Console.WriteLine($"Loaded config from: {configPath}");
            }
            
            // 2. Prepare output and temporary working directories
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
            Directory.CreateDirectory(outputDir);
            
            string tempDir = Path.Combine(Path.GetTempPath(), "patchy_" + Guid.NewGuid().ToString("N"));
            string diffsDir = Path.Combine(tempDir, "diffs");
            string addDir = Path.Combine(tempDir, "add");
            Directory.CreateDirectory(diffsDir);
            Directory.CreateDirectory(addDir);
            
            try
            {
                // 3. Scan directories for files
                Console.WriteLine("\nScanning directories...");
                var oldFiles = GetRelativeFiles(oldDir);
                var newFiles = GetRelativeFiles(newDir);
                
                Console.WriteLine($"  Old: {oldFiles.Count} files");
                Console.WriteLine($"  New: {newFiles.Count} files");
                
                var fileActions = new List<FileAction>();
                int patchCount = 0, addCount = 0, removeCount = 0, unchangedCount = 0;
                
                // 4. Process all new or modified files
                foreach (var relativePath in newFiles)
                {
                    string oldFilePath = Path.Combine(oldDir, relativePath);
                    string newFilePath = Path.Combine(newDir, relativePath);
                    string newHash = await CalculateFileHashAsync(newFilePath);
                    
                    // Create a safe flat filename for storing inside the package zip
                    string safeFileName = relativePath.Replace(Path.DirectorySeparatorChar, '_').Replace('/', '_');

                    if (oldFiles.Contains(relativePath))
                    {
                        string oldHash = await CalculateFileHashAsync(oldFilePath);

                        if (string.Equals(oldHash, newHash, StringComparison.OrdinalIgnoreCase))
                        {
                            unchangedCount++;
                            continue; // File is unchanged
                        }

                        // File changed - generate binary patch
                        string patchFileName = safeFileName + ".patch";
                        string patchPath = Path.Combine(diffsDir, patchFileName);
                        
                        Console.WriteLine($"  [PATCH] {relativePath}");
                        await CreatePatchAsync(oldFilePath, newFilePath, patchPath);

                        string patchFileHash = await CalculateFileHashAsync(patchPath);

                        fileActions.Add(new FileAction
                        {
                            Path = relativePath.Replace('\\', '/'),
                            Action = "modified",
                            PatchFile = "diffs/" + safeFileName + ".patch",
                            PackageFileHash = patchFileHash,
                            SourceHash = oldHash,
                            TargetHash = newHash
                        });
                        patchCount++;
                    }
                    else
                    {
                        // File is brand new - copy entirely
                        string addPath = Path.Combine(addDir, safeFileName);
                        
                        Console.WriteLine($"  [ADD] {relativePath}");
                        File.Copy(newFilePath, addPath, true);
                        
                        fileActions.Add(new FileAction
                        {
                            Path = relativePath.Replace('\\', '/'),
                            Action = "added",
                            AddFile = "add/" + safeFileName,
                            PackageFileHash = newHash,
                            TargetHash = newHash
                        });
                        addCount++;
                    }
                }
                
                // 5. Process removed files
                foreach (var relativePath in oldFiles)
                {
                    if (!newFiles.Contains(relativePath))
                    {
                        Console.WriteLine($"  [REMOVE] {relativePath}");
                        fileActions.Add(new FileAction
                        {
                            Path = relativePath.Replace('\\', '/'),
                            Action = "removed"
                        });
                        removeCount++;
                    }
                }
                
                Console.WriteLine($"\nSummary: {patchCount} modified, {addCount} added, {removeCount} removed, {unchangedCount} unchanged");
                
                // 6. Handle Fallback Installer if configured
                string? fallbackInstallerFile = null;
                string? fallbackInstallerHash = null;
                
                if (config != null && !string.IsNullOrEmpty(config.InstallerFile) && File.Exists(config.InstallerFile))
                {
                    string installerName = Path.GetFileName(config.InstallerFile);
                    string installerDest = Path.Combine(outputDir, installerName);
                    
                    File.Copy(config.InstallerFile, installerDest, true);
                 
                    fallbackInstallerFile = installerName;
                    fallbackInstallerHash = await CalculateFileHashAsync(installerDest);
                    Console.WriteLine($"  [INSTALLER] Prepared fallback installer: {installerName}");
                }

                // 7. Handle Standalone Full Package creation if configured
                string? fullPackageFile = null;
                string? fullPackageHash = null;

                if (config != null && !string.IsNullOrEmpty(config.FullPackageFile))
                {
                    string fullPath = Path.Combine(outputDir, config.FullPackageFile);
                    
                    Console.WriteLine($"  [FULL] Creating full package: {config.FullPackageFile}");
                    if (File.Exists(fullPath)) File.Delete(fullPath);
                    
                    // Run zip compression on background thread to prevent blocking
                    await Task.Run(() => ZipFile.CreateFromDirectory(newDir, fullPath, CompressionLevel.Optimal, false));
                    
                    fullPackageFile = config.FullPackageFile;
                    fullPackageHash = await CalculateFileHashAsync(fullPath);
                }

                // 8. Generate the update manifest (meta.json)
                var manifest = new UpdatePackageManifest
                {
                    VersionId = config?.NewVersionId ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Version = config?.Version ?? "1.0.0",
                    FromVersionId = config?.FromVersionId ?? 0,
                    ReleaseName = config?.ReleaseName ?? "Update Package",
                    Changes = config?.Changes ?? new List<string>(),
                    
                    RestartRequired = config?.RestartRequired ?? true,
                    Critical = config?.Critical ?? false,
                    
                    FallbackInstallerFile = fallbackInstallerFile,
                    FallbackInstallerHash = fallbackInstallerHash,
                    
                    PatchUrlBase = config?.PatchUrlBase ?? "",
                    FullPackageFile = fullPackageFile,
                    FullPackageHash = fullPackageHash,
                    
                    Files = fileActions
                };
                
                string manifestPath = Path.Combine(tempDir, "meta.json");
                string jsonContent = JsonConvert.SerializeObject(manifest, Formatting.Indented);
                await File.WriteAllTextAsync(manifestPath, jsonContent, Utf8NoBom);
                
                // 9. Cryptographically sign the manifest
                Console.WriteLine("\nSigning manifest...");
                await SignUpdatePackageManifestAsync(manifestPath, privateKeyPath);
                
                // 10. Zip the patches and manifest into final update.pkg
                string packagePath = Path.Combine(outputDir, "update.pkg");
                Console.WriteLine($"\nCreating package: {packagePath}");
                
                if (File.Exists(packagePath)) File.Delete(packagePath);
                await Task.Run(() => ZipFile.CreateFromDirectory(tempDir, packagePath, CompressionLevel.Optimal, false));
                
                // Copy manifest to output dir as info.json for server index
                File.Copy(manifestPath, Path.Combine(outputDir, "info.json"), true);
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n--- Update package created successfully! ---");
                Console.WriteLine($"Package: {packagePath}");
                Console.WriteLine($"Size: {new FileInfo(packagePath).Length / 1024} KB");
                Console.ResetColor();
            }
            finally
            {
                // Always clean up the temporary directory
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
        
        /// <summary>
        /// Gets all files in a directory as relative paths.
        /// </summary>
        private static HashSet<string> GetRelativeFiles(string directory)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories))
            {
                files.Add(Path.GetRelativePath(directory, file));
            }
            return files;
        }
        
        /// <summary>
        /// Signs the update package manifest with an ECDsa private key.
        /// Removes any existing signature, formats it, signs the raw string, and embeds the signature.
        /// </summary>
        private static async Task SignUpdatePackageManifestAsync(string manifestPath, string privateKeyPath)
        {
            // Use JObject to avoid double-serialization and keep exactly the same property order
            string jsonContent = await File.ReadAllTextAsync(manifestPath, Utf8NoBom);
            var jObj = JObject.Parse(jsonContent);
            
            // Remove signature field before hashing/signing
            jObj.Remove("Signature"); 
            
            // Normalize line endings to \n to ensure signature remains valid across Linux/Windows servers
            string dataToSign = jObj.ToString(Formatting.Indented).Replace("\r\n", "\n");
            
            using var ecdsa = ECDsa.Create();
            string privateKeyContent = await File.ReadAllTextAsync(privateKeyPath, Utf8NoBom);
            ecdsa.ImportFromPem(privateKeyContent);
            
            // Generate Signature
            var signatureBytes = ecdsa.SignData(Utf8NoBom.GetBytes(dataToSign), HashAlgorithmName.SHA256);
            string signature = Convert.ToBase64String(signatureBytes);

            // Re-insert signature into JSON and save
            jObj["Signature"] = signature;
            await File.WriteAllTextAsync(manifestPath, jObj.ToString(Formatting.Indented), Utf8NoBom);
            
            Console.WriteLine("Manifest signed successfully!");
        }
    }
}