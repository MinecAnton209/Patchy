using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Patchy;

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
                case "update-check":
                    if (args.Length < 3)
                    {
                        Console.WriteLine("Error: Missing arguments for 'update-check' command.");
                        PrintUsage();
                        return;
                    }
                    await RunUpdateCheck(args[1], args[2]);
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
        Console.WriteLine($"Calculating SHA256 hash for '{updateFilePath}'...");
        string fileHash = CalculateFileHash(updateFilePath);
        Console.WriteLine($"Calculated Hash: {fileHash}");

        // 2. Read the info.json, update the hash, and clear any old signature
        Console.WriteLine($"Updating '{infoJsonPath}' with new hash...");
        string jsonContent = File.ReadAllText(infoJsonPath);
        dynamic? jsonObj = JsonConvert.DeserializeObject(jsonContent);
        
        if (jsonObj == null)
        {
            throw new InvalidDataException($"The file '{infoJsonPath}' is not a valid JSON object.");
        }
        
        jsonObj.FileHash = fileHash;
        jsonObj.Signature = null;
        
        // 3. Prepare the data for signing
        string dataToSign = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
        
        Console.WriteLine($"Signing with '{privateKeyPath}'...");
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));
            var dataBytes = Encoding.UTF8.GetBytes(dataToSign);
            var signatureBytes = ecdsa.SignData(dataBytes, HashAlgorithmName.SHA256);
            string signature = Convert.ToBase64String(signatureBytes);

            // 4. Add the new signature and save the final file
            jsonObj.Signature = signature;
            string finalJson = JsonConvert.SerializeObject(jsonObj, Formatting.Indented);
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

    /// <summary>
    /// Prints the usage instructions for the command-line tool.
    /// </summary>
    private static void PrintUsage()
    {
        Console.WriteLine("Patchy.Tool - A utility for signing and testing updates.");
        Console.WriteLine("Usage: Patchy.Tool.exe <command> [arguments]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  generate-keys                                   Generates privateKey.pem and publicKey.pem");
        Console.WriteLine("  sign <info.json> <privateKey.pem> <update.zip>  Calculates hash of update.zip, adds it to");
        Console.WriteLine("                                                  info.json, and signs the file.");
        Console.WriteLine("  update-check <url> <publicKey.pem>              Simulates a client update check");
    }
}