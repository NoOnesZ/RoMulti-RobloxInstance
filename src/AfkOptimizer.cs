using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RoMulti
{
    public static class AfkOptimizer
    {
        public const int CompactWindowWidth = 520;
        public const int CompactWindowHeight = 320;

        public static SystemMemoryStats GetSystemMemoryStats()
        {
            var status = new NativeMethods.MEMORYSTATUSEX();
            status.dwLength = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORYSTATUSEX));
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
            {
                return new SystemMemoryStats();
            }

            const double bytesPerGb = 1024.0 * 1024.0 * 1024.0;
            return new SystemMemoryStats
            {
                TotalPhysicalGB = status.ullTotalPhys / bytesPerGb,
                AvailablePhysicalGB = status.ullAvailPhys / bytesPerGb,
                MemoryLoadPercent = status.dwMemoryLoad
            };
        }

        public static bool TrimWorkingSet(int pid, out double beforeMb, out double afterMb, out string error)
        {
            beforeMb = 0;
            afterMb = 0;
            error = null;

            IntPtr hProc = IntPtr.Zero;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    if (p.HasExited)
                    {
                        error = "Process " + pid + " has already exited.";
                        return false;
                    }
                    beforeMb = p.WorkingSet64 / (1024.0 * 1024.0);
                }

                hProc = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_SET_QUOTA | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    pid);

                if (hProc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    error = err == 5
                        ? "Access denied trimming PID " + pid + " (try Run as Admin)."
                        : "OpenProcess failed for PID " + pid + " (Win32 error " + err + ").";
                    return false;
                }

                bool ok = NativeMethods.EmptyWorkingSet(hProc);
                if (!ok)
                {
                    error = "EmptyWorkingSet failed for PID " + pid + " (Win32 error " + Marshal.GetLastWin32Error() + ").";
                    return false;
                }

                try
                {
                    using (var pAfter = Process.GetProcessById(pid))
                    {
                        afterMb = pAfter.WorkingSet64 / (1024.0 * 1024.0);
                    }
                }
                catch
                {
                    afterMb = beforeMb;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (hProc != IntPtr.Zero)
                    NativeMethods.CloseHandle(hProc);
            }
        }

        public static bool TrimSelfWorkingSet()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                return NativeMethods.EmptyWorkingSet(NativeMethods.GetCurrentProcess());
            }
            catch
            {
                return false;
            }
        }

        public static bool SetEfficiencyMode(int pid, bool enable, out string error)
        {
            error = null;
            IntPtr hProc = IntPtr.Zero;
            try
            {
                hProc = NativeMethods.OpenProcess(
                    NativeMethods.PROCESS_SET_INFORMATION | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                    false,
                    pid);

                if (hProc == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    error = err == 5
                        ? "Access denied setting Efficiency Mode on PID " + pid + "."
                        : "OpenProcess failed for PID " + pid + " (Win32 error " + err + ").";
                    return false;
                }

                var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
                {
                    Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                    ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                    StateMask = enable ? NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0u
                };

                bool ecoOk = NativeMethods.SetProcessInformation(
                    hProc,
                    NativeMethods.ProcessPowerThrottling,
                    ref state,
                    Marshal.SizeOf(typeof(NativeMethods.PROCESS_POWER_THROTTLING_STATE)));

                int ecoErr = ecoOk ? 0 : Marshal.GetLastWin32Error();

                bool prioOk = false;
                try
                {
                    using (var p = Process.GetProcessById(pid))
                    {
                        if (!p.HasExited)
                        {
                            p.PriorityClass = enable ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
                            prioOk = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    error = "Priority update failed: " + ex.Message;
                }

                if (!ecoOk && !prioOk)
                {
                    error = error ?? ("SetProcessInformation failed (Win32 error " + ecoErr + ").");
                    return false;
                }

                return prioOk;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
            finally
            {
                if (hProc != IntPtr.Zero)
                    NativeMethods.CloseHandle(hProc);
            }
        }

        public static bool ResizeCompactNoActivate(IntPtr hwnd, int width, int height)
        {
            if (hwnd == IntPtr.Zero) return false;

            if (NativeMethods.IsIconic(hwnd))
            {
                // Restore without stealing focus from Main window, keeping DWM frame live for OCR
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            }

            return NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                0,
                0,
                width,
                height,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }

        public static AfkApplyResult ApplyRoleProfile(int pid, IntPtr hwnd, InstanceRole role, bool resizeWindow)
        {
            var res = new AfkApplyResult
            {
                ProcessId = pid,
                Role = role
            };

            bool isAfk = (role == InstanceRole.AfkSaver);

            // 1. EcoQoS + CPU Priority
            string ecoErr;
            res.EcoQoSApplied = SetEfficiencyMode(pid, isAfk, out ecoErr);
            if (!res.EcoQoSApplied && !string.IsNullOrEmpty(ecoErr))
                res.Warnings.Add(ecoErr);

            // 2. Audio Mute (mute for AFK, unmute for Main)
            string audioErr;
            res.AudioMuteUpdated = SetProcessAudioMute(pid, isAfk, out audioErr);
            if (!res.AudioMuteUpdated && !string.IsNullOrEmpty(audioErr))
                res.Warnings.Add(audioErr);

            // 3. Compact window resize & RAM Trim (AFK Saver only — NEVER touch Main!)
            if (isAfk)
            {
                if (resizeWindow && hwnd != IntPtr.Zero)
                {
                    res.WindowResized = ResizeCompactNoActivate(hwnd, CompactWindowWidth, CompactWindowHeight);
                }

                double beforeMb, afterMb;
                string trimErr;
                res.RamTrimmed = TrimWorkingSet(pid, out beforeMb, out afterMb, out trimErr);
                res.BeforeWorkingSetMB = beforeMb;
                res.AfterWorkingSetMB = afterMb;
                if (!res.RamTrimmed && !string.IsNullOrEmpty(trimErr))
                    res.Warnings.Add(trimErr);
            }

            return res;
        }

        // --- Windows Core Audio COM Interop (Per-Process Mute, Zero External Dependencies) ---

        private const int eRender = 0;
        private const int eMultimedia = 1;
        private const int CLSCTX_ALL = 23;

        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid IID_IAudioSessionManager2 = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        public static HashSet<int> GetMutedPids()
        {
            var muted = new HashSet<int>();
            EnumerateAudioSessions((sessionPid, volume) =>
            {
                bool isMuted;
                if (volume.GetMute(out isMuted) == 0 && isMuted)
                {
                    muted.Add((int)sessionPid);
                }
            });
            return muted;
        }

        public static bool SetProcessAudioMute(int pid, bool mute, out string error)
        {
            error = null;
            int updatedCount = 0;

            bool enumOk = EnumerateAudioSessions((sessionPid, volume) =>
            {
                if ((int)sessionPid == pid)
                {
                    Guid ctx = Guid.Empty;
                    if (volume.SetMute(mute, ref ctx) == 0)
                        updatedCount++;
                }
            });

            if (!enumOk)
            {
                error = "Default audio output device not available.";
                return false;
            }

            if (updatedCount == 0)
            {
                error = "No active audio session for PID " + pid + " yet.";
                return false;
            }

            return true;
        }

        private static bool EnumerateAudioSessions(Action<uint, ISimpleAudioVolume> visitor)
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioSessionManager2 manager = null;
            IAudioSessionEnumerator sessionEnum = null;

            try
            {
                Type enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (enumType == null) return false;
                enumerator = Activator.CreateInstance(enumType) as IMMDeviceEnumerator;
                if (enumerator == null) return false;

                if (enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device) != 0 || device == null)
                    return false;

                Guid iid = IID_IAudioSessionManager2;
                object managerObj;
                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out managerObj) != 0 || managerObj == null)
                    return false;

                manager = managerObj as IAudioSessionManager2;
                if (manager == null) return false;

                if (manager.GetSessionEnumerator(out sessionEnum) != 0 || sessionEnum == null)
                    return false;

                int count;
                if (sessionEnum.GetCount(out count) != 0)
                    return false;

                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl ctrl = null;
                    try
                    {
                        if (sessionEnum.GetSession(i, out ctrl) != 0 || ctrl == null)
                            continue;

                        var ctrl2 = ctrl as IAudioSessionControl2;
                        var vol = ctrl as ISimpleAudioVolume;
                        if (ctrl2 == null || vol == null)
                            continue;

                        uint sessionPid;
                        if (ctrl2.GetProcessId(out sessionPid) == 0 && sessionPid != 0)
                        {
                            visitor(sessionPid, vol);
                        }
                    }
                    finally
                    {
                        if (ctrl != null) Marshal.ReleaseComObject(ctrl);
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (sessionEnum != null) Marshal.ReleaseComObject(sessionEnum);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport]
        [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            [PreserveSig]
            int GetAudioSessionControl(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
            [PreserveSig]
            int GetSimpleAudioVolume(IntPtr AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
            [PreserveSig]
            int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
        }

        [ComImport]
        [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig]
            int GetCount(out int SessionCount);
            [PreserveSig]
            int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [ComImport]
        [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int pRetVal);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetGroupingParam(out Guid pRetVal);
            [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        }

        [ComImport]
        [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // Inherited from IAudioSessionControl (9 vtable slots)
            [PreserveSig] int GetState(out int pRetVal);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetGroupingParam(out Guid pRetVal);
            [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
            // Own methods
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
            [PreserveSig] int GetProcessId(out uint pRetVal);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }

        [ComImport]
        [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
            [PreserveSig] int GetMasterVolume(out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }
    }
}
