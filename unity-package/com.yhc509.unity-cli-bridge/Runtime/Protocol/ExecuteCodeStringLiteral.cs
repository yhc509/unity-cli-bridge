#nullable enable
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("UnityCli.Cli.Tests")]
[assembly: InternalsVisibleTo("UnityCli.RoslynHost.Tests")]
[assembly: InternalsVisibleTo("UnityCliBridge.Bridge.Editor")]

namespace UnityCli.Protocol
{
    internal static class ExecuteCodeStringLiteral
    {
        internal static string ToCSharpStringLiteral(string value)
        {
            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '\\':
                        builder.Append(@"\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append(@"\r");
                        break;
                    case '\n':
                        builder.Append(@"\n");
                        break;
                    case '\t':
                        builder.Append(@"\t");
                        break;
                    case '\0':
                        builder.Append(@"\0");
                        break;
                    default:
                        if (character >= 0x20 && character <= 0x7e)
                        {
                            builder.Append(character);
                        }
                        else
                        {
                            AppendUnicodeEscape(builder, character);
                        }

                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static void AppendUnicodeEscape(StringBuilder builder, char character)
        {
            builder.Append("\\u");
            builder.Append(((int)character).ToString("x4"));
        }
    }
}
