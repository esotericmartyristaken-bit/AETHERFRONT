using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ApfCore
{
    public static class ApfProcessArguments
    {
        public static string Join(IEnumerable<string> arguments)
        {
            return string.Join(" ", (arguments ?? Enumerable.Empty<string>()).Select(Quote));
        }

        /// <summary>
        /// Quotes one argument using the Windows CommandLineToArgvW rules.
        /// Backslashes are doubled only when they precede a quote or the closing quote.
        /// </summary>
        public static string Quote(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return "\"\"";
            if (!argument.Any(char.IsWhiteSpace) && !argument.Contains('"')) return argument;

            var builder = new StringBuilder(argument.Length + 2);
            builder.Append('"');
            var backslashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    builder.Append('\\', backslashes * 2 + 1);
                    builder.Append('"');
                    backslashes = 0;
                    continue;
                }

                builder.Append('\\', backslashes);
                builder.Append(character);
                backslashes = 0;
            }

            builder.Append('\\', backslashes * 2);
            builder.Append('"');
            return builder.ToString();
        }
    }
}
