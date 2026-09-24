using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ZSnaper.Plugins;

namespace ZSnaper.PluginStability;

internal static class PluginStabilityProgram
{
    private static async Task<int> Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "ZSnaper-plugin-stability-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestVersionParsing();
            string packagePath = CreateValidPackage(root);
            TestPackageInspection(packagePath);
            TestTraversalPackage(root);
            TestIllegalFilenamePackage(root);
            await TestInstallAndLifecycleAsync(root);
            await TestUpdateResponseValidationAsync();
            Console.WriteLine("Plugin stability tests passed.");
            return 0;
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Do not hide a test failure because a temporary file is still locked.
            }
        }
    }

    private static void TestVersionParsing()
    {
        Assert(PluginCompatibility.IsValidVersion("1.2.3-alpha"), "semantic version should be accepted");
        Assert(PluginCompatibility.Satisfies("1.5.0", ">=1.0.0 <2.0.0"), "range should match");
        Assert(!PluginCompatibility.Satisfies("2.0.0", ">=1.0.0 <2.0.0"), "range upper bound should be exclusive");
        Assert(PluginCompatibility.Satisfies("1.8.0", "^1.2.3"), "caret range should match");
        Assert(!PluginCompatibility.Satisfies("2.0.0", "^1.2.3"), "caret range should reject next major");
        Assert(!PluginCompatibility.IsValidVersion("999999999999999999999.0.0"), "overflowing version must not throw or match");
    }

    private static string CreateValidPackage(string root)
    {
        string source = Path.Combine(root, "valid-source");
        Directory.CreateDirectory(source);
        File.WriteAllText(
            Path.Combine(source, PluginContract.ManifestFileName),
            """
            {
              "manifestVersion": 1,
              "id": "example.plugin",
              "name": "Example Plugin",
              "description": "Stability test plugin",
              "version": "1.0.0",
              "entry": { "assembly": "Example.Plugin.dll", "type": "Example.Plugin.EntryPoint" },
              "requires": { "pluginApi": ">=1.0.0 <2.0.0", "appVersion": ">=0.0.3" }
            }
            """);
        File.WriteAllBytes(Path.Combine(source, "Example.Plugin.dll"), [0x4D, 0x5A, 0x90, 0x00]);
        string packagePath = Path.Combine(root, "example.zsp");
        ZipFile.CreateFromDirectory(source, packagePath, CompressionLevel.Fastest, includeBaseDirectory: false);
        return packagePath;
    }

    private static void TestPackageInspection(string packagePath)
    {
        PluginPackageInspection inspection = PluginPackageService.Inspect(packagePath, "0.0.3", PluginContract.ApiVersion);
        Assert(inspection.IsValid, "valid package should pass inspection: " + string.Join(" | ", inspection.Errors));
        Assert(inspection.Manifest?.Id == "example.plugin", "manifest id should be read");
        Assert(PluginPackageService.VerifySha256(packagePath, GetSha256(packagePath)), "package hash should verify");
        Assert(!PluginPackageService.VerifySha256(packagePath, new string('0', 64)), "wrong package hash should fail");
    }

    private static void TestTraversalPackage(string root)
    {
        string packagePath = Path.Combine(root, "traversal.zsp");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        using (StreamWriter writer = new(archive.CreateEntry("../escape.txt").Open()))
        {
            writer.Write("must be rejected");
        }

        PluginPackageInspection inspection = PluginPackageService.Inspect(packagePath, "0.0.3");
        Assert(!inspection.IsValid, "traversal package should be rejected");
        Assert(inspection.Errors.Any(error => error.Contains("escapes", StringComparison.OrdinalIgnoreCase)), "traversal error should be explicit");
    }

    private static void TestIllegalFilenamePackage(string root)
    {
        string packagePath = Path.Combine(root, "illegal-name.zsp");
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        using (StreamWriter writer = new(archive.CreateEntry("bad:name.dll").Open()))
        {
            writer.Write("must be rejected");
        }

        PluginPackageInspection inspection = PluginPackageService.Inspect(packagePath, "0.0.3");
        Assert(!inspection.IsValid, "package with an illegal Windows filename should be rejected");
    }

    private static async Task TestInstallAndLifecycleAsync(string root)
    {
        string packagePath = Path.Combine(root, "sample.zsp");
        PluginManifest manifest = new()
        {
            Id = "example.plugin", Name = "Example Plugin", Version = "1.0.0",
            Entry = new PluginEntryPoint
            {
                Assembly = "Example.Plugin.dll", Type = "Example.Plugin.ExamplePlugin"
            },
            Requires = new PluginRequirements
            {
                AppVersion = ">=0.0.5", PluginApi = ">=1.0.0 <2.0.0"
            }
        };
        using (ZipArchive archive = ZipFile.Open(packagePath, ZipArchiveMode.Create))
        {
            using (StreamWriter writer = new(archive.CreateEntry("manifest.json").Open()))
                writer.Write(PluginManifestJson.Serialize(manifest));
            using Stream entry = archive.CreateEntry("Example.Plugin.dll").Open();
            using FileStream assembly = File.OpenRead(typeof(Example.Plugin.ExamplePlugin).Assembly.Location);
            assembly.CopyTo(entry);
        }

        string store = Path.Combine(root, "store");
        using PluginRuntimeManager runtime = new(new PluginInstaller(store));
        PluginOperationResult installed = runtime.Install(packagePath);
        Assert(installed.Success, "sample install should succeed: " + installed.Message);
        Assert(runtime.List().Count == 1 && !runtime.List()[0].Enabled, "new plugin should be disabled");
        PluginOperationResult enabled = await runtime.EnableAsync("example.plugin");
        Assert(enabled.Success && runtime.IsRunning("example.plugin"),
            "sample plugin should initialize and enable: " + enabled.Message);
        Assert(runtime.GetActions().Any(action => action.Item.Id == "log_capture"),
            "enabled plugin should register its action");
        PluginOperationResult disabled = await runtime.DisableAsync("example.plugin");
        Assert(disabled.Success && !runtime.IsRunning("example.plugin") && runtime.GetActions().Count == 0,
            "disabled plugin should remove actions");
        string installedManifest = Path.Combine(store, "installed", "example.plugin", "manifest.json");
        PluginManifest incompatible = PluginManifestService.Load(installedManifest);
        incompatible.Requires.AppVersion = ">=99.0.0";
        File.WriteAllText(installedManifest, PluginManifestJson.Serialize(incompatible));
        Assert(runtime.List().Count == 1 && !runtime.List()[0].Compatible,
            "incompatible installed plugin should remain visible for removal");
        Assert(!(await runtime.EnableAsync("example.plugin")).Success,
            "incompatible installed plugin should not load");
        PluginOperationResult removed = await runtime.RemoveAsync("example.plugin");
        Assert(removed.Success && runtime.List().Count == 0, "plugin should uninstall cleanly");
    }

    private static async Task TestUpdateResponseValidationAsync()
    {
        PluginManifest manifest = new()
        {
            Id = "example.plugin",
            Name = "Example Plugin",
            Version = "1.0.0",
            Entry = new PluginEntryPoint { Assembly = "Example.Plugin.dll", Type = "Example.Plugin.EntryPoint" },
            Requires = new PluginRequirements { AppVersion = ">=0.0.3", PluginApi = ">=1.0.0 <2.0.0" },
            Update = new PluginUpdateMetadata { CheckUrl = "https://updates.example.test/example.json" }
        };
        PluginUpdateInfo update = new()
        {
            PluginId = manifest.Id,
            Version = "1.1.0",
            AppVersion = ">=0.0.3",
            PluginApi = ">=1.0.0 <2.0.0",
            PackageUrl = "https://updates.example.test/example.zsp",
            Sha256 = new string('a', 64)
        };

        string json = JsonSerializer.Serialize(update, PluginManifestJson.Options);
        using HttpClient client = new(new StubHandler(() => CreateResponse(json)));
        PluginUpdateCheckResult result = await new PluginUpdateClient(client).CheckAsync(manifest);
        Assert(result.IsSuccess && result.HasUpdate, "valid update response should pass");

        PluginUpdateInfo invalid = new()
        {
            PluginId = manifest.Id,
            Version = "1.1.0",
            PackageUrl = "http://updates.example.test/example.zsp",
            Sha256 = "bad"
        };
        using HttpClient invalidClient = new(new StubHandler(() => CreateResponse(JsonSerializer.Serialize(invalid, PluginManifestJson.Options))));
        PluginUpdateCheckResult invalidResult = await new PluginUpdateClient(invalidClient).CheckAsync(manifest);
        Assert(!invalidResult.IsSuccess && invalidResult.ErrorMessage?.Contains("HTTPS", StringComparison.OrdinalIgnoreCase) == true, "insecure package URL should be rejected");

        string oversizedJson = new string('x', 1024 * 1024 + 1);
        using HttpClient oversizedClient = new(new StubHandler(() => CreateResponse(oversizedJson)));
        PluginUpdateCheckResult oversizedResult = await new PluginUpdateClient(oversizedClient).CheckAsync(manifest);
        Assert(!oversizedResult.IsSuccess && oversizedResult.ErrorMessage?.Contains("too large", StringComparison.OrdinalIgnoreCase) == true, "oversized response should be rejected");
    }

    private static HttpResponseMessage CreateResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static string GetSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StubHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory());
    }
}
