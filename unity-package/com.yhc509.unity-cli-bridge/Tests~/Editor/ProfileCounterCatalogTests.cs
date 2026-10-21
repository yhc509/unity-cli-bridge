#nullable enable
using NUnit.Framework;
using Unity.Profiling;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor.Tests
{
    public sealed class ProfileCounterCatalogTests
    {
        [Test]
        public void ExtendedCounters_StartWithoutThrowing()
        {
            StartAndDispose(ProfilerCategory.Memory, ProfileCounterCatalog.MemoryReport);
            StartAndDispose(ProfilerCategory.Render, ProfileCounterCatalog.RenderStats);
        }

        private static void StartAndDispose(ProfilerCategory category, ProfileCounterName[] counters)
        {
            for (int index = 0; index < counters.Length; index++)
            {
                ProfilerRecorder recorder = ProfilerRecorder.StartNew(category, counters[index].Name, 1);
                recorder.Dispose();
            }
        }
    }
}
