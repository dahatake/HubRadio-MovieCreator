using HubRadio.App.Web;

namespace HubRadio.App.Tests;

[TestClass]
public sealed class WebViewHostTests
{
    [TestMethod]
    public void BundledDocument_UsesStrictLocalOnlyContentSecurityPolicy()
    {
        string html = File.ReadAllText(
            ResolveRepositoryPath("src/HubRadio.Web/index.html"));

        StringAssert.Contains(html, "default-src 'none'");
        StringAssert.Contains(html, "script-src 'self'");
        StringAssert.Contains(html, "style-src 'self'");
        StringAssert.Contains(html, "connect-src 'none'");
        StringAssert.Contains(html, "font-src 'self'");
        StringAssert.Contains(html, "media-src 'none'");
        StringAssert.Contains(html, "object-src 'none'");
        StringAssert.Contains(html, "base-uri 'none'");
        StringAssert.Contains(html, "form-action 'none'");
        StringAssert.Contains(html, "frame-src 'none'");
        StringAssert.Contains(html, "worker-src 'none'");
        Assert.IsFalse(html.Contains("'unsafe-inline'", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("'unsafe-eval'", StringComparison.Ordinal));
        Assert.IsFalse(html.Contains("http://", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void TopLevelPolicy_AllowsOnlyExactBundledEntryPoint()
    {
        Assert.IsTrue(WebViewHost.IsAllowedTopLevelUri(WebViewHost.EntryPoint));

        string[] rejected =
        [
            "file:///C:/private/media.mp4",
            "http://appassets.example/index.html",
            "https://example.com/index.html",
            "https://appassets.example/",
            "https://appassets.example/assets/index.js",
            "https://appassets.example/index.html?next=https://example.com",
            "https://appassets.example/index.html#route",
            "https://user@appassets.example/index.html",
            "https://appassets.example:444/index.html",
            "javascript:alert(1)",
            "data:text/html,untrusted",
            "not-a-uri",
            "",
        ];

        foreach (string uri in rejected)
        {
            Assert.IsFalse(WebViewHost.IsAllowedTopLevelUri(uri), uri);
        }
    }

    [TestMethod]
    public void ResourcePolicy_AllowsOnlyBundledHttpsOrigin()
    {
        string[] allowed =
        [
            "https://appassets.example/index.html",
            "https://appassets.example/assets/index.js",
            "https://appassets.example/assets/index.css",
        ];
        foreach (string uri in allowed)
        {
            Assert.IsTrue(WebViewHost.IsAllowedResourceUri(uri), uri);
        }

        string[] rejected =
        [
            "file:///C:/private/media.mp4",
            "http://appassets.example/assets/index.js",
            "https://appassets.example.evil.test/assets/index.js",
            "https://example.com/remote.js",
            "https://user@appassets.example/assets/index.js",
            "https://appassets.example:444/assets/index.js",
            "javascript:alert(1)",
            "data:text/javascript,alert(1)",
            "",
        ];
        foreach (string uri in rejected)
        {
            Assert.IsFalse(WebViewHost.IsAllowedResourceUri(uri), uri);
        }
    }

    private static string ResolveRepositoryPath(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HubRadio.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not resolve repository-relative path '{relativePath}'.");
    }
}
