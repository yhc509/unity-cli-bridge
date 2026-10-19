using System.Text.RegularExpressions;
using UnityCli.DocGen;

namespace UnityCli.Cli.Tests;

/// <summary>
/// Unity 2021.3 batchmode -quit waits until every async-void operation on the editor
/// synchronization context finishes. The accept loop never finishes on its own, so an
/// async-void listener makes -executeMethod hit "Timeout after 300 seconds while waiting
/// async operations to finish." A Task is not tracked.
/// </summary>
public sealed class BridgeHostListenerTests
{
    [Fact]
    public void ListenerEntryPoints_AreNotAsyncVoid()
    {
        string repoRoot = RepositoryPaths.FindRepoRoot(AppContext.BaseDirectory);
        string source = File.ReadAllText(Path.Combine(
            repoRoot,
            "unity-package",
            "com.yhc509.unity-cli-bridge",
            "Editor",
            "BridgeHost.cs"));

        MatchCollection matches = Regex.Matches(source, @"\basync\s+void\b");

        Assert.Empty(matches);
    }
}
