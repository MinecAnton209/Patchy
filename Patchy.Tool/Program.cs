using System.Security.Cryptography;
using System.Text;
using BsDiff;
using ICSharpCode.SharpZipLib.Tar;
using Newtonsoft.Json;
using Patchy;
using Patchy.Models;

public class Program
{
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
                    GenerateKeys();
                    break;
                case "sign":
                    if (args.Length < 4) 
                    {
                        Console.WriteLine("Error: Missing arguments for 'sign' command.");
                        PrintUsage();
                        return;
                    }
                    SignRelease(args[1], args[2], args[3]);
                    break;
                case "create-patch":
                    if (args.Length < 4) { PrintUsage(); return; }
                    CreatePatch(args[1], args[2], args[3]);
                    break;
                case "apply-patch":
                    if (args.Length < 4)
                    {
                        Console.WriteLine("Error: Missing arguments for 'apply-patch' command.");
                        PrintUsage();
                        return;
                    }
                    await ApplyPatchTest(args[1], args[2], args[3]);
                    break;
                case "update-check":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: Missing arguments for 'update-check' command.");
                        PrintUsage();
                        return;
                    }
                    await RunUpdateCheck(args[1], args[2]);
                    break;
                case "prepare-release":
                    if (args.Length < 7)
                    {
                        Console.WriteLine("Error: Missing arguments for 'prepare-release' command.");
                        PrintUsage();
                        return;
                    }
                    PrepareRelease(args[1], args[2], args[3], args[4], args[5], args[6]);
                    break;
                case "test-update":
                    if (args.Length < 4) { PrintUsage(); return; }
                    await TestFullUpdate(args[1], args[2], args[3]);
                    break;
                default:
                    Console.WriteLine($"Error: Unknown command '{command}'");
                    PrintUsage();
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"An error occurred: {ex.Message}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Generates a new ECDsa private/public key pair and saves them to PEM files.
    /// </summary>
    private static void GenerateKeys()
    {
        Console.WriteLine("Generating ECDsa key pair using curve nistP256...");
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            // Export the private key
            string privateKeyPem = ecdsa.ExportECPrivateKeyPem();
            File.WriteAllText("privateKey.pem", privateKeyPem);
            Console.WriteLine("Private key saved to privateKey.pem (KEEP THIS SECRET!)");

            // Export the public key
            string publicKeyPem = ecdsa.ExportSubjectPublicKeyInfoPem();
            File.WriteAllText("publicKey.pem", publicKeyPem);
            Console.WriteLine("Public key saved to publicKey.pem (Embed this in your application)");
        }
    }

    /// <summary>
    /// Calculates the hash of the update file, updates the info.json, and signs it.
    /// </summary>
    /// <param name="infoJsonPath">Path to the info.json file.</param>
    /// <param name="privateKeyPath">Path to the privateKey.pem file.</param>
    /// <param name="updateFilePath">Path to the update package (e.g., Update.zip).</param>
    private static void SignRelease(string infoJsonPath, string privateKeyPath, string updateFilePath)
    {
        if (!File.Exists(updateFilePath))
        {
            throw new FileNotFoundException("Update file not found.", updateFilePath);
        }
        if (!File.Exists(infoJsonPath))
        {
            throw new FileNotFoundException("Info JSON file not found.", infoJsonPath);
        }
        if (!File.Exists(privateKeyPath))
        {
            throw new FileNotFoundException("Private key file not found.", privateKeyPath);
        }

        // 1. Calculate the hash of the update file
        Console.WriteLine($"Calculating ECDsa hash for '{updateFilePath}'...");
        string fileHash = CalculateFileHash(updateFilePath);
        Console.WriteLine($"Calculated Hash: {fileHash}");

        // 2. Read the info.json, update the hash, and clear any old signature
        Console.WriteLine($"Updating '{infoJsonPath}' with new hash...");
        string jsonContent = File.ReadAllText(infoJsonPath);
        var updateInfo = JsonConvert.DeserializeObject<UpdateInfo>(jsonContent);
        if (updateInfo == null) throw new Exception("Failed to parse info.json");
        
        updateInfo.FileHash = fileHash;
        updateInfo.Signature = null; 
        
        // 3. Prepare the data for signing
        string dataToSign = JsonConvert.SerializeObject(updateInfo, Formatting.Indented);
        
        Console.WriteLine($"Signing with '{privateKeyPath}'...");
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));
            var dataBytes = Encoding.UTF8.GetBytes(dataToSign);
            var signatureBytes = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
            string signature = Convert.ToBase64String(signatureBytes);

            // 4. Add the new signature and save the final file
            updateInfo.Signature = signature;
            string finalJson = JsonConvert.SerializeObject(updateInfo, Formatting.Indented);
            File.WriteAllText(infoJsonPath, finalJson);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Release info processed and signed successfully!");
            Console.WriteLine($"New Signature: {signature}");
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Helper method to calculate the SHA256 hash of a file.
    /// </summary>
    /// <param name="filePath">Path to the file.</param>
    /// <returns>A lowercase hex string of the hash.</returns>
    private static string CalculateFileHash(string filePath)
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

    /// <summary>
    /// Simulates the client-side update check process for testing.
    /// </summary>
    /// <param name="infoJsonUrl">The URL to the remote info.json file.</param>
    /// <param name="publicKeyPath">Path to the local publicKey.pem file.</param>
    private static async Task RunUpdateCheck(string infoJsonUrl, string publicKeyPath)
    {
        Console.WriteLine($"--- Running Example Update Check ---");
        Console.WriteLine($"Info URL: {infoJsonUrl}");
        Console.WriteLine($"Public Key: {publicKeyPath}");

        string publicKey = File.ReadAllText(publicKeyPath);
        var updater = new PatchyUpdater(infoJsonUrl, publicKey);
        
        long currentVersionId = 2025091400; // Example: current version of the application
        Console.WriteLine($"Current application version ID: {currentVersionId}");

        try
        {
            Console.WriteLine("Checking for updates...");
            UpdateInfo updateInfo = await updater.CheckForUpdatesAsync();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Update info signature is VALID.");
            Console.ResetColor();
            Console.WriteLine($"Latest Version: {updateInfo.Version} ({updateInfo.VersionId})");
            Console.WriteLine($"Release Name: {updateInfo.ReleaseName}");

            if (updateInfo.VersionId > currentVersionId)
            {
                Console.WriteLine("New version available! Downloading...");
                string downloadedFile = await updater.DownloadUpdateAsync(updateInfo);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Download complete and file hash is VALID.");
                Console.ResetColor();
                Console.WriteLine($"Update package saved to: {downloadedFile}");
                Console.WriteLine("Ready to apply the update.");
                // In a real application, you would now trigger the self-update mechanism
            }
            else
            {
                Console.WriteLine("You are already on the latest version.");
            }
        }
        catch (CryptographicException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"SECURITY ALERT: {ex.Message}");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Failed to check for updates: {ex.Message}");
            Console.ResetColor();
        }
    }
    
    private static void CreatePatch(string oldFilePath, string newFilePath, string patchFilePath)
    {
        Console.WriteLine($"Creating patch from '{Path.GetFileName(oldFilePath)}' to '{Path.GetFileName(newFilePath)}'...");
        if (!File.Exists(oldFilePath)) throw new FileNotFoundException("Old file not found.", oldFilePath);
        if (!File.Exists(newFilePath)) throw new FileNotFoundException("New file not found.", newFilePath);

        string? directory = Path.GetDirectoryName(patchFilePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var oldFileBytes = File.ReadAllBytes(oldFilePath);
        var newFileBytes = File.ReadAllBytes(newFilePath);

        using (var outputStream = File.Create(patchFilePath))
        {
            BinaryPatch.Create(oldFileBytes, newFileBytes, outputStream);
        }
        
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Successfully created patch: {patchFilePath}");
        Console.ResetColor();
    }
    
    private static async Task ApplyPatchTest(string oldFilePath, string patchFilePath, string newFilePath)
    {
        Console.WriteLine($"Testing patch application...");
        var updater = new PatchyUpdater("test_url", "test_key");

        try
        {
            await updater.ApplyPatchAsync(oldFilePath, patchFilePath, newFilePath);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Patch applied successfully!");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"An error occurred during patch application: {ex.Message}");
            Console.ResetColor();
        }
    }
    
    private static void PrepareRelease(string oldDir, string newDir, string outputDir, string privateKeyPath, string configPath, string installerPath)
    {
        Console.WriteLine("--- Preparing New Release ---");
        
        Console.WriteLine($"Loading release configuration from '{configPath}'...");
        if (!File.Exists(configPath)) throw new FileNotFoundException("Release config file not found.", configPath);
        var releaseConfig = JsonConvert.DeserializeObject<ReleaseConfig>(File.ReadAllText(configPath));
        if (releaseConfig == null) throw new Exception("Failed to parse release config file.");
        
        if (!Directory.Exists(oldDir)) throw new DirectoryNotFoundException($"Old version directory not found: {oldDir}");
        if (!Directory.Exists(newDir)) throw new DirectoryNotFoundException($"New version directory not found: {newDir}");

        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, true);
        Directory.CreateDirectory(outputDir);

        string? fullPackageHash = null;
        if (!string.IsNullOrEmpty(releaseConfig.FullPackageFile))
        {
            Console.WriteLine($"Creating full release package '{releaseConfig.FullPackageFile}'...");
            string fullPackagePath = Path.Combine(outputDir, releaseConfig.FullPackageFile);
            System.IO.Compression.ZipFile.CreateFromDirectory(newDir, fullPackagePath);
            fullPackageHash = CalculateFileHash(fullPackagePath);
            Console.WriteLine("Full release package created successfully.");
        }

        string oldArchiveFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");
        string newArchiveFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tar");
        string patchFileName = "update.patch";
        string patchFile = Path.Combine(outputDir, patchFileName);
        
        if (!File.Exists(installerPath)) throw new FileNotFoundException("Installer executable not found!", installerPath);
    
        string installerDestPath = Path.Combine(outputDir, releaseConfig.InstallerFile);
        File.Copy(installerPath, installerDestPath, true);
        string installerHash = CalculateFileHash(installerDestPath);
        Console.WriteLine($"Installer '{releaseConfig.InstallerFile}' prepared with hash: {installerHash}");
        
        try
        {
            CreateTarArchive(oldDir, oldArchiveFile);
            CreateTarArchive(newDir, newArchiveFile);
            CreatePatch(oldArchiveFile, newArchiveFile, patchFile);
            
            Console.WriteLine("Generating release manifest (info.json)...");
            var manifest = new SinglePatchManifest
            {
                VersionId = releaseConfig.NewVersionId,
                Version = releaseConfig.Version,
                FromVersionId = releaseConfig.FromVersionId,
                ReleaseName = releaseConfig.ReleaseName,
                Changes = releaseConfig.Changes,
                PatchUrlBase = releaseConfig.PatchUrlBase,
                PatchFile = patchFileName,
                PatchHash = CalculateFileHash(patchFile),
                SourceArchiveHash = CalculateFileHash(oldArchiveFile),
                TargetArchiveHash = CalculateFileHash(newArchiveFile),
                
                FullPackageFile = releaseConfig.FullPackageFile,
                FullPackageHash = fullPackageHash,
                InstallerFile = releaseConfig.InstallerFile,
                InstallerFileHash = installerHash,
            };
            
            string manifestPath = Path.Combine(outputDir, "info.json");
            string json = JsonConvert.SerializeObject(manifest, new JsonSerializerSettings { Formatting = Formatting.Indented, NullValueHandling = NullValueHandling.Ignore });
            File.WriteAllText(manifestPath, json);
            
            SignSimplifiedManifest(manifestPath, privateKeyPath);
        }
        finally
        {
            Console.WriteLine("Cleaning up temporary files...");
            if (File.Exists(oldArchiveFile)) File.Delete(oldArchiveFile);
            if (File.Exists(newArchiveFile)) File.Delete(newArchiveFile);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"--- Release prepared successfully in '{outputDir}' ---");
        Console.ResetColor();
    }
    
    private static void CreateTarArchive(string sourceDirectory, string tarFilePath)
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
    }
    
    private static void SignSimplifiedManifest(string manifestPath, string privateKeyPath)
    {
        Console.WriteLine($"Signing manifest '{manifestPath}'...");
    
        string jsonContent = File.ReadAllText(manifestPath);
        var manifest = JsonConvert.DeserializeObject<SinglePatchManifest>(jsonContent);
        if (manifest == null) throw new Exception("Failed to parse manifest file.");
        
        manifest.Signature = null;
        string dataToSign = JsonConvert.SerializeObject(manifest, Formatting.Indented);
        dataToSign = dataToSign.Replace("\r\n", "\n");
        
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));
            var dataBytes = Encoding.UTF8.GetBytes(dataToSign);
            var signatureBytes = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
            string signature = Convert.ToBase64String(signatureBytes);

            manifest.Signature = signature;
            string finalJson = JsonConvert.SerializeObject(manifest, Formatting.Indented);
            File.WriteAllText(manifestPath, finalJson);
    
            Console.WriteLine("Manifest signed successfully!");
        }
    }
    
    private static async Task TestFullUpdate(string currentVersionDir, string infoJsonUrl, string publicKeyPath)
    {
        Console.WriteLine("--- Testing Full Update Cycle ---");
    
        string publicKey = File.ReadAllText(publicKeyPath);
        var updater = new PatchyUpdater(infoJsonUrl, publicKey);
        
        long testCurrentVersionId = 1; 
    
        try
        {
            await updater.PerformUpdateAsync(currentVersionDir, testCurrentVersionId);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\nUpdate cycle completed successfully!");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\nAn error occurred during update: {ex.Message}");
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
        Console.WriteLine("  prepare-release <old_dir> <new_dir> <output_dir> <private_key> <config.json>");
        Console.WriteLine("    Compares two directories, creates binary patches, generates, and signs the release manifest (info.json).\n");

        Console.WriteLine("--- Utility Commands ---");
        Console.WriteLine("  generate-keys");
        Console.WriteLine("    Generates a new private/public key pair (privateKey.pem, publicKey.pem).\n");

        Console.WriteLine("--- Testing Commands ---");
        Console.WriteLine("  create-patch <old_file> <new_file> <patch_output>");
        Console.WriteLine("    Creates a single binary patch from an old file to a new file.");
        Console.WriteLine("  apply-patch <old_file> <patch_file> <new_file_output>");
        Console.WriteLine("    Applies a single binary patch to an old file to create the new file.");
        Console.WriteLine("  test-update <current_dir> <info_url> <public_key>");
        Console.WriteLine("    Simulates a full client-side update process.\n");
    }
}