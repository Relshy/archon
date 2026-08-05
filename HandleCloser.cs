// finds roblox's singleton handles inside the running clients and closes them,
// which frees the names so we can take them.
//
// heads up: this file is compiled into the exe AND Add-Type'd at runtime by
// MultiInstance.ps1, so it can't reference anything else in the project or the
// script stops being able to compile it on its own.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Archon
{
    public class HandleCloseResult
    {
        public int Closed;
        public int Inspected;
        public int ProcessesScanned;
        public int OpenFailures;
        public string Error;
    }

    // public because powershell can't touch internal types
    public static class RobloxHandleCloser
    {
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int size, out int returnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr info, int size, out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(IntPtr hSource, IntPtr hSourceHandle, IntPtr hTarget,
            out IntPtr hTargetHandle, int access, bool inherit, int options);

        // same api, but DUPLICATE_CLOSE_SOURCE needs both the target process
        // and the out pointer to be NULL, and "out IntPtr" can't do that
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "DuplicateHandle")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandleClose(IntPtr hSource, IntPtr hSourceHandle, IntPtr hTarget,
            IntPtr hTargetHandle, int access, bool inherit, int options);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        private const int SystemExtendedHandleInformation = 64;
        private const int ObjectNameInformation = 1;
        private const int PROCESS_DUP_HANDLE = 0x0040;
        private const int DUPLICATE_CLOSE_SOURCE = 0x1;
        private const int DUPLICATE_SAME_ACCESS = 0x2;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

        // SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX. read by offset rather than
        // marshalled, there's 100k+ of these and boxing each one is slow.
        //   0                   Object                (PVOID)
        //   IntPtr.Size         UniqueProcessId       (ULONG_PTR)
        //   IntPtr.Size * 2     HandleValue           (ULONG_PTR)
        //   IntPtr.Size * 3     GrantedAccess         (ULONG)
        //   IntPtr.Size * 3 + 4 CreatorBackTraceIndex (USHORT)
        //   IntPtr.Size * 3 + 6 ObjectTypeIndex       (USHORT)
        private static readonly int EntrySize = (IntPtr.Size * 3) + 16;

        // duplicated from SingletonManager.Names on purpose, see the note up top.
        // keep them in step.
        private static readonly string[] TargetNames = new string[]
        {
            "ROBLOX_singletonEvent",
            "ROBLOX_singletonMutex"
        };

        public static HandleCloseResult Close(string[] processNames)
        {
            HandleCloseResult result = new HandleCloseResult();

            HashSet<int> targetPids = new HashSet<int>();
            for (int i = 0; i < processNames.Length; i++)
            {
                Process[] procs;
                try { procs = Process.GetProcessesByName(processNames[i]); }
                catch { continue; }

                foreach (Process p in procs)
                {
                    using (p) { targetPids.Add(p.Id); }
                }
            }

            result.ProcessesScanned = targetPids.Count;
            if (targetPids.Count == 0) return result;

            int myPid;
            using (Process self = Process.GetCurrentProcess()) { myPid = self.Id; }

            // make our own mutex + event so we can find out what type index
            // they get. varies per windows build so it can't be hardcoded.
            string suffix = Guid.NewGuid().ToString("N");
            using (Mutex probeMutex = new Mutex(false, "Archon_TypeProbe_M_" + suffix))
            using (EventWaitHandle probeEvent = new EventWaitHandle(false, EventResetMode.ManualReset, "Archon_TypeProbe_E_" + suffix))
            {
                IntPtr probeMutexHandle = probeMutex.SafeWaitHandle.DangerousGetHandle();
                IntPtr probeEventHandle = probeEvent.SafeWaitHandle.DangerousGetHandle();

                IntPtr buffer = IntPtr.Zero;
                int size = 0x40000;
                int status;
                int returnLength;

                try
                {
                    for (int attempt = 0; ; attempt++)
                    {
                        buffer = Marshal.AllocHGlobal(size);
                        status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, size, out returnLength);
                        if (status != STATUS_INFO_LENGTH_MISMATCH) break;

                        Marshal.FreeHGlobal(buffer);
                        buffer = IntPtr.Zero;

                        int next = (returnLength > size) ? returnLength + 0x20000 : size * 2;
                        if (attempt >= 16 || next <= size || next > 0x8000000)
                        {
                            result.Error = "Handle table did not fit in a reasonable buffer.";
                            return result;
                        }
                        size = next;
                    }

                    if (status != 0)
                    {
                        result.Error = "NtQuerySystemInformation failed (0x" + status.ToString("X8") + ").";
                        return result;
                    }

                    long count = Marshal.ReadIntPtr(buffer).ToInt64();
                    long entriesBase = buffer.ToInt64() + (IntPtr.Size * 2);

                    // pass 1, find the probes and read their type indices
                    int mutantType = -1;
                    int eventType = -1;
                    for (long i = 0; i < count; i++)
                    {
                        IntPtr entry = new IntPtr(entriesBase + (i * EntrySize));
                        if (Marshal.ReadIntPtr(entry, IntPtr.Size).ToInt64() != myPid) continue;

                        IntPtr handleValue = Marshal.ReadIntPtr(entry, IntPtr.Size * 2);
                        if (handleValue == probeMutexHandle)
                            mutantType = (ushort)Marshal.ReadInt16(entry, (IntPtr.Size * 3) + 6);
                        else if (handleValue == probeEventHandle)
                            eventType = (ushort)Marshal.ReadInt16(entry, (IntPtr.Size * 3) + 6);

                        if (mutantType >= 0 && eventType >= 0) break;
                    }

                    if (mutantType < 0 || eventType < 0)
                    {
                        result.Error = "Could not determine Mutant/Event object type indices.";
                        return result;
                    }

                    // pass 2, close the ones that match.
                    // only mutant/event get their name queried - NtQueryObject
                    // can hang forever on a sync named pipe and roblox has loads.
                    IntPtr selfProcess = GetCurrentProcess();
                    Dictionary<int, IntPtr> opened = new Dictionary<int, IntPtr>();
                    try
                    {
                        for (long i = 0; i < count; i++)
                        {
                            IntPtr entry = new IntPtr(entriesBase + (i * EntrySize));

                            long ownerPid = Marshal.ReadIntPtr(entry, IntPtr.Size).ToInt64();
                            if (ownerPid < 0 || ownerPid > int.MaxValue) continue;
                            if (!targetPids.Contains((int)ownerPid)) continue;

                            int typeIndex = (ushort)Marshal.ReadInt16(entry, (IntPtr.Size * 3) + 6);
                            if (typeIndex != mutantType && typeIndex != eventType) continue;

                            IntPtr processHandle;
                            if (!opened.TryGetValue((int)ownerPid, out processHandle))
                            {
                                processHandle = OpenProcess(PROCESS_DUP_HANDLE, false, (int)ownerPid);
                                opened[(int)ownerPid] = processHandle;
                                if (processHandle == IntPtr.Zero) result.OpenFailures++;
                            }
                            if (processHandle == IntPtr.Zero) continue;

                            IntPtr sourceHandle = Marshal.ReadIntPtr(entry, IntPtr.Size * 2);
                            IntPtr dupHandle;
                            if (!DuplicateHandle(processHandle, sourceHandle, selfProcess, out dupHandle,
                                                 0, false, DUPLICATE_SAME_ACCESS))
                                continue;
                            if (dupHandle == IntPtr.Zero) continue;

                            // keep the dup open till after the close, it holds a
                            // ref so the name can't start meaning something else
                            try
                            {
                                string name = GetObjectName(dupHandle);

                                result.Inspected++;
                                if (!IsSingletonName(name)) continue;

                                // the table is a snapshot. roblox could have
                                // closed this handle by now and the kernel could
                                // have handed the same value to a file or socket.
                                // closing that would crash the client for no
                                // traceable reason, so check the name again.
                                if (!StillNamed(processHandle, sourceHandle, selfProcess, name))
                                    continue;

                                if (DuplicateHandleClose(processHandle, sourceHandle, IntPtr.Zero, IntPtr.Zero,
                                                         0, false, DUPLICATE_CLOSE_SOURCE))
                                    result.Closed++;
                            }
                            finally
                            {
                                CloseHandle(dupHandle);
                            }
                        }
                    }
                    finally
                    {
                        foreach (IntPtr h in opened.Values)
                            if (h != IntPtr.Zero) CloseHandle(h);
                    }
                }
                catch (Exception ex)
                {
                    result.Error = ex.Message;
                }
                finally
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                }
            }

            return result;
        }

        private static bool IsSingletonName(string name)
        {
            if (name == null) return false;

            for (int t = 0; t < TargetNames.Length; t++)
                if (name.IndexOf(TargetNames[t], StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

            return false;
        }

        // dup it again and check the name still matches, right before we close
        // it. two calls back to back is the tightest this api lets you get.
        private static bool StillNamed(IntPtr processHandle, IntPtr sourceHandle,
                                       IntPtr selfProcess, string expected)
        {
            IntPtr check;
            if (!DuplicateHandle(processHandle, sourceHandle, selfProcess, out check,
                                 0, false, DUPLICATE_SAME_ACCESS))
                return false;
            if (check == IntPtr.Zero) return false;

            try
            {
                return string.Equals(GetObjectName(check), expected, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                CloseHandle(check);
            }
        }

        private static string GetObjectName(IntPtr handle)
        {
            int size = 0x800;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int returnLength;
                int status = NtQueryObject(handle, ObjectNameInformation, buffer, size, out returnLength);

                if (status == STATUS_INFO_LENGTH_MISMATCH && returnLength > size && returnLength < 0x10000)
                {
                    // null it before freeing, otherwise a throw in AllocHGlobal
                    // leaves the stale pointer for finally to free twice
                    IntPtr old = buffer;
                    buffer = IntPtr.Zero;
                    Marshal.FreeHGlobal(old);

                    size = returnLength;
                    buffer = Marshal.AllocHGlobal(size);
                    status = NtQueryObject(handle, ObjectNameInformation, buffer, size, out returnLength);
                }

                if (status != 0) return null;

                // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; }
                int byteLength = (ushort)Marshal.ReadInt16(buffer, 0);
                if (byteLength == 0) return null;

                IntPtr stringPtr = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                if (stringPtr == IntPtr.Zero) return null;

                return Marshal.PtrToStringUni(stringPtr, byteLength / 2);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
