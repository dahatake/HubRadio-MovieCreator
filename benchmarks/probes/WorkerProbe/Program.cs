using System.Security.Cryptography;
using System.Text.Json;

if (args.Length != 3 || args[0] != "verify-model")
{
    Console.Error.WriteLine("Usage: HubRadio.WorkerProbe verify-model <model-path> <expected-sha256>");
    return 2;
}

string modelPath = Path.GetFullPath(args[1]);
string expectedHash = args[2];
if (expectedHash.Length != 64 || expectedHash.Any(character => !Uri.IsHexDigit(character)))
{
    Console.Error.WriteLine("The expected model SHA-256 is invalid.");
    return 2;
}
if (!File.Exists(modelPath))
{
    Console.Error.WriteLine("The model file does not exist.");
    return 1;
}

await using FileStream stream = File.OpenRead(modelPath);
string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
Console.WriteLine(JsonSerializer.Serialize(new
{
    schemaVersion = "1.0.0",
    modelFileName = Path.GetFileName(modelPath),
    modelByteSize = stream.Length,
    modelSha256 = hash,
    expectedModelSha256 = expectedHash,
    passed = stream.Length > 0 && string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase),
}));
return stream.Length > 0 && string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
