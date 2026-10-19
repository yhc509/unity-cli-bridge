using UnityCli.Protocol;

namespace UnityCli.Cli.Services;

/// <summary>
/// Several Unity projects were found on disk and neither a live Editor nor an explicit pin
/// selected exactly one of them. Callers must pass <c>--project</c>.
/// </summary>
public sealed class AmbiguousProjectException : Exception
{
    public const string ErrorCode = "AMBIGUOUS_PROJECT";

    public AmbiguousProjectException(IReadOnlyList<string> candidates)
        : base(BuildMessage(candidates))
    {
        Candidates = candidates
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public IReadOnlyList<string> Candidates { get; }

    public string ToDetailsJson()
    {
        return ProtocolJson.Serialize(new Dictionary<string, object?>
        {
            ["candidates"] = Candidates,
        });
    }

    private static string BuildMessage(IReadOnlyList<string> candidates)
    {
        var lines = candidates
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "  - " + path);
        return "More than one Unity project was found under the working directory. "
            + "Pass --project <path> to choose one.\nCandidates:\n"
            + string.Join("\n", lines);
    }
}
