using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using Microsoft.Win32.SafeHandles;

namespace EphemeralMongo;

internal static class NativeMethods
{
    private static readonly object _createJobObjectLock = new object();
    private static SafeFileHandle? _jobObjectHandle;

    public static void EnsureMongoProcessesAreKilledWhenCurrentProcessIsKilled()
    {
        // We only support this feature on Windows and modern .NET (netcoreapp3.1, net5.0, net6.0, net7.0 and so on):
        // - Job objects are Windows-specific
        // - On .NET Framework, the current process crashes even if we don't dispose the job object handle (tested with in test project while running "dotnet test")
        //
        // "A job object allows groups of processes to be managed as a unit.
        // Operations performed on a job object affect all processes associated with the job object.
        // Examples include [...] or terminating all processes associated with a job."
        // See: https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
        if (IsWindows() && !IsNetFramework())
        {
            CreateSingletonJobObject();
        }
    }

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    // This way of detecting if running on .NET Framework is also used in .NET runtime tests, see:
    // https://github.com/dotnet/runtime/blob/v6.0.0/src/libraries/Common/tests/TestUtilities/System/PlatformDetection.Windows.cs#L21
    private static bool IsNetFramework() => RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.OrdinalIgnoreCase);

    private static unsafe void CreateSingletonJobObject()
    {
        // Using a static job object ensures there's a single job object created for the current process.
        // Any MongoDB-related process that we will be created later on will be associated to the current process through this job object.
        // If the current process dies prematurely, all MongoDB-related processes will also be killed.
        // However, we never dispose this job object handle otherwise it would immediately kill the current process too.
        if (_jobObjectHandle != null)
        {
            return;
        }

        lock (_createJobObjectLock)
        {
            if (_jobObjectHandle != null)
            {
                return;
            }

            // https://www.meziantou.net/killing-all-child-processes-when-the-parent-exits-job-object.htm
            var attributes = new SECURITY_ATTRIBUTES
            {
                bInheritHandle = false,
                lpSecurityDescriptor = IntPtr.Zero.ToPointer(),
                nLength = (uint)Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
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
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                // Assign the job object to the current process
                if (!PInvoke.AssignProcessToJobObject(jobHandle, Process.GetCurrentProcess().SafeHandle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _jobObjectHandle = jobHandle;
            }
            catch
            {
                // It's safe to dispose the job object handle here because it was not yet associated to the current process
                jobHandle?.Dispose();
                throw;
            }
        }
    }
}