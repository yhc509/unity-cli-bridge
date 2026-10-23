#nullable enable
using System;
using UnityCli.Protocol;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class RoslynHostLauncherOptions
    {
        /// <summary><c>EditorApplication.applicationContentsPath</c>.</summary>
        public string ContentsPath { get; set; } = string.Empty;

        /// <summary>Package folder that holds <c>RoslynHost~/</c> and <c>Runtime/Protocol/</c>.</summary>
        public string PackageRoot { get; set; } = string.Empty;

        /// <summary><c>Library/com.yhc509.unity-cli-bridge/roslyn-host</c>; build outputs go to <c>&lt;key&gt;/</c> under it.</summary>
        public string CacheRoot { get; set; } = string.Empty;

        public string UnityVersion { get; set; } = string.Empty;

        public string ProjectHash { get; set; } = string.Empty;

        public int EditorPid { get; set; }

        /// <summary>Passed as <c>--parent-start-ms</c> when set (Windows only, where pids are reused quickly).</summary>
        public long? EditorStartUnixMs { get; set; }

        public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(15);
    }

    /// <summary>
    /// Production <see cref="IRoslynHostLauncher"/>: locates the bundled toolchain (once per domain),
    /// builds the host into the per-key cache folder when missing (<see cref="RoslynHostBuilder"/>),
    /// and starts it on the bundled muxer. Runs on the supervisor's background thread only.
    /// </summary>
    internal sealed class RoslynHostLauncher : IRoslynHostLauncher
    {
        private readonly RoslynHostLauncherOptions _options;
        private readonly object _gate = new object();
        private BundledToolchainReport? _toolchain;
        private RoslynHostBuildResult? _build;
        private RoslynHostImageDisposition? _disposition;

        public RoslynHostLauncher(RoslynHostLauncherOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public RoslynHostImageDisposition ImageDisposition
        {
            get
            {
                try
                {
                    return ReadDisposition();
                }
                catch (Exception)
                {
                    return RoslynHostImageDisposition.Unavailable;
                }
            }
        }

        public RoslynHostLaunch Launch(bool prewarm, Action<IRoslynHostInstance> onSpawned)
        {
            BundledToolchainReport toolchain = Toolchain();
            BundledToolComponent? runtime = toolchain.Runtime;
            BundledToolComponent? roslyn = toolchain.Roslyn;
            if (runtime == null || !runtime.Found || runtime.Path == null || runtime.Directory == null)
            {
                throw new RoslynHostException(
                    RoslynHostFailureCodes.ToolchainMissing,
                    "bundled .NET runtime not found: " + (runtime?.Error ?? toolchain.Error ?? "unknown"));
            }

            if (roslyn == null || !roslyn.Found || roslyn.Directory == null)
            {
                throw new RoslynHostException(
                    RoslynHostFailureCodes.ToolchainMissing,
                    "bundled Roslyn not found: " + (roslyn?.Error ?? toolchain.Error ?? "unknown"));
            }

            RoslynHostBuildResult? build;
            lock (_gate)
            {
                build = _build;
            }

            if (build == null || !build.Success || build.OutputDirectory == null || !RoslynHostBuilder.IsComplete(build.OutputDirectory))
            {
                build = RoslynHostBuilder.EnsureBuilt(BuildRequest(toolchain));
                lock (_gate)
                {
                    _build = build;
                    if (build.Success && build.OutputDirectory != null && RoslynHostBuilder.IsComplete(build.OutputDirectory))
                    {
                        _disposition = RoslynHostImageDisposition.Ready;
                    }
                }
            }

            if (!build.Success || build.HostAssemblyPath == null)
            {
                string output = build.CompilerOutput ?? string.Empty;
                if (output.Length > 600)
                {
                    output = output.Substring(0, 600) + "...";
                }

                throw new RoslynHostException(
                    RoslynHostFailureCodes.BuildFailed,
                    (build.Error ?? "host build failed") + (output.Length > 0 ? " " + output.Trim() : string.Empty));
            }

            var spec = new RoslynHostLaunchSpec(
                runtime.Path,
                build.HostAssemblyPath,
                runtime.Directory,
                new RoslynHostArguments(_options.ProjectHash, roslyn.Directory, _options.EditorPid, _options.EditorStartUnixMs, prewarm),
                _options.HelloTimeout);
            RoslynHostProcess instance = RoslynHostProcess.Start(spec, spawned => onSpawned(spawned));
            return new RoslynHostLaunch(instance, build.CacheKey ?? string.Empty, build.CacheHit, build.BuildMs);
        }

        private RoslynHostImageDisposition ReadDisposition()
        {
            lock (_gate)
            {
                if (_disposition == RoslynHostImageDisposition.Ready)
                {
                    return RoslynHostImageDisposition.Ready;
                }

                if (_build != null && _build.Success && _build.OutputDirectory != null && RoslynHostBuilder.IsComplete(_build.OutputDirectory))
                {
                    _disposition = RoslynHostImageDisposition.Ready;
                    return RoslynHostImageDisposition.Ready;
                }

                if (_disposition == RoslynHostImageDisposition.NeedsBuild || _disposition == RoslynHostImageDisposition.Unavailable)
                {
                    return _disposition.Value;
                }
            }

            BundledToolchainReport toolchain = Toolchain();
            bool ready = RoslynHostBuilder.IsImageReady(BuildRequest(toolchain), out bool keyed);
            RoslynHostImageDisposition probed = !keyed
                ? RoslynHostImageDisposition.Unavailable
                : (ready ? RoslynHostImageDisposition.Ready : RoslynHostImageDisposition.NeedsBuild);
            lock (_gate)
            {
                if (_disposition == RoslynHostImageDisposition.Ready)
                {
                    return RoslynHostImageDisposition.Ready;
                }

                _disposition = probed;
                return probed;
            }
        }

        private BundledToolchainReport Toolchain()
        {
            lock (_gate)
            {
                if (_toolchain != null)
                {
                    return _toolchain;
                }
            }

            BundledToolchainReport located = BundledToolchainLocator.Locate(_options.ContentsPath);
            lock (_gate)
            {
                return _toolchain ?? (_toolchain = located);
            }
        }

        private RoslynHostBuildRequest BuildRequest(BundledToolchainReport toolchain)
        {
            return new RoslynHostBuildRequest(
                toolchain,
                RoslynHostBuilder.GetHostSourcePaths(_options.PackageRoot),
                _options.CacheRoot,
                _options.UnityVersion);
        }
    }
}
