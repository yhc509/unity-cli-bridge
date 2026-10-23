#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UnityCliBridge.Bridge.Editor
{
    /// <summary>
    /// Writes the mcs response file that carries the <c>execute</c> compile arguments, so the
    /// compiler command line stays short no matter how many assemblies are loaded (#220: every
    /// reference used to be an inline <c>/r:"path"</c>, and Windows caps a command line at
    /// 32,767 characters). One argument per line, UTF-8 without a BOM.
    /// </summary>
    /// <remarks>
    /// Quoting follows mcs's response-file reader, not a shell: <c>"</c> or <c>'</c> opens a run
    /// that ends at the next identical character, there is no escape character, and a space
    /// outside a run ends the argument. A literal <c>"</c> inside a path therefore closes the
    /// run, is emitted as <c>'"'</c>, and reopens it. Backslashes are literal.
    /// </remarks>
    internal static class ResponseFileWriter
    {
        public const string FileExtension = ".rsp";

        private const string FileNamePrefix = "ucb-execute-";

        private static readonly UTF8Encoding Utf8WithoutBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        /// <summary>
        /// Arguments in the order the compiler used to see them inline: one quoted
        /// <c>/r:</c> per reference (empty entries skipped, as CodeDOM did), then
        /// <paramref name="options"/> verbatim.
        /// </summary>
        public static List<string> BuildCompilerArguments(IReadOnlyList<string> referencePaths, params string[] options)
        {
            var arguments = new List<string>();
            if (referencePaths != null)
            {
                for (int index = 0; index < referencePaths.Count; index++)
                {
                    string referencePath = referencePaths[index];
                    if (string.IsNullOrEmpty(referencePath))
                    {
                        continue;
                    }

                    arguments.Add("/r:" + Quote(referencePath));
                }
            }

            if (options != null)
            {
                for (int index = 0; index < options.Length; index++)
                {
                    if (!string.IsNullOrWhiteSpace(options[index]))
                    {
                        arguments.Add(options[index].Trim());
                    }
                }
            }

            return arguments;
        }

        public static string Quote(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character == '"')
                {
                    builder.Append("\"'\"'\"");
                }
                else
                {
                    builder.Append(character);
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>One argument per line. mcs reads the file line by line, so an argument cannot contain a line break.</summary>
        public static string FormatContent(IReadOnlyList<string> arguments)
        {
            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            var builder = new StringBuilder();
            for (int index = 0; index < arguments.Count; index++)
            {
                string argument = arguments[index] ?? string.Empty;
                if (argument.IndexOf('\n') >= 0 || argument.IndexOf('\r') >= 0)
                {
                    throw new ArgumentException("A response-file argument cannot contain a line break.", nameof(arguments));
                }

                builder.Append(argument);
                builder.Append('\n');
            }

            return builder.ToString();
        }

        public static void Write(string path, IReadOnlyList<string> arguments)
        {
            File.WriteAllText(path, FormatContent(arguments), Utf8WithoutBom);
        }

        /// <summary>A unique file path in <paramref name="directory"/>. The file is not created.</summary>
        public static string CreateTempPath(string directory)
        {
            return Path.Combine(directory, FileNamePrefix + Guid.NewGuid().ToString("N") + FileExtension);
        }

        /// <summary>
        /// The <c>@"path"</c> argument that replaces the inline arguments. It is parsed by the
        /// process launcher, not by mcs, so it is only double-quoted; temp paths never contain <c>"</c>.
        /// </summary>
        public static string ToCommandLineArgument(string responseFilePath)
        {
            return "@\"" + responseFilePath + "\"";
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}
