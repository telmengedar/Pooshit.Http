using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Http.Tests;

[TestFixture, Parallelizable]
public class HttpServiceConstructorTests {

    static string HttpServiceSourcePath([CallerFilePath] string testFilePath = "") {
        string testDir = Path.GetDirectoryName(testFilePath)!;
        return Path.GetFullPath(Path.Combine(testDir, "..", "Pooshit.Http", "HttpService.cs"));
    }

    [Test, Parallelizable]
    [Description("DiVoid #14620: the browser arm cannot run from this project, so the only thing a test here can notice is its removal")]
    public void ConstructorKeepsTheBrowserArmAndDisablesRedirectsOnlyOffIt() {
        string source = File.ReadAllText(HttpServiceSourcePath());

        Assert.That(Regex.IsMatch(source, @"IsOSPlatform\(OSPlatform\.Create\(""BROWSER""\)\)"), Is.True);
        Assert.That(Regex.Matches(source, @"AllowAutoRedirect\s*=\s*false").Count, Is.EqualTo(1));
        Assert.That(Regex.IsMatch(source, @"AllowAutoRedirect\s*=\s*true"), Is.False);
    }
}
