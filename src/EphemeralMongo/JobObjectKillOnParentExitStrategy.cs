using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.JobObjects;
using Microsoft.Win32.SafeHandles;

namespace EphemeralMongo;

// The current process is assigned to a job object, see: https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects
// This can't be undone, so it also applies to MongoDB processes started afterward by runners that disabled it.
// Not used on .NET Framework, where the current process crashes even if we don't dispose the job object handle.
[SuppressMessage("Interoperability", "CA1416:Validate platform compatibility", Justification = "This strategy is only used on Windows")]
internal sealed class JobObjectKillOnParentExitStrategy : IKillOnParentExitStrategy
{
    private static readonly object CreateJobObjectLock = new object();
    private static SafeFileHandle? _jobObjectHandle;

    public void Configure(ProcessStartInfo processStartInfo)
    {
        try
        {
            CreateSingletonJobObject();
        }
        catch (Win32Exception ex)
        {
            throw new EphemeralMongoException($"An error occurred trying to assign the current process to a job object. {KillOnParentExit.DisableHint}", ex);
        }
    }

    public void Start(Process process, string executablePath)
    {
        process.Start();
    }

    public string GetTroubleshootingMessage(int exitCode, string output)
    {
        return string.Empty;
    }

    private static unsafe void CreateSingletonJobObject()
    {
        // Never disposed, as closing the handle kills the current process too
        if (_jobObjectHandle != null)
        {
            return;
        }

        lock (CreateJobObjectLock)
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

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                    },
                };

                var infoBytes = new ReadOnlySpan<byte>(&info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());

                if (!PInvoke.SetInformationJobObject(jobHandle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, infoBytes))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (!PInvoke.AssignProcessToJobObject(jobHandle, Process.GetCurrentProcess().SafeHandle))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                _jobObjectHandle = jobHandle;
            }
            catch
            {
                // Safe, as the current process isn't assigned to the job object yet
                jobHandle?.Dispose();
                throw;
            }
        }
    }
}
