using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ShaderTools.Bridge
{
    /// <summary>
    /// Owns a Windows job whose members and descendants are terminated when the bridge exits.
    /// The operating system closes this handle even when the bridge is forcibly terminated.
    /// </summary>
    internal sealed class ProcessJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private readonly SafeJobHandle handle;

        private ProcessJob(SafeJobHandle handle)
        {
            this.handle = handle;
        }

        public static ProcessJob CreateKillOnClose()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                throw new PlatformNotSupportedException("Shader Tools process containment requires Windows.");
            }

            SafeJobHandle handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the Shader Tools process job.");
            }

            try
            {
                JobObjectExtendedLimitInformation information = new JobObjectExtendedLimitInformation
                {
                    BasicLimitInformation = new JobObjectBasicLimitInformation
                    {
                        LimitFlags = JobObjectLimitKillOnJobClose,
                    },
                };
                int length = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
                IntPtr buffer = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(information, buffer, false);
                    if (!NativeMethods.SetInformationJobObject(
                        handle,
                        JobObjectInformationClass.ExtendedLimitInformation,
                        buffer,
                        (uint)length))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot configure the Shader Tools process job.");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }

                return new ProcessJob(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }

        public void Add(Process process)
        {
            if (process == null)
            {
                throw new ArgumentNullException(nameof(process));
            }

            if (!NativeMethods.AssignProcessToJobObject(handle, process.Handle))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Cannot contain shader-language-server in the Shader Tools process job.");
            }
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        private enum JobObjectInformationClass
        {
            ExtendedLimitInformation = 9,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            private SafeJobHandle()
                : base(true)
            {
            }

            protected override bool ReleaseHandle()
            {
                return NativeMethods.CloseHandle(handle);
            }
        }

        private static class NativeMethods
        {
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            public static extern SafeJobHandle CreateJobObject(IntPtr securityAttributes, string? name);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool SetInformationJobObject(
                SafeJobHandle job,
                JobObjectInformationClass informationClass,
                IntPtr information,
                uint informationLength);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool AssignProcessToJobObject(SafeJobHandle job, IntPtr process);

            [DllImport("kernel32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool CloseHandle(IntPtr handle);
        }
    }
}
