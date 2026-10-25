#nullable enable
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    /// <summary>Allocates every frame so the capture has a script marker with GC below it.</summary>
    public sealed class ProfileCaptureProbe : MonoBehaviour
    {
        private readonly List<string> _keep = new List<string>();

        private void Update()
        {
            _keep.Add(new string('x', 128));
            if (_keep.Count > 50)
            {
                _keep.Clear();
            }
        }
    }

    /// <summary>
    /// The capture lifecycle and `profile stats --marker` against the real profiler, in Play Mode entered with
    /// domain reload off so the test keeps running in the same domain.
    /// </summary>
    [Category("ProfileLive")]
    public sealed class ProfileCaptureLiveTests
    {
        private const int MaxWaitFrames = 3000;
        private bool _previousOptionsEnabled;
        private EnterPlayModeOptions _previousOptions;
        private GameObject? _probe;

        [UnitySetUp]
        public IEnumerator EnterPlayModeWithoutReload()
        {
            _previousOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            _previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
            yield return new EnterPlayMode(expectDomainReload: false);
            _probe = new GameObject("ProfileCaptureProbe");
            _probe.AddComponent<ProfileCaptureProbe>();
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator ExitPlayModeAndRestore()
        {
            if (_probe != null)
            {
                Object.Destroy(_probe);
            }

            yield return new ExitPlayMode();
            EditorSettings.enterPlayModeOptions = _previousOptions;
            EditorSettings.enterPlayModeOptionsEnabled = _previousOptionsEnabled;
        }

        [UnityTest]
        public IEnumerator Stop_RightAfterStart_IsProfileEmptyAndKeepsTheCaptureId()
        {
            var handler = new ProfileCommandHandler();
            string captureId = Start(handler, "{}");

            CommandFailureException failure = Assert.Throws<CommandFailureException>(
                () => handler.Handle(ProtocolConstants.CommandProfileCaptureStop, "{}"))!;

            Assert.That(failure.ErrorCode, Is.EqualTo(ProtocolConstants.ErrorProfileEmpty));
            Assert.That(failure.Message, Does.Contain(captureId));
            Assert.That(failure.Message, Does.Contain("--frames <n> --wait"));
            Assert.That(Status(handler, captureId).status, Is.EqualTo("Empty"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator Stop_AfterTheCaptureFinishedOnItsOwn_ReturnsItsSummary()
        {
            var handler = new ProfileCommandHandler();
            string captureId = Start(handler, "{\"frames\":5}");
            for (int frame = 0; frame < MaxWaitFrames && Status(handler, captureId).status != "Completed"; frame++)
            {
                yield return null;
            }

            Assert.That(Status(handler, captureId).status, Is.EqualTo("Completed"));
            ProfileCaptureResultPayload result = JsonUtility.FromJson<ProfileCaptureResultPayload>(
                handler.Handle(ProtocolConstants.CommandProfileCaptureStop, "{}"));

            Assert.That(result.captureId, Is.EqualTo(captureId));
            Assert.That(result.status, Is.EqualTo("Completed"));
            Assert.That(result.notice, Is.EqualTo(ProtocolConstants.NoticeProfileAlreadyCompleted));
            Assert.That(result.capturedFrames, Is.EqualTo(5));
            Assert.That(result.summary.captureId, Is.EqualTo(captureId));
            Assert.That(result.summary.capturedFrames, Is.EqualTo(5));
        }

        [UnityTest]
        public IEnumerator Capture_AttributesGcToTheScriptAndLeavesContainersOutOfGcTop()
        {
            var handler = new ProfileCommandHandler();
            string captureId = Start(handler, "{\"frames\":10}");
            for (int frame = 0; frame < MaxWaitFrames && Status(handler, captureId).status != "Completed"; frame++)
            {
                yield return null;
            }

            string sidecarPath = Path.Combine(
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                ProtocolConstants.ProfilesDirectoryRelative,
                captureId + ".json");
            ProfileSidecarFile sidecar = JsonUtility.FromJson<ProfileSidecarFile>(File.ReadAllText(sidecarPath));

            Assert.That(sidecar.schemaVersion, Is.EqualTo(ProtocolConstants.ProfileSidecarSchemaVersion));
            foreach (ProfileGcEntry entry in sidecar.summary.gcTop)
            {
                Assert.That(ProfileGcAttribution.IsExcludedFromGcTop(entry.marker), Is.False, entry.marker);
            }

            ProfileGcScriptEntry? probe = null;
            foreach (ProfileGcScriptEntry entry in sidecar.gcByScript)
            {
                if (entry.marker.Contains("ProfileCaptureProbe.Update"))
                {
                    probe = entry;
                }
            }

            Assert.That(probe, Is.Not.Null, "gcByScript: " + string.Join(", ", System.Array.ConvertAll(sidecar.gcByScript, e => e.marker)));
            Assert.That(probe!.bytesTotal, Is.GreaterThan(0));
        }

        [UnityTest]
        public IEnumerator StatsMarker_ShortName_MeasuresTimeAndGcPerFrame()
        {
            var handler = new ProfileCommandHandler();
            var completion = new TaskCompletionSource<ResponseEnvelope>("profile-live-stats");
            handler.StartDeferred(
                ProtocolConstants.CommandProfileStats,
                "{\"frames\":10,\"markers\":[\"ProfileCaptureProbe.Update\"]}",
                completion,
                "test");
            for (int frame = 0; frame < MaxWaitFrames && !completion.Task.IsCompleted; frame++)
            {
                yield return null;
            }

            Assert.That(completion.Task.IsCompleted, Is.True);
            ResponseEnvelope response = completion.Task.Result;
            Assert.That(response.status, Is.EqualTo(ProtocolConstants.StatusSuccess), response.error?.message);
            ProfileStatsPayload payload = JsonUtility.FromJson<ProfileStatsPayload>(response.data);
            Assert.That(payload.markerStats.Length, Is.EqualTo(1));
            ProfileMarkerStat stat = payload.markerStats[0];
            string describe = "path=" + stat.path + " frames=" + stat.frames + " withCalls=" + stat.framesWithCalls + " note=" + stat.note;
            Assert.That(stat.resolvedMarker, Does.Contain("ProfileCaptureProbe.Update"));
            Assert.That(stat.path, Is.EqualTo(ProfileStatMath.MarkerPathRecorder).Or.EqualTo(ProfileStatMath.MarkerPathCapture));
            Assert.That(stat.frames, Is.EqualTo(10), describe);
            Assert.That(stat.framesWithCalls, Is.GreaterThan(0), describe);
            Assert.That(stat.median, Is.GreaterThan(0), describe);
            Assert.That(stat.gcBytesPerFrame, Is.GreaterThan(0), describe);
        }

        [UnityTest]
        public IEnumerator StatsMarker_UnknownName_ListsSimilarNames()
        {
            var handler = new ProfileCommandHandler();
            var completion = new TaskCompletionSource<ResponseEnvelope>("profile-live-stats-miss");
            handler.StartDeferred(
                ProtocolConstants.CommandProfileStats,
                "{\"frames\":5,\"markers\":[\"ProfileCaptureProbe.Updat\"]}",
                completion,
                "test");
            for (int frame = 0; frame < MaxWaitFrames && !completion.Task.IsCompleted; frame++)
            {
                yield return null;
            }

            ResponseEnvelope response = completion.Task.Result;
            Assert.That(response.error!.code, Is.EqualTo(ProtocolConstants.ErrorProfileNotFound));
            Assert.That(response.error.message, Does.Contain("Similar names:"));
            Assert.That(response.error.message, Does.Contain("ProfileCaptureProbe.Update"));
        }

        private static string Start(ProfileCommandHandler handler, string argumentsJson)
        {
            ProfileCaptureStartedPayload started = JsonUtility.FromJson<ProfileCaptureStartedPayload>(
                handler.Handle(ProtocolConstants.CommandProfileCaptureStart, argumentsJson));
            Assert.That(started.captureId, Is.Not.Empty);
            return started.captureId;
        }

        private static ProfileSummaryPayload Status(ProfileCommandHandler handler, string captureId)
        {
            return JsonUtility.FromJson<ProfileSummaryPayload>(
                handler.Handle(ProtocolConstants.CommandProfileStatus, "{\"captureId\":\"" + captureId + "\"}"));
        }
    }
}
