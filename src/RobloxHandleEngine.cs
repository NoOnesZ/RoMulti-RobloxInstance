using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace RoMulti
{
    public sealed class RobloxHandleEngine : IDisposable
    {
        public const string SingletonEventToken = "ROBLOX_singletonEvent";
        public const string SingletonMutexToken = "ROBLOX_singletonMutex";

        private static readonly string[] TargetProcessNames = new[]
        {
            "RobloxPlayerBeta"
        };

        // Reusable unmanaged handle snapshot buffer (avoids repeated 4-16 MB allocations every 1.5s)
        private readonly object _bufferLock = new object();
        private IntPtr _handleSnapshotBuffer = IntPtr.Zero;
        private int _handleSnapshotCapacity = 0;
        private bool _disposed;

        public void Dispose()
        {
            lock (_bufferLock)
            {
                if (_disposed) return;
                _disposed = true;
                if (_handleSnapshotBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_handleSnapshotBuffer);
                    _handleSnapshotBuffer = IntPtr.Zero;
                    _handleSnapshotCapacity = 0;
                }
            }
        }

        public static bool IsRunningAsAdmin()
        {
            try
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch
            {
                return false;
            }
        }

        public List<RobloxInstanceInfo> ScanRobloxInstances(
            IDictionary<int, string> customLabels,
            IDictionary<int, InstanceRole> roleMap,
            bool includeMutexInLockState)
        {
            var result = new List<RobloxInstanceInfo>();
            var procMap = new Dictionary<ulong, RobloxInstanceInfo>();

            foreach (string procName in TargetProcessNames)
            {
                Process[] procs;
                try
                {
                    procs = Process.GetProcessesByName(procName);
                }
                catch
                {
                    continue;
                }

                foreach (Process p in procs)
                {
                    try
                    {
                        if (p.HasExited) continue;

                        int pid = p.Id;
                        string exePath = TryGetProcessPath(p);
                        string versionTag = ExtractVersionTag(exePath);
                        DateTime startTime = DateTime.MinValue;
                        try { startTime = p.StartTime; } catch { }

                        double memMb = 0;
                        try { memMb = p.WorkingSet64 / (1024.0 * 1024.0); } catch { }

                        bool isLowPriority = false;
                        try
                        {
                            var prio = p.PriorityClass;
                            isLowPriority = (prio == ProcessPriorityClass.BelowNormal || prio == ProcessPriorityClass.Idle);
                        }
                        catch { }

                        string label = null;
                        if (customLabels != null)
                            customLabels.TryGetValue(pid, out label);

                        InstanceRole role = InstanceRole.Main;
                        if (roleMap != null)
                            roleMap.TryGetValue(pid, out role);

                        var info = new RobloxInstanceInfo
                        {
                            ProcessId = pid,
                            ProcessName = p.ProcessName,
                            Label = string.IsNullOrWhiteSpace(label) ? ("PID " + pid) : label,
                            Role = role,
                            IsEcoMode = isLowPriority,
                            MemoryMB = memMb,
                            StartTime = startTime,
                            ExePath = exePath,
                            VersionTag = versionTag,
                            State = SingletonState.Unlocked
                        };

                        procMap[(ulong)pid] = info;
                        result.Add(info);
                    }
                    catch
                    {
                        // Process exited mid-scan
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }

            if (result.Count == 0)
                return result;

            var ordered = result.OrderBy(x => x.StartTime == DateTime.MinValue ? DateTime.MaxValue : x.StartTime)
                                .ThenBy(x => x.ProcessId)
                                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                if (customLabels == null || !customLabels.ContainsKey(ordered[i].ProcessId))
                {
                    ordered[i].Label = "Instance #" + (i + 1);
                }
            }

            // Populate window handles & titles
            var windowsByPid = MapBestWindowsByPid(procMap.Keys);
            // Single-pass audio mute snapshot across active Windows Core Audio sessions
            var mutedPids = AfkOptimizer.GetMutedPids();

            foreach (var info in ordered)
            {
                WindowCandidate win;
                if (windowsByPid.TryGetValue((ulong)info.ProcessId, out win))
                {
                    info.WindowHandle = win.Hwnd;
                    info.WindowTitle = win.Title;
                }
                info.IsAudioMuted = mutedPids.Contains(info.ProcessId);
            }

            // Single fast system handle snapshot filtered to Roblox PIDs
            var pidSet = new HashSet<ulong>(procMap.Keys);
            Dictionary<ulong, bool> accessDeniedMap;
            var handlesByPid = QueryHandlesForPids(pidSet, out accessDeniedMap, filterToRobloxKeywords: true);

            foreach (var info in ordered)
            {
                ulong pidKey = (ulong)info.ProcessId;
                bool denied;
                if (accessDeniedMap.TryGetValue(pidKey, out denied) && denied)
                {
                    info.State = SingletonState.AccessDenied;
                    continue;
                }

                List<RobloxHandleInfo> handles;
                if (handlesByPid.TryGetValue(pidKey, out handles))
                {
                    handles.Sort((a, b) =>
                    {
                        int scoreA = a.IsSingletonEvent ? 0 : (a.IsSingletonMutex ? 1 : 2);
                        int scoreB = b.IsSingletonEvent ? 0 : (b.IsSingletonMutex ? 1 : 2);
                        int cmp = scoreA.CompareTo(scoreB);
                        return cmp != 0 ? cmp : string.CompareOrdinal(a.ShortName, b.ShortName);
                    });

                    info.Handles = handles;
                    bool hasEvent = handles.Any(h => h.IsSingletonEvent);
                    bool hasMutex = handles.Any(h => h.IsSingletonMutex);
                    info.State = (hasEvent || (includeMutexInLockState && hasMutex))
                        ? SingletonState.Locked
                        : SingletonState.Unlocked;
                }
            }

            return ordered;
        }

        public List<RobloxHandleInfo> ScanHandlesForPid(int pid, bool filterToRobloxKeywords)
        {
            var pids = new HashSet<ulong> { (ulong)pid };
            Dictionary<ulong, bool> deniedMap;
            var map = QueryHandlesForPids(pids, out deniedMap, filterToRobloxKeywords);
            List<RobloxHandleInfo> list;
            return map.TryGetValue((ulong)pid, out list) ? list : new List<RobloxHandleInfo>();
        }

        public UnlockResult UnlockInstance(int pid, bool closeSingletonEvent = true, bool closeSingletonMutex = true)
        {
            var tokens = new List<string>();
            if (closeSingletonEvent) tokens.Add(SingletonEventToken);
            if (closeSingletonMutex) tokens.Add(SingletonMutexToken);
            return CloseHandlesMatchingTokens(pid, tokens);
        }

        public UnlockResult CloseHandlesMatchingTokens(int pid, IEnumerable<string> nameTokens)
        {
            var tokenList = (nameTokens ?? Enumerable.Empty<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            var res = new UnlockResult { ProcessId = pid };
            if (tokenList.Count == 0) return res;

            IntPtr hProc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_DUP_HANDLE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                pid);

            if (hProc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                res.AccessDenied = (err == 5);
                res.ErrorMessage = res.AccessDenied
                    ? "Access denied opening PID " + pid + ". Try running RoMulti as Administrator."
                    : "OpenProcess failed (Win32 error " + err + ").";
                return res;
            }

            try
            {
                var pids = new HashSet<ulong> { (ulong)pid };
                Dictionary<ulong, bool> deniedMap;
                var handlesMap = QueryHandlesForPids(pids, out deniedMap, filterToRobloxKeywords: false);

                List<RobloxHandleInfo> handles;
                if (!handlesMap.TryGetValue((ulong)pid, out handles) || handles.Count == 0)
                    return res;

                IntPtr curProc = NativeMethods.GetCurrentProcess();

                foreach (var h in handles)
                {
                    bool matches = false;
                    foreach (string token in tokenList)
                    {
                        if (!string.IsNullOrEmpty(h.FullObjectName) &&
                            h.FullObjectName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matches = true;
                            break;
                        }
                    }

                    if (!matches) continue;

                    if (!VerifyAndCloseSingleHandle(hProc, curProc, h.HandleValue, h.FullObjectName))
                        continue;

                    res.ClosedCount++;
                    res.ClosedHandleNames.Add(h.ShortName + " (" + h.HandleHex + ")");
                }
            }
            catch (Exception ex)
            {
                res.ErrorMessage = ex.Message;
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }

            return res;
        }

        public bool CloseSpecificHandle(int pid, ulong handleValue, string expectedFullName, out string error)
        {
            error = null;
            IntPtr hProc = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_DUP_HANDLE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                pid);

            if (hProc == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                error = err == 5
                    ? "Access denied. Run RoMulti as Administrator to close this handle."
                    : "OpenProcess failed (Win32 error " + err + ").";
                return false;
            }

            try
            {
                bool ok = VerifyAndCloseSingleHandle(hProc, NativeMethods.GetCurrentProcess(), handleValue, expectedFullName);
                if (!ok)
                {
                    error = "Handle no longer matches or could not be closed (Win32 error " + Marshal.GetLastWin32Error() + ").";
                }
                return ok;
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }
        }

        private static bool VerifyAndCloseSingleHandle(IntPtr hProc, IntPtr curProc, ulong handleValue, string expectedFullName)
        {
            IntPtr srcHandle = new IntPtr((long)handleValue);

            IntPtr verifyDup;
            if (!NativeMethods.DuplicateHandle(hProc, srcHandle, curProc, out verifyDup, 0, false, 0))
                return false;

            bool nameMatches = false;
            try
            {
                string typeName = QueryObjectString(verifyDup, NativeMethods.ObjectTypeInformation);
                if (typeName == "Event" || typeName == "Mutant" || typeName == "Semaphore")
                {
                    string objName = QueryObjectString(verifyDup, NativeMethods.ObjectNameInformation);
                    nameMatches = string.Equals(objName, expectedFullName, StringComparison.OrdinalIgnoreCase);
                }
            }
            finally
            {
                NativeMethods.CloseHandle(verifyDup);
            }

            if (!nameMatches) return false;

            IntPtr closeDup;
            if (!NativeMethods.DuplicateHandle(
                hProc,
                srcHandle,
                curProc,
                out closeDup,
                0,
                false,
                NativeMethods.DUPLICATE_CLOSE_SOURCE))
            {
                return false;
            }

            if (closeDup != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(closeDup);
            }

            return true;
        }

        private Dictionary<ulong, List<RobloxHandleInfo>> QueryHandlesForPids(
            HashSet<ulong> targetPids,
            out Dictionary<ulong, bool> accessDeniedMap,
            bool filterToRobloxKeywords)
        {
            var result = new Dictionary<ulong, List<RobloxHandleInfo>>();
            accessDeniedMap = new Dictionary<ulong, bool>();

            if (targetPids == null || targetPids.Count == 0)
                return result;

            lock (_bufferLock)
            {
                if (_disposed) return result;

                if (_handleSnapshotBuffer == IntPtr.Zero)
                {
                    _handleSnapshotCapacity = 4 * 1024 * 1024;
                    _handleSnapshotBuffer = Marshal.AllocHGlobal(_handleSnapshotCapacity);
                }

                int returnLength;
                uint status;
                int attempts = 0;

                while ((status = NativeMethods.NtQuerySystemInformation(
                    NativeMethods.SystemExtendedHandleInformation,
                    _handleSnapshotBuffer,
                    _handleSnapshotCapacity,
                    out returnLength)) == NativeMethods.STATUS_INFO_LENGTH_MISMATCH)
                {
                    attempts++;
                    if (attempts > 8) break;
                    Marshal.FreeHGlobal(_handleSnapshotBuffer);
                    _handleSnapshotCapacity = Math.Max(_handleSnapshotCapacity * 2, returnLength + 131072);
                    _handleSnapshotBuffer = Marshal.AllocHGlobal(_handleSnapshotCapacity);
                }

                if (status != NativeMethods.STATUS_SUCCESS)
                    return result;

                long handleCount = Marshal.ReadIntPtr(_handleSnapshotBuffer).ToInt64();
                IntPtr entriesBase = new IntPtr(_handleSnapshotBuffer.ToInt64() + IntPtr.Size * 2);
                int entrySize = Marshal.SizeOf(typeof(NativeMethods.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));

                var procHandles = new Dictionary<ulong, IntPtr>();
                foreach (ulong pid in targetPids)
                {
                    IntPtr h = NativeMethods.OpenProcess(
                        NativeMethods.PROCESS_DUP_HANDLE | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                        false,
                        (int)pid);

                    if (h == IntPtr.Zero)
                    {
                        accessDeniedMap[pid] = (Marshal.GetLastWin32Error() == 5);
                    }
                    else
                    {
                        procHandles[pid] = h;
                        result[pid] = new List<RobloxHandleInfo>();
                    }
                }

                IntPtr curProc = NativeMethods.GetCurrentProcess();
                try
                {
                    for (long i = 0; i < handleCount; i++)
                    {
                        IntPtr entryPtr = new IntPtr(entriesBase.ToInt64() + i * entrySize);
                        var entry = Marshal.PtrToStructure<NativeMethods.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(entryPtr);

                        ulong pid = entry.UniqueProcessId.ToUInt64();
                        IntPtr hProc;
                        if (!procHandles.TryGetValue(pid, out hProc))
                            continue;

                        IntPtr dup;
                        if (!NativeMethods.DuplicateHandle(
                            hProc,
                            new IntPtr((long)entry.HandleValue.ToUInt64()),
                            curProc,
                            out dup,
                            0,
                            false,
                            0))
                        {
                            continue;
                        }

                        try
                        {
                            string typeName = QueryObjectString(dup, NativeMethods.ObjectTypeInformation);
                            if (typeName != "Event" && typeName != "Mutant" && typeName != "Semaphore")
                                continue;

                            string fullName = QueryObjectString(dup, NativeMethods.ObjectNameInformation);
                            if (string.IsNullOrEmpty(fullName))
                                continue;

                            bool isEvent = fullName.IndexOf(SingletonEventToken, StringComparison.OrdinalIgnoreCase) >= 0;
                            bool isMutex = fullName.IndexOf(SingletonMutexToken, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (filterToRobloxKeywords)
                            {
                                bool isRobloxRelated = isEvent || isMutex ||
                                    fullName.IndexOf("roblox", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    fullName.IndexOf("singleton", StringComparison.OrdinalIgnoreCase) >= 0;

                                if (!isRobloxRelated)
                                    continue;
                            }

                            string shortName = fullName;
                            int lastSlash = fullName.LastIndexOf('\\');
                            if (lastSlash >= 0 && lastSlash + 1 < fullName.Length)
                                shortName = fullName.Substring(lastSlash + 1);

                            result[pid].Add(new RobloxHandleInfo
                            {
                                ProcessId = (int)pid,
                                HandleValue = entry.HandleValue.ToUInt64(),
                                TypeName = typeName,
                                FullObjectName = fullName,
                                ShortName = shortName,
                                GrantedAccess = entry.GrantedAccess,
                                IsSingletonEvent = isEvent,
                                IsSingletonMutex = isMutex
                            });
                        }
                        finally
                        {
                            NativeMethods.CloseHandle(dup);
                        }
                    }
                }
                finally
                {
                    foreach (var kv in procHandles)
                    {
                        NativeMethods.CloseHandle(kv.Value);
                    }
                }
            }

            return result;
        }

        private static string QueryObjectString(IntPtr handle, int infoClass)
        {
            int len = 1024;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int retLen;
                uint status = NativeMethods.NtQueryObject(handle, infoClass, buf, len, out retLen);
                if (status == NativeMethods.STATUS_INFO_LENGTH_MISMATCH && retLen > 0)
                {
                    Marshal.FreeHGlobal(buf);
                    len = retLen + 256;
                    buf = Marshal.AllocHGlobal(len);
                    status = NativeMethods.NtQueryObject(handle, infoClass, buf, len, out retLen);
                }

                if (status != NativeMethods.STATUS_SUCCESS)
                    return null;

                var us = Marshal.PtrToStructure<NativeMethods.UNICODE_STRING>(buf);
                if (us.Length == 0 || us.Buffer == IntPtr.Zero)
                    return string.Empty;

                return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        private struct WindowCandidate
        {
            public IntPtr Hwnd;
            public string Title;
            public string ClassName;
            public int Score;
        }

        private static Dictionary<ulong, WindowCandidate> MapBestWindowsByPid(IEnumerable<ulong> targetPids)
        {
            var pidSet = new HashSet<ulong>(targetPids);
            var best = new Dictionary<ulong, WindowCandidate>();

            NativeMethods.EnumWindows((hWnd, lParam) =>
            {
                uint pid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out pid);
                if (!pidSet.Contains(pid)) return true;

                bool visible = NativeMethods.IsWindowVisible(hWnd);
                var sbTitle = new StringBuilder(256);
                var sbClass = new StringBuilder(128);
                NativeMethods.GetWindowText(hWnd, sbTitle, sbTitle.Capacity);
                NativeMethods.GetClassName(hWnd, sbClass, sbClass.Capacity);

                string title = sbTitle.ToString();
                string cls = sbClass.ToString();

                int score = 0;
                if (visible) score += 100;
                if (string.Equals(cls, "WINDOWSCLIENT", StringComparison.OrdinalIgnoreCase)) score += 200;
                if (!string.IsNullOrWhiteSpace(title)) score += 50;

                NativeMethods.RECT rect;
                if (NativeMethods.GetWindowRect(hWnd, out rect))
                {
                    int w = rect.Right - rect.Left;
                    int h = rect.Bottom - rect.Top;
                    if (w > 200 && h > 200) score += 40;
                }

                WindowCandidate existing;
                if (!best.TryGetValue(pid, out existing) || score > existing.Score)
                {
                    best[pid] = new WindowCandidate
                    {
                        Hwnd = hWnd,
                        Title = title,
                        ClassName = cls,
                        Score = score
                    };
                }

                return true;
            }, IntPtr.Zero);

            return best;
        }

        public static bool FocusWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            if (NativeMethods.IsIconic(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            else
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);

            NativeMethods.BringWindowToTop(hwnd);
            return NativeMethods.SetForegroundWindow(hwnd);
        }

        public static bool RenameWindow(IntPtr hwnd, string newTitle)
        {
            if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(newTitle)) return false;
            return NativeMethods.SetWindowText(hwnd, newTitle.Trim());
        }

        public static int TileWindows(IList<IntPtr> hwnds, TileLayout layout)
        {
            var valid = hwnds.Where(h => h != IntPtr.Zero).ToList();
            if (valid.Count == 0) return 0;

            var area = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;
            int n = valid.Count;
            int cols, rows;

            switch (layout)
            {
                case TileLayout.SideBySide:
                    cols = n;
                    rows = 1;
                    break;
                case TileLayout.Stacked:
                    cols = 1;
                    rows = n;
                    break;
                default:
                    cols = (int)Math.Ceiling(Math.Sqrt(n));
                    rows = (int)Math.Ceiling((double)n / cols);
                    break;
            }

            int cellW = area.Width / Math.Max(1, cols);
            int cellH = area.Height / Math.Max(1, rows);

            int moved = 0;
            for (int i = 0; i < n; i++)
            {
                int col = i % cols;
                int row = i / cols;
                int x = area.Left + col * cellW;
                int y = area.Top + row * cellH;
                int w = (col == cols - 1) ? (area.Right - x) : cellW;
                int h = (row == rows - 1) ? (area.Bottom - y) : cellH;

                if (NativeMethods.IsIconic(valid[i]))
                    NativeMethods.ShowWindow(valid[i], NativeMethods.SW_RESTORE);

                if (NativeMethods.MoveWindow(valid[i], x, y, w, h, true))
                    moved++;
            }

            return moved;
        }

        public static string FindLatestRobloxPlayerExe()
        {
            var searchRoots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Roblox", "Versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Roblox", "Versions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Roblox", "Versions")
            };

            string bestPath = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (string root in searchRoots)
            {
                if (!Directory.Exists(root)) continue;
                try
                {
                    foreach (string dir in Directory.GetDirectories(root, "version-*"))
                    {
                        string candidate = Path.Combine(dir, "RobloxPlayerBeta.exe");
                        if (!File.Exists(candidate)) continue;
                        DateTime wt = File.GetLastWriteTimeUtc(candidate);
                        if (wt > bestTime)
                        {
                            bestTime = wt;
                            bestPath = candidate;
                        }
                    }
                }
                catch { }
            }

            return bestPath;
        }

        public static bool LaunchNewRobloxClient(out string message)
        {
            string exe = FindLatestRobloxPlayerExe();
            if (string.IsNullOrEmpty(exe))
            {
                message = "RobloxPlayerBeta.exe not found in LocalAppData or Program Files.";
                return false;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = "--app",
                    WorkingDirectory = Path.GetDirectoryName(exe),
                    UseShellExecute = true
                };
                Process.Start(psi);
                message = "Launched " + Path.GetFileName(Path.GetDirectoryName(exe)) + "\\RobloxPlayerBeta.exe";
                return true;
            }
            catch (Exception ex)
            {
                message = "Failed to launch Roblox: " + ex.Message;
                return false;
            }
        }

        public static bool KillProcess(int pid, out string error)
        {
            error = null;
            try
            {
                using (var p = Process.GetProcessById(pid))
                {
                    p.Kill();
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static string TryGetProcessPath(Process p)
        {
            IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
            if (h == IntPtr.Zero) return string.Empty;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (NativeMethods.QueryFullProcessImageName(h, 0, sb, ref size))
                    return sb.ToString();
                return string.Empty;
            }
            finally
            {
                NativeMethods.CloseHandle(h);
            }
        }

        private static string ExtractVersionTag(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return "-";
            try
            {
                string dir = Path.GetFileName(Path.GetDirectoryName(exePath));
                return string.IsNullOrEmpty(dir) ? "-" : dir;
            }
            catch
            {
                return "-";
            }
        }
    }
}
