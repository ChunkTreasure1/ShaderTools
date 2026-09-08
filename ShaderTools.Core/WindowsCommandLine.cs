using System;
using System.Text;

namespace ShaderTools.Core
{
    public static class WindowsCommandLine
    {
        // CommandLineToArgvW / CRT quoting. Never hand this string to a command shell.
        public static string Quote(string argument)
        {
            if (argument == null)
            {
                throw new ArgumentNullException(nameof(argument));
            }

            StringBuilder result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"')
                {
                    _=result.Append('\\', (slashes * 2) + 1);
                }
                else
                {
                    _=result.Append('\\', slashes);
                }

                _=result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
    }
}
