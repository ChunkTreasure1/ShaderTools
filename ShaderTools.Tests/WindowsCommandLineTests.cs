using System.Runtime.InteropServices;
using ShaderTools.Core;
using Xunit;

namespace ShaderTools.Tests;

public class WindowsCommandLineTests
{
    [Theory]
    [InlineData("")]
    [InlineData("C:\\source\\shader project\\")]
    [InlineData("C:\\шейдеры\\file.frag")]
    [InlineData("{\"defines\":{\"NAME\":\"a b\"}}")]
    [InlineData("a\\\\\"b $()`&|<>")]
    public void RoundTripsThroughWindowsArgumentParser(string argument)
    {
        IntPtr arguments = CommandLineToArgvW("program.exe " + WindowsCommandLine.Quote(argument), out int count);
        try
        {
            Assert.NotEqual(IntPtr.Zero, arguments);
            Assert.Equal(2, count);
            Assert.Equal(argument, Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, IntPtr.Size)));
        }
        finally { LocalFree(arguments); }
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
