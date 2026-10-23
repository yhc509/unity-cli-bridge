#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace UnityCli.Protocol
{
    /// <summary>
    /// Message types on the Roslyn host pipe. Host → editor: <c>hello</c>, <c>refsSet</c>,
    /// <c>result</c>, <c>pong</c>, <c>error</c>. Editor → host: <c>setRefs</c>, <c>compile</c>,
    /// <c>ping</c>, <c>quit</c>. Requests are answered in order, one at a time.
    /// </summary>
    public static class RoslynHostMessageTypes
    {
        public const string Hello = "hello";
        public const string SetRefs = "setRefs";
        public const string RefsSet = "refsSet";
        public const string Compile = "compile";
        public const string Result = "result";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Quit = "quit";
        public const string Error = "error";
    }

    /// <summary>First message the host writes, once it is ready for requests.</summary>
    public sealed class RoslynHostHello
    {
        /// <summary>Version of the host pipe protocol. Unrelated to the CLI wire protocol.</summary>
        public const int CurrentProtocol = 1;

        public int Protocol { get; set; } = CurrentProtocol;

        /// <summary><c>RuntimeEnvironment.GetRuntimeDirectory()</c> inside the host.</summary>
        public string RuntimeDirectory { get; set; } = string.Empty;

        /// <summary><c>typeof(object).Assembly.Location</c> inside the host.</summary>
        public string CoreLibraryPath { get; set; } = string.Empty;

        public string RuntimeVersion { get; set; } = string.Empty;

        public string RoslynVersion { get; set; } = string.Empty;

        public int ProcessId { get; set; }

        /// <summary>Milliseconds spent on the framework prewarm compile before hello; 0 when the host ran without <c>--prewarm</c>.</summary>
        public double PrewarmMs { get; set; }

        /// <summary>The project hash from <c>--ucb-roslyn-host</c>.</summary>
        public string Marker { get; set; } = string.Empty;

        /// <summary>
        /// True when both reported runtime paths sit under <paramref name="bundledRuntimeDirectory"/>
        /// (the located <c>NetCoreRuntime</c> folder), i.e. the host did not end up on a system .NET.
        /// </summary>
        public bool IsRuntimeUnder(string bundledRuntimeDirectory)
        {
            return IsPathUnder(RuntimeDirectory, bundledRuntimeDirectory)
                && IsPathUnder(CoreLibraryPath, bundledRuntimeDirectory);
        }

        public RoslynHostMessage ToMessage()
        {
            var header = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["protocol"] = Protocol,
                ["runtime"] = RuntimeDirectory,
                ["coreLib"] = CoreLibraryPath,
                ["runtimeVersion"] = RuntimeVersion,
                ["roslyn"] = RoslynVersion,
                ["pid"] = ProcessId,
                ["prewarmMs"] = PrewarmMs,
                ["marker"] = Marker,
            };
            return new RoslynHostMessage(RoslynHostMessageTypes.Hello, header);
        }

        public static RoslynHostHello FromMessage(RoslynHostMessage message)
        {
            RequireType(message, RoslynHostMessageTypes.Hello);
            Dictionary<string, object?> h = message.Header;
            return new RoslynHostHello
            {
                Protocol = (int)RoslynHostJson.GetInt64(h, "protocol", 0, int.MinValue, int.MaxValue),
                RuntimeDirectory = RoslynHostJson.GetString(h, "runtime") ?? string.Empty,
                CoreLibraryPath = RoslynHostJson.GetString(h, "coreLib") ?? string.Empty,
                RuntimeVersion = RoslynHostJson.GetString(h, "runtimeVersion") ?? string.Empty,
                RoslynVersion = RoslynHostJson.GetString(h, "roslyn") ?? string.Empty,
                ProcessId = (int)RoslynHostJson.GetInt64(h, "pid", 0, 0, int.MaxValue),
                PrewarmMs = RoslynHostJson.GetDouble(h, "prewarmMs"),
                Marker = RoslynHostJson.GetString(h, "marker") ?? string.Empty,
            };
        }

        internal static void RequireType(RoslynHostMessage message, string type)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }

            if (!string.Equals(message.Type, type, StringComparison.Ordinal))
            {
                throw new ArgumentException("Expected a '" + type + "' message, got '" + message.Type + "'.", nameof(message));
            }
        }

        private static bool IsPathUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }

            string fullPath;
            string fullRoot;
            try
            {
                fullPath = Path.GetFullPath(path);
                fullRoot = Path.GetFullPath(root).TrimEnd('/', '\\');
            }
            catch (Exception)
            {
                return false;
            }

            // Unity installs live on case-insensitive volumes on macOS and Windows.
            return fullPath.Length > fullRoot.Length
                && fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                && (fullPath[fullRoot.Length] == '/' || fullPath[fullRoot.Length] == '\\');
        }
    }

    /// <summary>Editor → host: replace the reference set. Unchanged files (same path, mtime, size) are not re-read.</summary>
    public sealed class RoslynHostSetRefs
    {
        public string RefsHash { get; set; } = string.Empty;

        public List<string> Paths { get; set; } = new List<string>();

        public RoslynHostMessage ToMessage()
        {
            var header = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["refsHash"] = RefsHash,
                ["paths"] = Paths,
            };
            return new RoslynHostMessage(RoslynHostMessageTypes.SetRefs, header);
        }

        public static RoslynHostSetRefs FromMessage(RoslynHostMessage message)
        {
            RoslynHostHello.RequireType(message, RoslynHostMessageTypes.SetRefs);
            return new RoslynHostSetRefs
            {
                RefsHash = RoslynHostJson.GetString(message.Header, "refsHash") ?? string.Empty,
                Paths = RoslynHostJson.GetStringList(message.Header, "paths"),
            };
        }
    }

    /// <summary>Host → editor acknowledgement of <c>setRefs</c>.</summary>
    public sealed class RoslynHostRefsSet
    {
        public string RefsHash { get; set; } = string.Empty;

        /// <summary>References now in use.</summary>
        public int Count { get; set; }

        /// <summary>Files read from disk for this request (new, or mtime/size changed).</summary>
        public int Loaded { get; set; }

        /// <summary>Files served from the cache.</summary>
        public int Reused { get; set; }

        /// <summary>Paths that do not exist or could not be read as metadata (<c>path: reason</c> for the latter).</summary>
        public List<string> Skipped { get; set; } = new List<string>();

        public double LoadMs { get; set; }

        /// <summary>Milliseconds of the reference prewarm compile after the first <c>setRefs</c>; 0 when not run.</summary>
        public double PrewarmMs { get; set; }

        public RoslynHostMessage ToMessage()
        {
            var header = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["refsHash"] = RefsHash,
                ["count"] = Count,
                ["loaded"] = Loaded,
                ["reused"] = Reused,
                ["skipped"] = Skipped,
                ["loadMs"] = LoadMs,
                ["prewarmMs"] = PrewarmMs,
            };
            return new RoslynHostMessage(RoslynHostMessageTypes.RefsSet, header);
        }

        public static RoslynHostRefsSet FromMessage(RoslynHostMessage message)
        {
            RoslynHostHello.RequireType(message, RoslynHostMessageTypes.RefsSet);
            Dictionary<string, object?> h = message.Header;
            return new RoslynHostRefsSet
            {
                RefsHash = RoslynHostJson.GetString(h, "refsHash") ?? string.Empty,
                Count = (int)RoslynHostJson.GetInt64(h, "count", 0, 0, int.MaxValue),
                Loaded = (int)RoslynHostJson.GetInt64(h, "loaded", 0, 0, int.MaxValue),
                Reused = (int)RoslynHostJson.GetInt64(h, "reused", 0, 0, int.MaxValue),
                Skipped = RoslynHostJson.GetStringList(h, "skipped"),
                LoadMs = RoslynHostJson.GetDouble(h, "loadMs"),
                PrewarmMs = RoslynHostJson.GetDouble(h, "prewarmMs"),
            };
        }
    }

    /// <summary>Editor → host: compile one source text against the current reference set.</summary>
    public sealed class RoslynHostCompileRequest
    {
        public const string DefaultLanguageVersion = "9";

        public string Id { get; set; } = string.Empty;

        /// <summary>Must match the host's current set; empty skips the check.</summary>
        public string RefsHash { get; set; } = string.Empty;

        public string Source { get; set; } = string.Empty;

        public List<string> Defines { get; set; } = new List<string>();

        /// <summary>Roslyn language version text (<c>9</c>, <c>9.0</c>, <c>latest</c>); empty means C# 9.</summary>
        public string LanguageVersion { get; set; } = DefaultLanguageVersion;

        public bool AllowUnsafe { get; set; }

        /// <summary>Warning ids to suppress, with or without the <c>CS</c> prefix.</summary>
        public List<string> NoWarn { get; set; } = new List<string>();

        public string AssemblyName { get; set; } = string.Empty;

        /// <summary>Syntax tree path; diagnostics outside a <c>#line</c> region report it as their file.</summary>
        public string FilePath { get; set; } = string.Empty;

        public RoslynHostMessage ToMessage()
        {
            var header = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Id,
                ["refsHash"] = RefsHash,
                ["source"] = Source,
                ["defines"] = Defines,
                ["langVersion"] = LanguageVersion,
                ["allowUnsafe"] = AllowUnsafe,
                ["nowarn"] = NoWarn,
                ["assemblyName"] = AssemblyName,
                ["filePath"] = FilePath,
            };
            return new RoslynHostMessage(RoslynHostMessageTypes.Compile, header);
        }

        public static RoslynHostCompileRequest FromMessage(RoslynHostMessage message)
        {
            RoslynHostHello.RequireType(message, RoslynHostMessageTypes.Compile);
            Dictionary<string, object?> h = message.Header;
            return new RoslynHostCompileRequest
            {
                Id = RoslynHostJson.GetString(h, "id") ?? string.Empty,
                RefsHash = RoslynHostJson.GetString(h, "refsHash") ?? string.Empty,
                Source = RoslynHostJson.GetString(h, "source") ?? string.Empty,
                Defines = RoslynHostJson.GetStringList(h, "defines"),
                LanguageVersion = RoslynHostJson.GetString(h, "langVersion") ?? string.Empty,
                AllowUnsafe = RoslynHostJson.GetBool(h, "allowUnsafe"),
                NoWarn = RoslynHostJson.GetStringList(h, "nowarn"),
                AssemblyName = RoslynHostJson.GetString(h, "assemblyName") ?? string.Empty,
                FilePath = RoslynHostJson.GetString(h, "filePath") ?? string.Empty,
            };
        }
    }

    /// <summary>One compiler diagnostic, with the position already mapped through <c>#line</c>.</summary>
    public sealed class RoslynHostDiagnostic
    {
        public string Id { get; set; } = string.Empty;

        /// <summary><c>error</c> or <c>warning</c>.</summary>
        public string Severity { get; set; } = string.Empty;

        /// <summary>1-based mapped line; 0 when the diagnostic has no source location.</summary>
        public int Line { get; set; }

        /// <summary>1-based mapped column; 0 when the diagnostic has no source location.</summary>
        public int Column { get; set; }

        /// <summary>Mapped file (the <c>#line</c> file name when one applies).</summary>
        public string File { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public bool IsError => string.Equals(Severity, "error", StringComparison.Ordinal);

        internal Dictionary<string, object?> ToJsonObject()
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Id,
                ["severity"] = Severity,
                ["line"] = Line,
                ["col"] = Column,
                ["file"] = File,
                ["message"] = Message,
            };
        }

        internal static RoslynHostDiagnostic FromJsonObject(Dictionary<string, object?> obj)
        {
            return new RoslynHostDiagnostic
            {
                Id = RoslynHostJson.GetString(obj, "id") ?? string.Empty,
                Severity = RoslynHostJson.GetString(obj, "severity") ?? string.Empty,
                Line = (int)RoslynHostJson.GetInt64(obj, "line", 0, 0, int.MaxValue),
                Column = (int)RoslynHostJson.GetInt64(obj, "col", 0, 0, int.MaxValue),
                File = RoslynHostJson.GetString(obj, "file") ?? string.Empty,
                Message = RoslynHostJson.GetString(obj, "message") ?? string.Empty,
            };
        }
    }

    /// <summary>
    /// Host → editor answer to <c>compile</c>. <see cref="Error"/> is set only for an
    /// infrastructure problem (no references, refs hash mismatch, invalid request, compiler crash);
    /// user compile errors come back as <see cref="Diagnostics"/> with <see cref="Ok"/> false.
    /// The emitted assembly travels as the binary payload when <see cref="Ok"/> is true.
    /// </summary>
    public sealed class RoslynHostCompileResult
    {
        public string Id { get; set; } = string.Empty;

        public bool Ok { get; set; }

        public double CompileMs { get; set; }

        public List<RoslynHostDiagnostic> Diagnostics { get; set; } = new List<RoslynHostDiagnostic>();

        public string? Error { get; set; }

        public byte[]? Assembly { get; set; }

        public RoslynHostMessage ToMessage()
        {
            var diagnostics = new List<object?>(Diagnostics.Count);
            foreach (RoslynHostDiagnostic diagnostic in Diagnostics)
            {
                diagnostics.Add(diagnostic.ToJsonObject());
            }

            var header = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Id,
                ["ok"] = Ok,
                ["compileMs"] = CompileMs,
                ["diagnostics"] = diagnostics,
            };
            if (Error != null)
            {
                header["error"] = Error;
            }

            return new RoslynHostMessage(RoslynHostMessageTypes.Result, header, Ok ? Assembly : null);
        }

        public static RoslynHostCompileResult FromMessage(RoslynHostMessage message)
        {
            RoslynHostHello.RequireType(message, RoslynHostMessageTypes.Result);
            Dictionary<string, object?> h = message.Header;
            var result = new RoslynHostCompileResult
            {
                Id = RoslynHostJson.GetString(h, "id") ?? string.Empty,
                Ok = RoslynHostJson.GetBool(h, "ok"),
                CompileMs = RoslynHostJson.GetDouble(h, "compileMs"),
                Error = RoslynHostJson.GetString(h, "error"),
                Assembly = message.Payload,
            };
            if (h.TryGetValue("diagnostics", out object? value) && value is List<object?> items)
            {
                foreach (object? item in items)
                {
                    if (item is Dictionary<string, object?> obj)
                    {
                        result.Diagnostics.Add(RoslynHostDiagnostic.FromJsonObject(obj));
                    }
                }
            }

            return result;
        }
    }

    /// <summary>Host → editor report of a request it could not handle. <see cref="Fatal"/> means the host is exiting.</summary>
    public sealed class RoslynHostError
    {
        public string Message { get; set; } = string.Empty;

        public bool Fatal { get; set; }

        public RoslynHostMessage ToMessage()
        {
            var header = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["message"] = Message,
                ["fatal"] = Fatal,
            };
            return new RoslynHostMessage(RoslynHostMessageTypes.Error, header);
        }

        public static RoslynHostError FromMessage(RoslynHostMessage message)
        {
            RoslynHostHello.RequireType(message, RoslynHostMessageTypes.Error);
            return new RoslynHostError
            {
                Message = RoslynHostJson.GetString(message.Header, "message") ?? string.Empty,
                Fatal = RoslynHostJson.GetBool(message.Header, "fatal"),
            };
        }
    }
}
