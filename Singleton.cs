// roblox blocks a second client with two named kernel objects,
// ROBLOX_singletonEvent and ROBLOX_singletonMutex. hold both and it can't.
//
// if roblox is already up it owns them, and you can't take a name someone
// else owns, so HandleCloser has to free them first. always that order.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace Archon
{
    class SingletonLock
    {
        public string Name;
        public Mutex Handle;
        public bool Owned;
        public string Error;
    }

    // call everything on one thread, a mutex can only be released by whoever took it
    class SingletonManager
    {
        public static readonly string[] Names = new string[]
        {
            "ROBLOX_singletonEvent",
            "ROBLOX_singletonMutex"
        };

        private readonly List<SingletonLock> _locks = new List<SingletonLock>();

        public bool AnyOwned
        {
            get
            {
                foreach (SingletonLock l in _locks)
                    if (l.Owned) return true;
                return false;
            }
        }

        public bool AllOwned
        {
            get
            {
                if (_locks.Count != Names.Length) return false;
                foreach (SingletonLock l in _locks)
                    if (!l.Owned) return false;
                return true;
            }
        }

        public bool Attempted { get { return _locks.Count > 0; } }

        public string FirstError
        {
            get
            {
                foreach (SingletonLock l in _locks)
                    if (!l.Owned && !string.IsNullOrEmpty(l.Error))
                        return l.Name + ": " + l.Error;
                return null;
            }
        }

        public void Acquire()
        {
            Release();
            for (int i = 0; i < Names.Length; i++)
                _locks.Add(AcquireOne(Names[i]));
        }

        // run elevated and the names get the elevated token's default
        // descriptor, which makes roblox's CreateEventW fail with
        // ACCESS_DENIED instead of INVALID_HANDLE. different path in roblox,
        // no reason it behaves the same. so: everyone gets access.
        //
        // no low-integrity label on top - MutexSecurity can't carry one. it
        // drops the ML ace, and the empty SACL left behind needs
        // SeSecurityPrivilege, so creating the mutex with it just throws.
        private static Mutex CreateOpenMutex(string name, out bool createdNew)
        {
            try
            {
                MutexSecurity security = new MutexSecurity();
                security.AddAccessRule(new MutexAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    MutexRights.FullControl,
                    AccessControlType.Allow));
                return new Mutex(true, name, out createdNew, security);
            }
            catch (Exception)
            {
            }

            return new Mutex(true, name, out createdNew);
        }

        // new Mutex on an existing name doesn't throw, it just hands back one
        // you don't own. check createdNew/WaitOne or failure looks like success.
        private static SingletonLock AcquireOne(string name)
        {
            SingletonLock l = new SingletonLock();
            l.Name = name;

            try
            {
                bool createdNew;
                l.Handle = CreateOpenMutex(name, out createdNew);

                if (createdNew)
                {
                    l.Owned = true;
                }
                else
                {
                    try
                    {
                        l.Owned = l.Handle.WaitOne(0);
                    }
                    catch (AbandonedMutexException)
                    {
                        // last owner died without releasing, so we have it now
                        l.Owned = true;
                    }
                }

                if (!l.Owned) l.Error = "held by another process";
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                // name exists as a different object type, ie roblox made it an Event
                l.Error = "held by Roblox - free its handles first";
            }
            catch (Exception ex)
            {
                l.Error = ex.Message;
            }

            return l;
        }

        public void Release()
        {
            foreach (SingletonLock l in _locks)
            {
                if (l.Handle == null) continue;
                try
                {
                    // releasing one we don't own throws
                    if (l.Owned) l.Handle.ReleaseMutex();
                }
                catch { }

                try { l.Handle.Dispose(); }
                catch { }

                l.Owned = false;
                l.Handle = null;
            }
            _locks.Clear();
        }
    }
}
