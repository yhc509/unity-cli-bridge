#nullable enable
using System;
using System.Collections.Generic;

namespace UnityCli.Protocol
{
    /// <summary>
    /// GC ranking rules shared by the Editor capture walk and <c>profile analyze --gc</c>.
    /// The profiler's GC column is inclusive, so a player-loop container reports every allocation
    /// below it and would push the scripts that allocate out of the top of the list.
    /// </summary>
    public static class ProfileGcAttribution
    {
        public const string GcAllocMarker = "GC.Alloc";

        /// <summary>
        /// Script-dispatch containers whose GC is the sum of their children, and the <c>GC.Alloc</c> leaf itself.
        /// They are left out of <c>gcTop</c> and are never a script ancestor. Engine PlayerLoop phases and
        /// end-of-frame markers are excluded by <see cref="IsEnginePlayerLoopMarker"/> as well.
        /// </summary>
        public static readonly string[] GcTopExcludedMarkers =
        {
            "PlayerLoop",
            "UpdateScene",
            "BehaviourUpdate",
            "Update.ScriptRunBehaviourUpdate",
            "LateBehaviourUpdate",
            "PreLateUpdate.ScriptRunBehaviourLateUpdate",
            "FixedBehaviourUpdate",
            "FixedUpdate.ScriptRunBehaviourFixedUpdate",
            GcAllocMarker,
        };

        /// <summary>PlayerLoop phase roots. A marker named <c>Phase</c> or <c>Phase.Subsystem</c> is engine code.</summary>
        public static readonly string[] PlayerLoopPhaseNames =
        {
            "TimeUpdate",
            "Initialization",
            "EarlyUpdate",
            "FixedUpdate",
            "PreUpdate",
            "Update",
            "PreLateUpdate",
            "PostLateUpdate",
        };

        /// <summary>End-of-frame samples. Matched exactly and as a <c>.Name</c> suffix (for example <c>PlayerEndOfFrame</c>).</summary>
        public static readonly string[] EndOfFrameMarkerNames =
        {
            "PlayerEndOfFrame",
            "EndOfFrame",
            "WaitForEndOfFrame",
            "TriggerEndOfFrameCallbacks",
        };

        public static bool IsExcludedFromGcTop(string? marker)
        {
            if (string.IsNullOrEmpty(marker))
            {
                return true;
            }

            for (int index = 0; index < GcTopExcludedMarkers.Length; index++)
            {
                if (string.Equals(GcTopExcludedMarkers[index], marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return IsEnginePlayerLoopMarker(marker);
        }

        /// <summary>
        /// Engine PlayerLoop phase and end-of-frame markers, such as <c>PlayerEndOfFrame</c> and
        /// <c>PostLateUpdate.FinishFrameRendering</c>. A script decoration (<c>.dll!</c>, <c>[Invoke]</c>,
        /// <c>[Coroutine:</c>) keeps the marker, so <c>ThreatField.Update() [Invoke]</c> stays a script.
        /// </summary>
        public static bool IsEnginePlayerLoopMarker(string? marker)
        {
            if (string.IsNullOrEmpty(marker))
            {
                return false;
            }

            if (marker!.IndexOf(".dll!", StringComparison.Ordinal) >= 0
                || marker.EndsWith(" [Invoke]", StringComparison.Ordinal)
                || marker.IndexOf(" [Coroutine:", StringComparison.Ordinal) >= 0)
            {
                return false;
            }

            if (EqualsAny(marker, EndOfFrameMarkerNames) || EqualsAny(marker, PlayerLoopPhaseNames))
            {
                return true;
            }

            for (int index = 0; index < PlayerLoopPhaseNames.Length; index++)
            {
                string phase = PlayerLoopPhaseNames[index];
                if (marker.Length > phase.Length
                    && marker[phase.Length] == '.'
                    && marker.StartsWith(phase, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            for (int index = 0; index < EndOfFrameMarkerNames.Length; index++)
            {
                if (marker.EndsWith("." + EndOfFrameMarkerNames[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// A script or user marker: the profiler flags it as script code, or its name carries the script
        /// decoration (<c>Assembly.dll!</c> prefix or <c>[Invoke]</c> suffix). Containers and engine
        /// PlayerLoop markers never count, even when the profiler sets a script flag on them.
        /// </summary>
        public static bool IsScriptMarker(string? marker, bool scriptFlag)
        {
            if (IsExcludedFromGcTop(marker))
            {
                return false;
            }

            return scriptFlag
                || marker!.IndexOf(".dll!", StringComparison.Ordinal) >= 0
                || marker.EndsWith(" [Invoke]", StringComparison.Ordinal);
        }

        /// <summary>Markers with GC bytes, containers excluded, largest first (ties by name).</summary>
        public static ProfileGcEntry[] BuildGcTop(ProfileMarkerEntry[]? markers, int limit, out bool truncated)
        {
            var entries = new List<ProfileGcEntry>();
            if (markers != null)
            {
                foreach (ProfileMarkerEntry marker in markers)
                {
                    if (marker == null || marker.gcBytes <= 0 || IsExcludedFromGcTop(marker.m))
                    {
                        continue;
                    }

                    entries.Add(new ProfileGcEntry
                    {
                        marker = marker.m,
                        bytesTotal = marker.gcBytes,
                        framesWithAlloc = marker.frames,
                    });
                }
            }

            entries.Sort((left, right) =>
            {
                int byBytes = right.bytesTotal.CompareTo(left.bytesTotal);
                return byBytes != 0 ? byBytes : string.CompareOrdinal(left.marker, right.marker);
            });

            truncated = limit >= 0 && entries.Count > limit;
            if (truncated)
            {
                entries.RemoveRange(limit, entries.Count - limit);
            }

            return entries.ToArray();
        }

        /// <summary>The first <paramref name="limit"/> entries of an already sorted list.</summary>
        public static ProfileGcScriptEntry[] Take(ProfileGcScriptEntry[]? entries, int limit, out bool truncated)
        {
            ProfileGcScriptEntry[] source = entries ?? Array.Empty<ProfileGcScriptEntry>();
            truncated = limit >= 0 && source.Length > limit;
            if (!truncated)
            {
                return source;
            }

            var result = new ProfileGcScriptEntry[limit];
            Array.Copy(source, result, limit);
            return result;
        }

        /// <summary>
        /// Drops engine containers and PlayerLoop markers from a stored <c>gcByScript</c> list and adds their
        /// bytes to <paramref name="unattributedBytes"/>. A capture recorded before that exclusion still reads cleanly.
        /// </summary>
        public static ProfileGcScriptEntry[] OmitEngineMarkers(ProfileGcScriptEntry[]? entries, ref long unattributedBytes)
        {
            ProfileGcScriptEntry[] source = entries ?? Array.Empty<ProfileGcScriptEntry>();
            int omitted = 0;
            for (int index = 0; index < source.Length; index++)
            {
                ProfileGcScriptEntry? entry = source[index];
                if (entry != null && IsExcludedFromGcTop(entry.marker))
                {
                    omitted++;
                }
            }

            if (omitted == 0)
            {
                return source;
            }

            var kept = new List<ProfileGcScriptEntry>(source.Length - omitted);
            for (int index = 0; index < source.Length; index++)
            {
                ProfileGcScriptEntry? entry = source[index];
                if (entry == null)
                {
                    continue;
                }

                if (IsExcludedFromGcTop(entry.marker))
                {
                    unattributedBytes += entry.bytesTotal;
                    continue;
                }

                kept.Add(entry);
            }

            return kept.ToArray();
        }

        private static bool EqualsAny(string marker, string[] names)
        {
            for (int index = 0; index < names.Length; index++)
            {
                if (string.Equals(names[index], marker, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Read access to one frame's profiler hierarchy. The Editor wraps HierarchyFrameDataView; tests use a fake tree.</summary>
    public interface IProfileHierarchy
    {
        int RootId { get; }

        void GetChildren(int itemId, List<int> children);

        string GetName(int itemId);

        /// <summary>True when the profiler flags this item's marker as script code.</summary>
        bool HasScriptFlag(int itemId);
    }

    /// <summary>Called once per hierarchy item with the nearest script ancestor's marker name (null when there is none).</summary>
    public delegate void ProfileHierarchyVisitor(int itemId, string name, string? scriptAncestor);

    public static class ProfileHierarchyWalker
    {
        /// <summary>
        /// Visits every item below the root, depth first. An item's script ancestor is its parent when the parent
        /// is a script marker, otherwise the parent's own script ancestor.
        /// </summary>
        public static void Walk(IProfileHierarchy hierarchy, ProfileHierarchyVisitor visit)
        {
            var pending = new Stack<KeyValuePair<int, string?>>();
            var children = new List<int>(64);
            hierarchy.GetChildren(hierarchy.RootId, children);
            for (int index = 0; index < children.Count; index++)
            {
                pending.Push(new KeyValuePair<int, string?>(children[index], null));
            }

            while (pending.Count > 0)
            {
                KeyValuePair<int, string?> item = pending.Pop();
                string name = hierarchy.GetName(item.Key) ?? string.Empty;
                visit(item.Key, name, item.Value);

                string? childAncestor = ProfileGcAttribution.IsScriptMarker(name, hierarchy.HasScriptFlag(item.Key))
                    ? name
                    : item.Value;
                children.Clear();
                hierarchy.GetChildren(item.Key, children);
                for (int index = 0; index < children.Count; index++)
                {
                    pending.Push(new KeyValuePair<int, string?>(children[index], childAncestor));
                }
            }
        }
    }

    /// <summary>Sums <c>GC.Alloc</c> bytes per nearest script ancestor across a capture.</summary>
    public sealed class ProfileGcByScriptAccumulator
    {
        private sealed class Aggregate
        {
            public long Bytes;
            public long Allocations;
            public int Frames;
            public int LastFrame = -1;
        }

        private readonly Dictionary<string, Aggregate> _byScript = new Dictionary<string, Aggregate>(StringComparer.Ordinal);
        private int _frame = -1;

        public long UnattributedBytes { get; private set; }

        public int FrameCount { get; private set; }

        public void BeginFrame()
        {
            _frame++;
            FrameCount++;
        }

        /// <summary>Adds one <c>GC.Alloc</c> sample. <paramref name="scriptAncestor"/> null means no script marker above it.</summary>
        public void Record(string? scriptAncestor, long bytes, long allocations)
        {
            if (bytes <= 0)
            {
                return;
            }

            if (string.IsNullOrEmpty(scriptAncestor))
            {
                UnattributedBytes += bytes;
                return;
            }

            if (!_byScript.TryGetValue(scriptAncestor!, out Aggregate? aggregate) || aggregate == null)
            {
                aggregate = new Aggregate();
                _byScript[scriptAncestor!] = aggregate;
            }

            aggregate.Bytes += bytes;
            aggregate.Allocations += allocations;
            if (aggregate.LastFrame != _frame)
            {
                aggregate.LastFrame = _frame;
                aggregate.Frames++;
            }
        }

        /// <summary>Every script, largest first (ties by name). bytesPerFrame divides by <paramref name="capturedFrames"/>.</summary>
        public ProfileGcScriptEntry[] Build(int capturedFrames)
        {
            var entries = new List<ProfileGcScriptEntry>(_byScript.Count);
            foreach (KeyValuePair<string, Aggregate> pair in _byScript)
            {
                entries.Add(new ProfileGcScriptEntry
                {
                    marker = pair.Key,
                    bytesTotal = pair.Value.Bytes,
                    bytesPerFrame = capturedFrames > 0 ? (double)pair.Value.Bytes / capturedFrames : 0,
                    allocations = pair.Value.Allocations,
                    framesWithAlloc = pair.Value.Frames,
                });
            }

            entries.Sort((left, right) =>
            {
                int byBytes = right.bytesTotal.CompareTo(left.bytesTotal);
                return byBytes != 0 ? byBytes : string.CompareOrdinal(left.marker, right.marker);
            });
            return entries.ToArray();
        }
    }
}
