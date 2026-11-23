using System;
using System.Collections.Generic;

namespace RoMulti
{
    public enum SingletonState
    {
        Unlocked,
        Locked,
        AccessDenied
    }

    public enum InstanceRole
    {
        Main,
        AfkSaver
    }

    public enum TileLayout
    {
        AutoGrid,
        SideBySide,
        Stacked
    }

    public sealed class SystemMemoryStats
    {
        public double TotalPhysicalGB { get; set; }
        public double AvailablePhysicalGB { get; set; }
        public uint MemoryLoadPercent { get; set; }
    }

    public sealed class AfkApplyResult
    {
        public int ProcessId { get; set; }
        public InstanceRole Role { get; set; }
        public bool RamTrimmed { get; set; }
        public double BeforeWorkingSetMB { get; set; }
        public double AfterWorkingSetMB { get; set; }
        public bool EcoQoSApplied { get; set; }
        public bool WindowResized { get; set; }
        public bool AudioMuteUpdated { get; set; }
        public List<string> Warnings { get; set; }

        public AfkApplyResult()
        {
            Warnings = new List<string>();
        }
    }

    public sealed class RobloxHandleInfo
    {
        public int ProcessId { get; set; }
        public ulong HandleValue { get; set; }
        public string HandleHex { get { return "0x" + HandleValue.ToString("X"); } }
        public string TypeName { get; set; }
        public string FullObjectName { get; set; }
        public string ShortName { get; set; }
        public uint GrantedAccess { get; set; }
        public string AccessHex { get { return "0x" + GrantedAccess.ToString("X6"); } }
        public bool IsSingletonEvent { get; set; }
        public bool IsSingletonMutex { get; set; }
        public bool IsPrimaryTarget { get { return IsSingletonEvent || IsSingletonMutex; } }
        public string RoleTag
        {
            get
            {
                if (IsSingletonEvent) return "PRIMARY LOCK";
                if (IsSingletonMutex) return "LEGACY LOCK";
                return "INFO";
            }
        }
    }

    public sealed class RobloxInstanceInfo
    {
        public int ProcessId { get; set; }
        public string ProcessName { get; set; }
        public string Label { get; set; }
        public InstanceRole Role { get; set; }
        public bool IsAudioMuted { get; set; }
        public bool IsEcoMode { get; set; }
        public string WindowTitle { get; set; }
        public IntPtr WindowHandle { get; set; }
        public double MemoryMB { get; set; }
        public DateTime StartTime { get; set; }
        public string ExePath { get; set; }
        public string VersionTag { get; set; }
        public SingletonState State { get; set; }
        public List<RobloxHandleInfo> Handles { get; set; }

        public RobloxInstanceInfo()
        {
            Handles = new List<RobloxHandleInfo>();
            Role = InstanceRole.Main;
        }

        public string RoleDisplay
        {
            get { return Role == InstanceRole.AfkSaver ? "AFK SAVER" : "MAIN"; }
        }

        public string AudioDisplay
        {
            get { return IsAudioMuted ? "MUTE" : "ON"; }
        }

        public string MemoryDisplay
        {
            get
            {
                if (MemoryMB >= 1024.0)
                    return (MemoryMB / 1024.0).ToString("0.00") + " GB";
                return MemoryMB.ToString("0") + " MB";
            }
        }

        public string UptimeDisplay
        {
            get
            {
                if (StartTime == DateTime.MinValue) return "-";
                TimeSpan span = DateTime.Now - StartTime;
                if (span.TotalDays >= 1)
                    return string.Format("{0}d {1}h {2}m", (int)span.TotalDays, span.Hours, span.Minutes);
                if (span.TotalHours >= 1)
                    return string.Format("{0}h {1}m", (int)span.TotalHours, span.Minutes);
                if (span.TotalMinutes >= 1)
                    return string.Format("{0}m {1}s", span.Minutes, span.Seconds);
                return string.Format("{0}s", Math.Max(0, span.Seconds));
            }
        }

        public string StateDisplay
        {
            get
            {
                switch (State)
                {
                    case SingletonState.Unlocked: return "Unlocked";
                    case SingletonState.Locked: return "Locked";
                    case SingletonState.AccessDenied: return "Admin Needed";
                    default: return "Unknown";
                }
            }
        }

        public string WindowDisplay
        {
            get
            {
                if (WindowHandle == IntPtr.Zero) return "(No visible window)";
                return string.IsNullOrWhiteSpace(WindowTitle) ? "(Untitled)" : WindowTitle;
            }
        }

        public int SingletonHandleCount
        {
            get
            {
                int count = 0;
                foreach (var h in Handles)
                {
                    if (h.IsPrimaryTarget) count++;
                }
                return count;
            }
        }
    }

    public sealed class UnlockResult
    {
        public int ProcessId { get; set; }
        public int ClosedCount { get; set; }
        public List<string> ClosedHandleNames { get; set; }
        public bool AccessDenied { get; set; }
        public string ErrorMessage { get; set; }

        public UnlockResult()
        {
            ClosedHandleNames = new List<string>();
        }
    }
}
