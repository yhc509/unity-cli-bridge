using System.Text.Json;
using UnityCli.Protocol;

namespace UnityCli.Cli.Tests;

public sealed class ProfileClampJsonTests
{
    [Fact]
    public void OmitUnclamped_DropsEmptyWarningAndZeroFrameClamp_OnStartWaitAndStopShapes()
    {
        const string start =
            "{\"captureId\":\"cap1\",\"status\":\"STARTED\",\"requestedFrames\":5,\"warning\":\"\",\"frameClamp\":{\"requested\":0,\"clamped\":0,\"bufferLimit\":0}}";
        const string waited =
            "{\n  \"captureId\": \"cap1\",\n  \"status\": \"Completed\",\n  \"capturedFrames\": 120,\n  \"warning\": \"\",\n  \"frameClamp\": {\n    \"requested\": 0,\n    \"clamped\": 0,\n    \"bufferLimit\": 0\n  },\n  \"summary\": {\n    \"capturedFrames\": 120,\n    \"warning\": \"\",\n    \"frameClamp\": {\"requested\": 0, \"clamped\": 0, \"bufferLimit\": 0},\n    \"hotspots\": [{\"marker\": \"say \\\"warning\\\"\"}]\n  }\n}";
        const string stopped =
            "{\"captureId\":\"cap1\",\"capturedFrames\":297,\"notice\":\"PROFILE_ALREADY_COMPLETED\",\"warning\":null,\"frameClamp\":null,\"summary\":{\"capturedFrames\":297,\"warning\":\"\",\"frameClamp\":{\"requested\":0,\"clamped\":0.0,\"bufferLimit\":0}}}";

        foreach (string payload in new[] { start, waited, stopped })
        {
            string cleaned = ProfileClampJson.OmitUnclamped(payload);
            using JsonDocument parsed = JsonDocument.Parse(cleaned);
            Assert.False(ProfileClampJson.HasProperty(cleaned, "warning"), cleaned);
            Assert.False(ProfileClampJson.HasProperty(cleaned, "frameClamp"), cleaned);
            Assert.Contains("cap1", cleaned);
            _ = parsed;
        }

        string pretty = ProfileClampJson.OmitUnclamped(waited);
        Assert.Contains("\"capturedFrames\": 120", pretty);
        Assert.Contains("say \\\"warning\\\"", pretty);
        Assert.DoesNotContain("\"warning\":", pretty);
        Assert.DoesNotContain("\"frameClamp\":", pretty);
    }

    [Fact]
    public void OmitUnclamped_KeepsARealClampBesideTheActualFrameCount()
    {
        const string payload =
            "{\"capturedFrames\":254,\"requestedFrames\":300,\"warning\":\"Requested 100000 frames, clamped to 300 (profiler buffer limit 300).\",\"frameClamp\":{\"requested\":100000,\"clamped\":300,\"bufferLimit\":300}}";

        string cleaned = ProfileClampJson.OmitUnclamped(payload);

        Assert.Equal(payload, cleaned);
        Assert.True(ProfileClampJson.HasProperty(cleaned, "warning"));
        Assert.True(ProfileClampJson.HasProperty(cleaned, "frameClamp"));
        Assert.Contains("\"capturedFrames\":254", cleaned);
    }

    [Fact]
    public void OmitUnclamped_LeavesAStringThatMentionsTheFieldNames()
    {
        const string payload = "{\"marker\":\"frameClamp\",\"note\":\"see warning\"}";

        Assert.Equal(payload, ProfileClampJson.OmitUnclamped(payload));
        Assert.False(ProfileClampJson.HasProperty(payload, "warning"));
        Assert.False(ProfileClampJson.HasProperty(payload, "frameClamp"));
    }

    [Fact]
    public void OmitUnclamped_InvalidJsonIsUnchanged()
    {
        const string payload = "{\"warning\":";

        Assert.Equal(payload, ProfileClampJson.OmitUnclamped(payload));
    }

    [Fact]
    public void Normalize_ClearsJsonUtilityDefaultsAndKeepsARealClamp()
    {
        var summary = new ProfileSummaryPayload
        {
            capturedFrames = 1998,
            requestedFrames = 2000,
            warning = "",
            frameClamp = new ProfileFrameClamp(),
        };

        ProfileClampJson.Normalize(summary);

        Assert.Null(summary.warning);
        Assert.Null(summary.frameClamp);
        Assert.Equal(1998, summary.capturedFrames);

        ProfileFrameClamp clamp = ProfileFrameClamp.TryCreate(100000, 300)!;
        summary.warning = clamp.WarningText();
        summary.frameClamp = clamp;
        ProfileClampJson.Normalize(summary);
        Assert.Equal(clamp.WarningText(), summary.warning);
        Assert.Equal(300, summary.frameClamp!.clamped);
    }
}
