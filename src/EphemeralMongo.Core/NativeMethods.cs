using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.JobObjects;
using Microsoft.Win32.SafeHandles;

namespace EphemeralMongo;

internal static class NativeMethods
{
    private static readonly Lazy<SafeFileHandle?> _lazyWin32Job = new Lazy<SafeFileHandle?>(CreateWin32Job);

    public static void EnsureMongoProcessesAreKilledWhenCurrentProcessIsKilled()
    {
        // We only support this feature on Windows for now
        // https://www.meziantou.net/killing-all-child-processes-when-the-parent-exits-job-object.htm
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _ = _lazyWin32Job.Value;
        }
    }

    private static SafeFileHandle? CreateWin32Job()
    {
        unsafe
        {
            var attributes = new Windows.Win32.Security.SECURITY_ATTRIBUTES
            {
                bInheritHandle = false,
                lpSecurityDescriptor = IntPtr.Zero.ToPointer(),
                nLength = (uint)Marshal.SizeOf(typeof(Windows.Win32.Security.SECURITY_ATTRIBUTES)),
            };

            SafeFileHandle? jobHandle = null;

            try
            {
                jobHandle = PInvoke.CreateJobObject(attributes, lpName: null);

                if (jobHandle.IsInvalid)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // Configure the job object to kill all child processes when the root process is killed
                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        // Kill all processes associated to the job when the last handle is closed
                        LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                if (!PInvoke.SetInformationJobObject(jobHandle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &info, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    return null;
                }

                // Assign the job object to the current process
                if (!PInvoke.AssignProcessToJobObject(jobHandle, Process.GetCurrentProcess().SafeHandle))
                {
                    return null;
                }
            }
            catch
            {
                jobHandle?.Dispose();
            }

            return jobHandle;
        }
    }
}