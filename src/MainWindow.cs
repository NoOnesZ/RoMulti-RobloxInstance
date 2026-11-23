using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace RoMulti
{
    public sealed partial class MainWindow : Window
    {
        private const int PeriodicTrimIntervalSeconds = 45;

        private readonly RobloxHandleEngine _engine = new RobloxHandleEngine();

        private readonly Dictionary<int, string> _customLabels = new Dictionary<int, string>();
        private readonly Dictionary<int, InstanceRole> _instanceRoles = new Dictionary<int, InstanceRole>();
        private readonly HashSet<int> _appliedAfkPids = new HashSet<int>();
        private readonly ObservableCollection<RobloxInstanceInfo> _instances = new ObservableCollection<RobloxInstanceInfo>();
        private readonly ObservableCollection<RobloxHandleInfo> _selectedHandles = new ObservableCollection<RobloxHandleInfo>();
        private readonly ObservableCollection<string> _activityLog = new ObservableCollection<string>();

        private readonly DispatcherTimer _refreshTimer;
        private DateTime _lastPeriodicTrimUtc = DateTime.UtcNow;

        private bool _isScanning;
        private bool _isExiting;
        private int _selectedPid = -1;

        // UI Controls — Header & KPIs
        private ToggleButton _autoUnlockToggle;
        private CheckBox _includeMutexCheck;
        private CheckBox _autoAfkCheck;
        private TextBlock _kpiCountValue;
        private TextBlock _kpiCountSub;
        private TextBlock _kpiStatusValue;
        private TextBlock _kpiStatusSub;
        private TextBlock _kpiRamValue;
        private TextBlock _kpiRamSub;

        // UI Controls — Instances & Handles
        private ListView _instancesList;
        private ListView _handlesList;
        private TextBlock _handlesHeaderSub;
        private Button _toggleAfkBtn;

        // UI Controls — Footer & Tray
        private ListBox _logList;
        private Forms.NotifyIcon _trayIcon;
        private Forms.ToolStripMenuItem _trayAutoUnlockItem;

        public MainWindow()
        {
            BgCanvas = AddThemeColor("#111318", "#F1F3F6");
            BgSurface = AddThemeColor("#1A1D24", "#FFFFFF");
            BgElevated = AddThemeColor("#242933", "#F4F6F9");
            BgHover = AddThemeColor("#283A57", "#DFEAFC");
            BorderSubtle = AddThemeColor("#414856", "#C5CDD8");
            TextPrimary = AddThemeColor("#F0F2F6", "#18202D");
            TextSecondary = AddThemeColor("#BAC2D0", "#475569");
            AccentBlue = AddThemeColor("#88B7FF", "#174FAF");
            AccentBlueBg = AddThemeColor("#213555", "#E4EEFF");
            TealAccent = AddThemeColor("#71DBC4", "#08665C");
            TealBg = AddThemeColor("#17352F", "#DCF6EF");
            StatusGreen = AddThemeColor("#81DE9F", "#17633A");
            StatusGreenBg = AddThemeColor("#1B3528", "#E4F4EA");
            StatusAmber = AddThemeColor("#F7C977", "#834304");
            StatusAmberBg = AddThemeColor("#3C2D1A", "#FFF0D5");
            StatusRed = AddThemeColor("#FF9A9E", "#9D3035");
            StatusRedBg = AddThemeColor("#40262A", "#FBE7E9");
            ApplyTheme(ReadDarkTheme());

            Title = "RoMulti — Roblox Multi-Instance & AFK Saver";
            Width = 1180;
            Height = 810;
            MinWidth = 980;
            MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = BgCanvas;
            Foreground = TextPrimary;
            FontFamily = AppFont;
            FontSize = 14;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var tooltipStyle = new Style(typeof(ToolTip));
            tooltipStyle.Setters.Add(new Setter(Control.FontFamilyProperty, AppFont));
            tooltipStyle.Setters.Add(new Setter(Control.FontSizeProperty, 14.0));
            tooltipStyle.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            tooltipStyle.Setters.Add(new Setter(Control.BackgroundProperty, BgElevated));
            tooltipStyle.Setters.Add(new Setter(Control.BorderBrushProperty, BorderSubtle));
            Resources.Add(typeof(ToolTip), tooltipStyle);

            SourceInitialized += (s, e) =>
            {
                var helper = new WindowInteropHelper(this);
                NativeMethods.SetTitleBarTheme(helper.Handle, _darkTheme);
            };

            Closing += OnWindowClosing;

            BuildUi();
            InitTrayIcon();

            Log("INFO", "RoMulti ready. Mode: " + (RobloxHandleEngine.IsRunningAsAdmin() ? "Administrator" : "Standard User"));

            _refreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _refreshTimer.Tick += async (s, e) => await RefreshAsync(allowAutoActions: true);
            _refreshTimer.Start();

            Loaded += async (s, e) =>
            {
                await RefreshAsync(allowAutoActions: false);
                AfkOptimizer.TrimSelfWorkingSet();
            };
        }

        private void BuildUi()
        {
            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // KPI Strip
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // Instances & handles
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(0.25, GridUnitType.Star),
                MinHeight = 72,
                MaxHeight = 116
            }); // Activity Log

            var header = BuildHeader();
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var kpis = BuildKpiStrip();
            Grid.SetRow(kpis, 1);
            root.Children.Add(kpis);

            var workspace = BuildWorkspace();
            Grid.SetRow(workspace, 2);
            root.Children.Add(workspace);

            var logCard = BuildLogCard();
            Grid.SetRow(logCard, 3);
            root.Children.Add(logCard);

            Content = root;
        }

        private UIElement BuildHeader()
        {
            var bar = new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 11, 16, 11),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var leftStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var logoBadge = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(8),
                Background = ActionBlue,
                Margin = new Thickness(0, 0, 12, 0),
                Child = new TextBlock
                {
                    Text = "R+",
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    Foreground = System.Windows.Media.Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            leftStack.Children.Add(logoBadge);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleRow.Children.Add(new TextBlock
            {
                Text = "RoMulti",
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary
            });

            bool isAdmin = RobloxHandleEngine.IsRunningAsAdmin();
            var rolePill = new Border
            {
                CornerRadius = new CornerRadius(10),
                MinHeight = 24,
                Padding = new Thickness(9, 3, 9, 3),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = isAdmin ? StatusGreenBg : BgElevated,
                BorderBrush = isAdmin ? StatusGreen : BorderSubtle,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = isAdmin ? "Administrator" : "Standard user",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isAdmin ? StatusGreen : TextSecondary
                }
            };
            titleRow.Children.Add(rolePill);

            if (!isAdmin)
            {
                var elevateBtn = CreateCompactButton("Run as Admin", BgElevated, TextPrimary, BorderSubtle);
                elevateBtn.Margin = new Thickness(8, 0, 0, 0);
                elevateBtn.Padding = new Thickness(8, 2, 8, 2);
                elevateBtn.VerticalAlignment = VerticalAlignment.Center;
                elevateBtn.ToolTip = "Restart RoMulti as Administrator (only needed if Roblox runs elevated)";
                elevateBtn.Click += (s, e) => RestartAsAdmin();
                titleRow.Children.Add(elevateBtn);
            }

            titleStack.Children.Add(titleRow);
            titleStack.Children.Add(new TextBlock
            {
                Text = "Roblox Multi-Instance · AFK Saver",
                FontSize = 14,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 2, 0, 0)
            });
            leftStack.Children.Add(titleStack);
            Grid.SetColumn(leftStack, 0);
            grid.Children.Add(leftStack);

            var themePicker = BuildThemePicker();
            Grid.SetColumn(themePicker, 1);
            grid.Children.Add(themePicker);

            var rightStack = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };

            _includeMutexCheck = new CheckBox
            {
                Content = "Mutex",
                IsChecked = true,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 14,
                ToolTip = "Also close ROBLOX_singletonMutex alongside ROBLOX_singletonEvent"
            };
            _includeMutexCheck.Click += async (s, e) => await RefreshAsync(allowAutoActions: false);
            rightStack.Children.Add(_includeMutexCheck);

            _autoAfkCheck = new CheckBox
            {
                Content = "Auto AFK Saver",
                IsChecked = false,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                FontSize = 14,
                ToolTip = "Automatically tag newly launched Roblox clients (after Main) as AFK Saver"
            };
            rightStack.Children.Add(_autoAfkCheck);

            _autoUnlockToggle = new ToggleButton
            {
                Content = "Auto-Unlock: OFF",
                IsChecked = false,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary,
                Background = BgElevated,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            ApplyRoundedButtonTemplate(_autoUnlockToggle);
            _autoUnlockToggle.Checked += async (s, e) =>
            {
                UpdateAutoUnlockVisual(true);
                Log("INFO", "Auto-Unlock enabled — new Roblox instances will be unlocked automatically.");
                await RefreshAsync(allowAutoActions: true);
            };
            _autoUnlockToggle.Unchecked += (s, e) =>
            {
                UpdateAutoUnlockVisual(false);
                Log("INFO", "Auto-Unlock disabled.");
            };
            rightStack.Children.Add(_autoUnlockToggle);

            var unlockAllBtn = CreateActionButton("Unlock All", ActionBlue, System.Windows.Media.Brushes.White, ActionBlue);
            unlockAllBtn.Margin = new Thickness(0, 0, 8, 0);
            unlockAllBtn.Click += async (s, e) => await UnlockAllAsync();
            rightStack.Children.Add(unlockAllBtn);

            var launchBtn = CreateActionButton("Launch Roblox", BgElevated, TextPrimary, BorderSubtle);
            launchBtn.Margin = new Thickness(0, 0, 8, 0);
            launchBtn.Click += (s, e) => LaunchNewRoblox();
            rightStack.Children.Add(launchBtn);

            var trayBtn = CreateActionButton("Tray", BgElevated, TextSecondary, BorderSubtle);
            trayBtn.ToolTip = "Minimize RoMulti to System Tray (and trim RoMulti RAM)";
            trayBtn.Click += (s, e) => HideToTray();
            rightStack.Children.Add(trayBtn);

            Grid.SetRow(rightStack, 1);
            Grid.SetColumnSpan(rightStack, 2);
            grid.Children.Add(rightStack);

            bar.Child = grid;
            return bar;
        }

        private UIElement BuildKpiStrip()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });

            var c1 = BuildKpiCard("ACTIVE INSTANCES", "0", "Waiting for RobloxPlayerBeta.exe", out _kpiCountValue, out _kpiCountSub);
            Grid.SetColumn(c1, 0);
            grid.Children.Add(c1);

            var c2 = BuildKpiCard("MULTI-INSTANCE STATE", "Idle", "No active singleton lock", out _kpiStatusValue, out _kpiStatusSub);
            Grid.SetColumn(c2, 2);
            grid.Children.Add(c2);

            var c3 = BuildKpiCard("MEMORY & SYSTEM RAM", "Checking...", "Total RAM", out _kpiRamValue, out _kpiRamSub);
            Grid.SetColumn(c3, 4);
            grid.Children.Add(c3);

            return grid;
        }

        private Border BuildKpiCard(string label, string val, string sub, out TextBlock valBlock, out TextBlock subBlock)
        {
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = TextSecondary
            });

            valBlock = new TextBlock
            {
                Text = val,
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary,
                Margin = new Thickness(0, 3, 0, 2)
            };
            stack.Children.Add(valBlock);

            subBlock = new TextBlock
            {
                Text = sub,
                FontSize = 14,
                Foreground = TextSecondary
            };
            stack.Children.Add(subBlock);

            return new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 9, 14, 9),
                Child = stack
            };
        }

        private UIElement BuildWorkspace()
        {
            var workspace = new Grid { Margin = new Thickness(0, 6, 0, 10) };
            workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.2, GridUnitType.Star) });
            workspace.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0.85, GridUnitType.Star) });

            var instances = BuildInstancesCard();
            Grid.SetRow(instances, 0);
            workspace.Children.Add(instances);

            var handles = BuildHandlesCard();
            Grid.SetRow(handles, 1);
            workspace.Children.Add(handles);
            return workspace;
        }

        private UIElement BuildInstancesCard()
        {
            var card = new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(0, 0, 0, 10),
                Padding = new Thickness(14, 10, 14, 10)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Roblox Instances",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary
            });
            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var unlockSelBtn = CreateCompactButton("Unlock", StatusGreenBg, StatusGreen, StatusGreen);
            unlockSelBtn.Margin = new Thickness(0, 0, 6, 0);
            unlockSelBtn.Click += async (s, e) => await UnlockSelectedAsync();
            btnRow.Children.Add(unlockSelBtn);

            _toggleAfkBtn = CreateCompactButton("Set AFK Saver", TealBg, TealAccent, TealAccent);
            _toggleAfkBtn.Margin = new Thickness(0, 0, 6, 0);
            _toggleAfkBtn.Click += async (s, e) => await ToggleSelectedRoleAsync();
            btnRow.Children.Add(_toggleAfkBtn);

            var trimNowBtn = CreateCompactButton("Trim AFK RAM", BgElevated, TealAccent, BorderSubtle);
            trimNowBtn.Margin = new Thickness(0, 0, 6, 0);
            trimNowBtn.Click += async (s, e) => await TrimAllAfkRamAsync(isManual: true);
            btnRow.Children.Add(trimNowBtn);

            var focusBtn = CreateCompactButton("Focus", BgElevated, TextPrimary, BorderSubtle);
            focusBtn.Margin = new Thickness(0, 0, 6, 0);
            focusBtn.Click += (s, e) => FocusSelected();
            btnRow.Children.Add(focusBtn);

            var renameBtn = CreateCompactButton("Rename", BgElevated, TextPrimary, BorderSubtle);
            renameBtn.Margin = new Thickness(0, 0, 6, 0);
            renameBtn.Click += (s, e) => RenameSelected();
            btnRow.Children.Add(renameBtn);

            var tileGridBtn = CreateCompactButton("Tile Grid", BgElevated, TextPrimary, BorderSubtle);
            tileGridBtn.Margin = new Thickness(0, 0, 6, 0);
            tileGridBtn.Click += (s, e) => TileAllWindows(TileLayout.AutoGrid);
            btnRow.Children.Add(tileGridBtn);

            var tileSideBtn = CreateCompactButton("Side-by-Side", BgElevated, TextPrimary, BorderSubtle);
            tileSideBtn.Margin = new Thickness(0, 0, 6, 0);
            tileSideBtn.Click += (s, e) => TileAllWindows(TileLayout.SideBySide);
            btnRow.Children.Add(tileSideBtn);

            var killBtn = CreateCompactButton("Kill", StatusRedBg, StatusRed, StatusRed);
            killBtn.Margin = new Thickness(0, 0, 6, 0);
            killBtn.Click += async (s, e) => await KillSelectedAsync();
            btnRow.Children.Add(killBtn);

            var killAllBtn = CreateCompactButton("Kill All", BgElevated, StatusRed, BorderSubtle);
            killAllBtn.Click += async (s, e) => await KillAllAsync();
            btnRow.Children.Add(killAllBtn);

            Grid.SetColumn(btnRow, 1);
            topGrid.Children.Add(btnRow);

            Grid.SetRow(topGrid, 0);
            grid.Children.Add(topGrid);

            _instancesList = CreateThemedListView();
            _instancesList.ItemsSource = _instances;
            _instancesList.SelectionChanged += (s, e) => UpdateHandlesPaneFromSelection();
            _instancesList.MouseDoubleClick += (s, e) => FocusSelected();

            var gv = new GridView();
            gv.Columns.Add(CreateTextColumn("Account Label", "Label", 120, bold: true));
            gv.Columns.Add(CreateTextColumn("PID", "ProcessId", 66, bold: false));
            gv.Columns.Add(CreateRoleColumn("Role", 100));
            gv.Columns.Add(CreateStatusColumn("Singleton", 124));
            gv.Columns.Add(CreateTextColumn("RAM (WS)", "MemoryDisplay", 80, bold: true));
            gv.Columns.Add(CreateAudioColumn("Audio", 62));
            gv.Columns.Add(CreateTextColumn("Window Title", "WindowDisplay", 185, bold: false));
            gv.Columns.Add(CreateTextColumn("Uptime", "UptimeDisplay", 80, bold: false));
            _instancesList.View = gv;

            Grid.SetRow(_instancesList, 1);
            grid.Children.Add(_instancesList);

            card.Child = grid;
            return card;
        }

        private UIElement BuildHandlesCard()
        {
            var card = new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var topGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = "Roblox Handles Inspector",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary
            });

            _handlesHeaderSub = new TextBlock
            {
                Text = "Filtered strictly to Event / Mutant handles belonging to the selected Roblox process.",
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = TextSecondary
            };
            titleStack.Children.Add(_handlesHeaderSub);
            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var closeHandleBtn = CreateCompactButton("Close Selected Handle", StatusAmberBg, StatusAmber, StatusAmber);
            closeHandleBtn.Margin = new Thickness(0, 0, 6, 0);
            closeHandleBtn.Click += async (s, e) => await CloseSelectedHandleAsync();
            rightBtns.Children.Add(closeHandleBtn);

            var refreshBtn = CreateCompactButton("Refresh Now", BgElevated, TextPrimary, BorderSubtle);
            refreshBtn.Click += async (s, e) => await RefreshAsync(allowAutoActions: false);
            rightBtns.Children.Add(refreshBtn);

            Grid.SetColumn(rightBtns, 1);
            topGrid.Children.Add(rightBtns);

            Grid.SetRow(topGrid, 0);
            grid.Children.Add(topGrid);

            _handlesList = CreateThemedListView();
            _handlesList.ItemsSource = _selectedHandles;

            var gv = new GridView();
            gv.Columns.Add(CreateTextColumn("Handle", "HandleHex", 85, bold: true));
            gv.Columns.Add(CreateTextColumn("Type", "TypeName", 75, bold: false));
            gv.Columns.Add(CreateHandleRoleColumn("Lock Type", 134));
            gv.Columns.Add(CreateTextColumn("Object Name", "ShortName", 210, bold: true));
            gv.Columns.Add(CreateTextColumn("Full NT Path", "FullObjectName", 300, bold: false));
            gv.Columns.Add(CreateTextColumn("Access", "AccessHex", 90, bold: false));
            _handlesList.View = gv;

            Grid.SetRow(_handlesList, 1);
            grid.Children.Add(_handlesList);

            card.Child = grid;
            return card;
        }

        private UIElement BuildLogCard()
        {
            var card = new Border
            {
                Background = BgSurface,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 7, 12, 7)
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new TextBlock
            {
                Text = "ACTIVITY LOG",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = TextSecondary,
                Margin = new Thickness(0, 0, 0, 3)
            };
            Grid.SetRow(header, 0);
            grid.Children.Add(header);

            _logList = new ListBox
            {
                ItemsSource = _activityLog,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = TextSecondary,
                FontSize = 14
            };
            Grid.SetRow(_logList, 1);
            grid.Children.Add(_logList);

            card.Child = grid;
            return card;
        }

        private async Task RefreshAsync(bool allowAutoActions)
        {
            if (_isScanning) return;
            _isScanning = true;

            try
            {
                bool includeMutex = _includeMutexCheck.IsChecked == true;
                bool autoUnlock = allowAutoActions && (_autoUnlockToggle.IsChecked == true);
                bool autoAfkNew = allowAutoActions && (_autoAfkCheck.IsChecked == true);

                var labelCopy = new Dictionary<int, string>(_customLabels);
                var roleCopy = new Dictionary<int, InstanceRole>(_instanceRoles);

                var scanned = await Task.Run(() => _engine.ScanRobloxInstances(labelCopy, roleCopy, includeMutex));
                bool needsRescan = false;

                bool hasMain = scanned.Any(x => _instanceRoles.ContainsKey(x.ProcessId) && _instanceRoles[x.ProcessId] == InstanceRole.Main);
                foreach (var inst in scanned)
                {
                    if (!_instanceRoles.ContainsKey(inst.ProcessId))
                    {
                        if (!hasMain)
                        {
                            _instanceRoles[inst.ProcessId] = InstanceRole.Main;
                            inst.Role = InstanceRole.Main;
                            hasMain = true;
                        }
                        else if (autoAfkNew)
                        {
                            _instanceRoles[inst.ProcessId] = InstanceRole.AfkSaver;
                            inst.Role = InstanceRole.AfkSaver;
                        }
                        else
                        {
                            _instanceRoles[inst.ProcessId] = InstanceRole.Main;
                            inst.Role = InstanceRole.Main;
                        }
                    }
                    else
                    {
                        inst.Role = _instanceRoles[inst.ProcessId];
                    }
                }

                foreach (var inst in scanned.Where(x => x.Role == InstanceRole.AfkSaver).ToList())
                {
                    if (!_appliedAfkPids.Contains(inst.ProcessId))
                    {
                        var applyRes = await Task.Run(() =>
                            AfkOptimizer.ApplyRoleProfile(inst.ProcessId, inst.WindowHandle, InstanceRole.AfkSaver, resizeWindow: true));
                        _appliedAfkPids.Add(inst.ProcessId);
                        needsRescan = true;
                        LogAfkApplyResult(inst.Label, applyRes);
                    }
                    else if (!inst.IsAudioMuted)
                    {
                        string muteErr = null;
                        bool mutedNow = await Task.Run(() => AfkOptimizer.SetProcessAudioMute(inst.ProcessId, true, out muteErr));
                        if (mutedNow)
                        {
                            inst.IsAudioMuted = true;
                            Log("OK", "Muted late audio session for AFK instance PID " + inst.ProcessId + ".");
                        }
                    }
                }

                if (autoUnlock)
                {
                    var lockedPids = scanned
                        .Where(i => i.State == SingletonState.Locked)
                        .Select(i => i.ProcessId)
                        .ToList();

                    foreach (int pid in lockedPids)
                    {
                        UnlockResult res = await Task.Run(() => _engine.UnlockInstance(pid, true, includeMutex));
                        if (res.ClosedCount > 0)
                        {
                            needsRescan = true;
                            Log("OK", string.Format("Auto-unlocked PID {0}: closed {1}", pid, string.Join(", ", res.ClosedHandleNames)));
                            if (!IsVisible && _trayIcon != null)
                            {
                                _trayIcon.ShowBalloonTip(2000, "RoMulti — Unlocked", "Unlocked Roblox PID " + pid + " for multi-instance.", Forms.ToolTipIcon.Info);
                            }
                        }
                        else if (!string.IsNullOrEmpty(res.ErrorMessage))
                        {
                            Log("WARN", res.ErrorMessage);
                        }
                    }
                }

                if (allowAutoActions && (DateTime.UtcNow - _lastPeriodicTrimUtc).TotalSeconds >= PeriodicTrimIntervalSeconds)
                {
                    _lastPeriodicTrimUtc = DateTime.UtcNow;
                    var afkTargets = scanned.Where(x => x.Role == InstanceRole.AfkSaver).ToList();
                    if (afkTargets.Count > 0)
                    {
                        double savedMb = 0;
                        int trimmedCount = 0;
                        foreach (var inst in afkTargets)
                        {
                            double b = 0, a = 0;
                            string err = null;
                            bool ok = await Task.Run(() => AfkOptimizer.TrimWorkingSet(inst.ProcessId, out b, out a, out err));
                            if (ok)
                            {
                                trimmedCount++;
                                savedMb += Math.Max(0, b - a);
                            }
                        }
                        if (trimmedCount > 0)
                        {
                            needsRescan = true;
                            Log("INFO", string.Format("Periodic AFK RAM trim: {0} instance(s) trimmed ({1:0} MB freed).", trimmedCount, savedMb));
                        }
                    }

                    if (!IsVisible)
                        AfkOptimizer.TrimSelfWorkingSet();
                }

                if (needsRescan)
                {
                    var updatedRoles = new Dictionary<int, InstanceRole>(_instanceRoles);
                    scanned = await Task.Run(() => _engine.ScanRobloxInstances(labelCopy, updatedRoles, includeMutex));
                }

                var sysMem = await Task.Run(() => AfkOptimizer.GetSystemMemoryStats());
                ApplyScanToUi(scanned, sysMem);
            }
            catch (Exception ex)
            {
                Log("ERR", "Scan error: " + ex.Message);
            }
            finally
            {
                _isScanning = false;
            }
        }

        private void ApplyScanToUi(List<RobloxInstanceInfo> scanned, SystemMemoryStats sysMem)
        {
            int previousPid = _selectedPid;
            var curSel = _instancesList.SelectedItem as RobloxInstanceInfo;
            if (curSel != null)
                previousPid = curSel.ProcessId;

            var activePids = new HashSet<int>(scanned.Select(x => x.ProcessId));
            foreach (int deadPid in _customLabels.Keys.Where(k => !activePids.Contains(k)).ToList())
                _customLabels.Remove(deadPid);
            foreach (int deadPid in _instanceRoles.Keys.Where(k => !activePids.Contains(k)).ToList())
                _instanceRoles.Remove(deadPid);
            foreach (int deadPid in _appliedAfkPids.Where(k => !activePids.Contains(k)).ToList())
                _appliedAfkPids.Remove(deadPid);

            _instances.Clear();
            foreach (var inst in scanned)
                _instances.Add(inst);

            RobloxInstanceInfo toSelect = scanned.FirstOrDefault(x => x.ProcessId == previousPid) ?? scanned.FirstOrDefault();
            if (toSelect != null)
            {
                _selectedPid = toSelect.ProcessId;
                _instancesList.SelectedItem = toSelect;
            }
            else
            {
                _selectedPid = -1;
            }

            UpdateHandlesPaneFromSelection();
            UpdateKpis(scanned, sysMem);
        }

        private void UpdateHandlesPaneFromSelection()
        {
            _selectedHandles.Clear();
            var selected = _instancesList.SelectedItem as RobloxInstanceInfo;
            if (selected == null)
            {
                _handlesHeaderSub.Text = "No Roblox instance selected.";
                _toggleAfkBtn.Content = "Set AFK Saver";
                return;
            }

            _selectedPid = selected.ProcessId;
            if (selected.Role == InstanceRole.AfkSaver)
            {
                _toggleAfkBtn.Content = "Set as Main";
                _toggleAfkBtn.Background = AccentBlueBg;
                _toggleAfkBtn.Foreground = AccentBlue;
                _toggleAfkBtn.BorderBrush = AccentBlue;
            }
            else
            {
                _toggleAfkBtn.Content = "Set AFK Saver";
                _toggleAfkBtn.Background = TealBg;
                _toggleAfkBtn.Foreground = TealAccent;
                _toggleAfkBtn.BorderBrush = TealAccent;
            }

            if (selected.State == SingletonState.AccessDenied)
            {
                _handlesHeaderSub.Text = "Access denied for PID " + selected.ProcessId + " — click 'Run as Admin' in the top bar.";
                return;
            }

            foreach (var h in selected.Handles)
                _selectedHandles.Add(h);

            _handlesHeaderSub.Text = string.Format(
                "PID {0} ({1} · {2}) — {3} Roblox handle(s) found, {4} singleton lock(s) active.",
                selected.ProcessId,
                selected.Label,
                selected.RoleDisplay,
                selected.Handles.Count,
                selected.SingletonHandleCount);
        }

        private void UpdateKpis(List<RobloxInstanceInfo> scanned, SystemMemoryStats sysMem)
        {
            int total = scanned.Count;
            int mainCount = scanned.Count(x => x.Role == InstanceRole.Main);
            int afkCount = scanned.Count(x => x.Role == InstanceRole.AfkSaver);
            int locked = scanned.Count(x => x.State == SingletonState.Locked);
            int denied = scanned.Count(x => x.State == SingletonState.AccessDenied);
            double totalMb = scanned.Sum(x => x.MemoryMB);

            _kpiCountValue.Text = total.ToString();
            _kpiCountSub.Text = total == 0
                ? "Waiting for RobloxPlayerBeta.exe"
                : string.Format("{0} Main · {1} AFK Saver", mainCount, afkCount);

            if (total == 0)
            {
                _kpiStatusValue.Text = "Idle";
                _kpiStatusValue.Foreground = TextSecondary;
                _kpiStatusSub.Text = "Launch Roblox to begin";
            }
            else if (denied > 0)
            {
                _kpiStatusValue.Text = "Admin Needed";
                _kpiStatusValue.Foreground = StatusRed;
                _kpiStatusSub.Text = denied + " instance(s) require Administrator rights";
            }
            else if (locked > 0)
            {
                _kpiStatusValue.Text = locked + " Locked";
                _kpiStatusValue.Foreground = StatusAmber;
                _kpiStatusSub.Text = "Click 'Unlock All' or enable Auto-Unlock";
            }
            else
            {
                _kpiStatusValue.Text = "Ready";
                _kpiStatusValue.Foreground = StatusGreen;
                _kpiStatusSub.Text = "All instances unlocked — safe to launch next account";
            }

            string robloxRamStr = totalMb >= 1024.0
                ? (totalMb / 1024.0).ToString("0.00") + " GB"
                : totalMb.ToString("0") + " MB";

            _kpiRamValue.Text = string.Format("Free: {0:0.00} GB", sysMem.AvailablePhysicalGB);
            if (sysMem.AvailablePhysicalGB < 1.2)
                _kpiRamValue.Foreground = StatusRed;
            else if (sysMem.AvailablePhysicalGB < 2.5)
                _kpiRamValue.Foreground = StatusAmber;
            else
                _kpiRamValue.Foreground = StatusGreen;

            _kpiRamSub.Text = string.Format(
                "Roblox {0} · Total {1:0.0} GB · {2}% used",
                robloxRamStr,
                sysMem.TotalPhysicalGB,
                sysMem.MemoryLoadPercent);
        }

        private async Task ToggleSelectedRoleAsync()
        {
            var selected = _instancesList.SelectedItem as RobloxInstanceInfo;
            if (selected == null)
            {
                Log("WARN", "Select a Roblox instance first.");
                return;
            }

            InstanceRole newRole = (selected.Role == InstanceRole.AfkSaver)
                ? InstanceRole.Main
                : InstanceRole.AfkSaver;

            _instanceRoles[selected.ProcessId] = newRole;
            if (newRole == InstanceRole.Main)
                _appliedAfkPids.Remove(selected.ProcessId);
            else
                _appliedAfkPids.Add(selected.ProcessId);

            var res = await Task.Run(() =>
                AfkOptimizer.ApplyRoleProfile(selected.ProcessId, selected.WindowHandle, newRole, resizeWindow: (newRole == InstanceRole.AfkSaver)));

            LogAfkApplyResult(selected.Label, res);
            await RefreshAsync(allowAutoActions: false);
        }

        private void LogAfkApplyResult(string label, AfkApplyResult res)
        {
            if (res.Role == InstanceRole.AfkSaver)
            {
                string ramNote = res.RamTrimmed
                    ? string.Format("RAM {0:0}->{1:0} MB", res.BeforeWorkingSetMB, res.AfterWorkingSetMB)
                    : "RAM unchanged";
                Log("OK", string.Format(
                    "AFK Saver ON for {0} (PID {1}): {2}, EcoQoS={3}, Compact={4}, Mute={5}",
                    label, res.ProcessId, ramNote,
                    res.EcoQoSApplied ? "ON" : "no",
                    res.WindowResized ? "520x320" : "skip",
                    res.AudioMuteUpdated ? "ON" : "pending"));
            }
            else
            {
                Log("OK", string.Format(
                    "Restored MAIN profile for {0} (PID {1}): Normal priority, EcoQoS=OFF, Audio={2}",
                    label, res.ProcessId, res.AudioMuteUpdated ? "UNMUTED" : "unchanged"));
            }

            foreach (string w in res.Warnings)
            {
                if (w.IndexOf("No active audio session", StringComparison.OrdinalIgnoreCase) < 0)
                    Log("WARN", w);
            }
        }

        private async Task TrimAllAfkRamAsync(bool isManual)
        {
            var afkTargets = _instances.Where(x => x.Role == InstanceRole.AfkSaver).ToList();
            if (afkTargets.Count == 0)
            {
                if (isManual)
                    Log("INFO", "No AFK Saver instances to trim. Select a scout account and click 'Set AFK Saver' first.");
                return;
            }

            double totalBefore = 0;
            double totalAfter = 0;
            int okCount = 0;

            foreach (var inst in afkTargets)
            {
                double b = 0, a = 0;
                string err = null;
                bool ok = await Task.Run(() => AfkOptimizer.TrimWorkingSet(inst.ProcessId, out b, out a, out err));
                if (ok)
                {
                    okCount++;
                    totalBefore += b;
                    totalAfter += a;
                }
                else if (!string.IsNullOrEmpty(err))
                {
                    Log("WARN", err);
                }
            }

            if (okCount > 0)
            {
                Log("OK", string.Format(
                    "Trimmed {0} AFK instance(s): {1:0} MB -> {2:0} MB (freed ~{3:0} MB working set).",
                    okCount, totalBefore, totalAfter, Math.Max(0, totalBefore - totalAfter)));
                await RefreshAsync(allowAutoActions: false);
            }
        }

        private async Task UnlockAllAsync()
        {
            bool includeMutex = _includeMutexCheck.IsChecked == true;
            var targets = _instances.Where(x => x.State == SingletonState.Locked || x.SingletonHandleCount > 0).ToList();

            if (targets.Count == 0)
            {
                Log("INFO", "No locked Roblox instances to unlock.");
                return;
            }

            int totalClosed = 0;
            foreach (var inst in targets)
            {
                var res = await Task.Run(() => _engine.UnlockInstance(inst.ProcessId, true, includeMutex));
                if (res.ClosedCount > 0)
                {
                    totalClosed += res.ClosedCount;
                    Log("OK", string.Format("Unlocked PID {0} ({1}): closed {2}", inst.ProcessId, inst.Label, string.Join(", ", res.ClosedHandleNames)));
                }
                else if (!string.IsNullOrEmpty(res.ErrorMessage))
                {
                    Log("ERR", res.ErrorMessage);
                }
            }

            if (totalClosed > 0)
                await RefreshAsync(allowAutoActions: false);
        }

        private async Task UnlockSelectedAsync()
        {
            var selected = _instancesList.SelectedItem as RobloxInstanceInfo;
            if (selected == null)
            {
                Log("WARN", "Select a Roblox instance first.");
                return;
            }

            bool includeMutex = _includeMutexCheck.IsChecked == true;
            var res = await Task.Run(() => _engine.UnlockInstance(selected.ProcessId, true, includeMutex));
            if (res.ClosedCount > 0)
                Log("OK", string.Format("Unlocked PID {0}: closed {1}", selected.ProcessId, string.Join(", ", res.ClosedHandleNames)));
            else if (!string.IsNullOrEmpty(res.ErrorMessage))
                Log("ERR", res.ErrorMessage);
            else
                Log("INFO", "PID " + selected.ProcessId + " has no active singleton locks.");

            await RefreshAsync(allowAutoActions: false);
        }

        private async Task CloseSelectedHandleAsync()
        {
            var h = _handlesList.SelectedItem as RobloxHandleInfo;
            if (h == null)
            {
                Log("WARN", "Select a handle in the lower pane first.");
                return;
            }

            if (!h.IsPrimaryTarget)
            {
                var confirm = MessageBox.Show(
                    "'" + h.ShortName + "' is not a primary Roblox singleton lock.\nClosing non-singleton handles can destabilize the client.\n\nClose this handle anyway?",
                    "Confirm Close Handle",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            string err = null;
            bool ok = await Task.Run(() => _engine.CloseSpecificHandle(h.ProcessId, h.HandleValue, h.FullObjectName, out err));
            if (ok)
                Log("OK", string.Format("Closed handle {0} ({1}) on PID {2}", h.HandleHex, h.ShortName, h.ProcessId));
            else
                Log("ERR", err ?? "Failed to close handle.");

            await RefreshAsync(allowAutoActions: false);
        }

        private void FocusSelected()
        {
            var selected = _instancesList.SelectedItem as RobloxInstanceInfo;
            if (selected == null) return;
            if (selected.WindowHandle == IntPtr.Zero)
            {
                Log("WARN", "PID " + selected.ProcessId + " has no visible top-level window yet.");
                return;
            }

            RobloxHandleEngine.FocusWindow(selected.WindowHandle);
            Log("INFO", "Focused window for " + selected.Label + " (PID " + selected.ProcessId + ").");
        }

        private void RenameSelected()
        {
            var selected = _instancesList.SelectedItem as RobloxInstanceInfo;
            if (selected == null)
            {
                Log("WARN", "Select a Roblox instance to rename.");
                return;
            }

            string newLabel = PromptRenameDialog(selected.Label, selected.WindowTitle);
            if (string.IsNullOrWhiteSpace(newLabel)) return;

            _customLabels[selected.ProcessId] = newLabel.Trim();
            if (selected.WindowHandle != IntPtr.Zero)
                RobloxHandleEngine.RenameWindow(selected.WindowHandle, "Roblox — " + newLabel.Trim());

            Log("OK", string.Format("Renamed PID {0} to '{1}'.", selected.ProcessId, newLabel.Trim()));
            var refreshTask = RefreshAsync(allowAutoActions: false);
        }

        private void TileAllWindows(TileLayout layout)
        {
            var hwnds = _instances.Where(x => x.WindowHandle != IntPtr.Zero).Select(x => x.WindowHandle).ToList();
            if (hwnds.Count == 0)
            {
                Log("WARN", "No visible Roblox windows found to arrange.");
                return;
            }

            int moved = RobloxHandleEngine.TileWindows(hwnds, layout);
            Log("OK", string.Format("Arranged {0} Roblox window(s) using {1} layout.", moved, layout));
        }

        private async Task KillSelectedAsync()
        {
            var selected = _instancesList.SelectedItem as RobloxInstanceInfo;
            if (selected == null) return;

            var res = MessageBox.Show(
                string.Format("Terminate {0} (PID {1})?", selected.Label, selected.ProcessId),
                "Kill Roblox Instance",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (res != MessageBoxResult.Yes) return;

            string err;
            if (RobloxHandleEngine.KillProcess(selected.ProcessId, out err))
                Log("WARN", string.Format("Terminated {0} (PID {1}).", selected.Label, selected.ProcessId));
            else
                Log("ERR", "Failed to kill PID " + selected.ProcessId + ": " + err);

            await RefreshAsync(allowAutoActions: false);
        }

        private async Task KillAllAsync()
        {
            if (_instances.Count == 0) return;

            var res = MessageBox.Show(
                string.Format("Terminate ALL {0} running Roblox instance(s)?", _instances.Count),
                "Kill All Roblox Instances",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;

            int killed = 0;
            foreach (var inst in _instances.ToList())
            {
                string err;
                if (RobloxHandleEngine.KillProcess(inst.ProcessId, out err))
                    killed++;
            }

            Log("WARN", "Terminated " + killed + " Roblox instance(s).");
            await RefreshAsync(allowAutoActions: false);
        }

        private void LaunchNewRoblox()
        {
            if (_instances.Any(x => x.State == SingletonState.Locked))
            {
                var r = MessageBox.Show(
                    "One or more running Roblox instances still hold ROBLOX_singletonEvent.\nUnlock them first before launching a new client?",
                    "Locked Instance Detected",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (r == MessageBoxResult.Cancel) return;
                if (r == MessageBoxResult.Yes)
                {
                    bool includeMutex = _includeMutexCheck.IsChecked == true;
                    foreach (var inst in _instances.Where(x => x.State == SingletonState.Locked).ToList())
                        _engine.UnlockInstance(inst.ProcessId, true, includeMutex);
                    Log("OK", "Unlocked locked instance(s) prior to launch.");
                }
            }

            string msg;
            bool ok = RobloxHandleEngine.LaunchNewRobloxClient(out msg);
            Log(ok ? "OK" : "ERR", msg);
        }

        private void RestartAsAdmin()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process.Start(psi);
                _isExiting = true;
                Close();
            }
            catch (Exception ex)
            {
                Log("WARN", "Admin elevation cancelled or failed: " + ex.Message);
            }
        }

        private string PromptRenameDialog(string currentLabel, string currentWindowTitle)
        {
            var dlg = new Window
            {
                Title = "Rename Roblox Instance",
                Width = 380,
                Height = 185,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                Background = BgSurface,
                Foreground = TextPrimary,
                FontFamily = AppFont,
                FontSize = 14
            };

            dlg.SourceInitialized += (s, e) =>
            {
                NativeMethods.SetTitleBarTheme(new WindowInteropHelper(dlg).Handle, _darkTheme);
            };

            var stack = new StackPanel { Margin = new Thickness(18) };
            stack.Children.Add(new TextBlock
            {
                Text = "Account / Instance Label",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimary,
                Margin = new Thickness(0, 0, 0, 6)
            });

            var tb = CreateThemedTextBox(currentLabel ?? "");
            stack.Children.Add(tb);

            var btnBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0)
            };

            string result = null;
            var cancelBtn = CreateCompactButton("Cancel", BgElevated, TextSecondary, BorderSubtle);
            cancelBtn.Margin = new Thickness(0, 0, 8, 0);
            cancelBtn.Click += (s, e) => dlg.Close();
            btnBar.Children.Add(cancelBtn);

            var okBtn = CreateCompactButton("Save Title", ActionBlue, System.Windows.Media.Brushes.White, ActionBlue);
            okBtn.IsDefault = true;
            okBtn.Click += (s, e) =>
            {
                result = tb.Text;
                dlg.Close();
            };
            btnBar.Children.Add(okBtn);

            stack.Children.Add(btnBar);
            dlg.Content = stack;
            dlg.Loaded += (s, e) => { tb.Focus(); tb.SelectAll(); };
            dlg.ShowDialog();
            return result;
        }

        private void InitTrayIcon()
        {
            _trayIcon = new Forms.NotifyIcon
            {
                Icon = CreateAppIcon(),
                Text = "RoMulti — Roblox Multi-Instance & AFK Saver",
                Visible = true
            };

            var menu = new Forms.ContextMenuStrip { Font = new Font(AppFontName, 11) };
            menu.Items.Add("Open RoMulti", null, (s, e) => Dispatcher.Invoke(RestoreFromTray));

            _trayAutoUnlockItem = new Forms.ToolStripMenuItem("Auto-Unlock: OFF");
            _trayAutoUnlockItem.Click += (s, e) => Dispatcher.Invoke(() =>
            {
                _autoUnlockToggle.IsChecked = !(_autoUnlockToggle.IsChecked == true);
            });
            menu.Items.Add(_trayAutoUnlockItem);

            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Unlock All Now", null, (s, e) => Dispatcher.Invoke(async () => await UnlockAllAsync()));
            menu.Items.Add("Trim AFK RAM Now", null, (s, e) => Dispatcher.Invoke(async () => await TrimAllAfkRamAsync(isManual: true)));
            menu.Items.Add("Tile Roblox Windows", null, (s, e) => Dispatcher.Invoke(() => TileAllWindows(TileLayout.AutoGrid)));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Exit RoMulti", null, (s, e) => Dispatcher.Invoke(() =>
            {
                _isExiting = true;
                Close();
            }));

            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (s, e) => Dispatcher.Invoke(RestoreFromTray);
        }

        private void UpdateAutoUnlockVisual(bool enabled)
        {
            _autoUnlockToggle.Content = enabled ? "Auto-Unlock: ON" : "Auto-Unlock: OFF";
            _autoUnlockToggle.Background = enabled ? StatusGreenBg : BgElevated;
            _autoUnlockToggle.Foreground = enabled ? StatusGreen : TextPrimary;
            _autoUnlockToggle.BorderBrush = enabled ? StatusGreen : BorderSubtle;

            if (_trayAutoUnlockItem != null)
                _trayAutoUnlockItem.Text = enabled ? "Auto-Unlock: ON" : "Auto-Unlock: OFF";
        }

        private void HideToTray()
        {
            Hide();
            AfkOptimizer.TrimSelfWorkingSet();
            if (_trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(1500, "RoMulti", "Running in System Tray. Double-click icon to reopen.", Forms.ToolTipIcon.Info);
            }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void OnWindowClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            bool keepInTray = !_isExiting && (
                _autoUnlockToggle.IsChecked == true ||
                _instances.Any(x => x.Role == InstanceRole.AfkSaver));

            if (keepInTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            _refreshTimer.Stop();
            _engine.Dispose();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                var icon = _trayIcon.Icon;
                var menu = _trayIcon.ContextMenuStrip;
                _trayIcon.Dispose();
                if (menu != null)
                {
                    var menuFont = menu.Font;
                    menu.Dispose();
                    menuFont.Dispose();
                }
                if (icon != null) icon.Dispose();
                _trayIcon = null;
            }
        }

        private void Log(string level, string message)
        {
            string entry = string.Format("[{0:HH:mm:ss}] [{1}] {2}", DateTime.Now, level, message);
            _activityLog.Insert(0, entry);
            while (_activityLog.Count > 150)
                _activityLog.RemoveAt(_activityLog.Count - 1);
        }

        // --- Styling & Helper Controls ---

        private TextBox CreateThemedTextBox(string text)
        {
            return new TextBox
            {
                Text = text ?? "",
                Background = BgCanvas,
                Foreground = TextPrimary,
                CaretBrush = TextPrimary,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 5, 8, 5),
                FontSize = 14
            };
        }

        private Button CreateActionButton(string text, System.Windows.Media.Brush bg, System.Windows.Media.Brush fg, System.Windows.Media.Brush border)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(12, 6, 12, 6),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Background = bg,
                Foreground = fg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            ApplyRoundedButtonTemplate(btn);
            return btn;
        }

        private Button CreateCompactButton(string text, System.Windows.Media.Brush bg, System.Windows.Media.Brush fg, System.Windows.Media.Brush border)
        {
            var btn = new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 14,
                FontWeight = FontWeights.Normal,
                Background = bg,
                Foreground = fg,
                BorderBrush = border,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand
            };
            ApplyRoundedButtonTemplate(btn);
            return btn;
        }

        private void ApplyRoundedButtonTemplate(ButtonBase btn)
        {
            var template = new ControlTemplate(typeof(ButtonBase));
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.Name = "bd";
            borderFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            borderFactory.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            borderFactory.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            borderFactory.AppendChild(cp);

            template.VisualTree = borderFactory;

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(UIElement.OpacityProperty, 0.88, "bd"));
            template.Triggers.Add(hoverTrigger);

            var focusTrigger = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, AccentBlue, "bd"));
            focusTrigger.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(2), "bd"));
            template.Triggers.Add(focusTrigger);

            btn.Template = template;
        }

        private ListView CreateThemedListView()
        {
            var lv = new ListView
            {
                Background = BgCanvas,
                Foreground = TextPrimary,
                BorderBrush = BorderSubtle,
                BorderThickness = new Thickness(1),
                FontSize = 14,
                SelectionMode = SelectionMode.Single
            };

            var headerStyle = new Style(typeof(GridViewColumnHeader));
            headerStyle.Setters.Add(new Setter(Control.BackgroundProperty, BgElevated));
            headerStyle.Setters.Add(new Setter(Control.ForegroundProperty, TextSecondary));
            headerStyle.Setters.Add(new Setter(Control.BorderBrushProperty, BorderSubtle));
            headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 1, 1)));
            headerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 5, 8, 5)));
            headerStyle.Setters.Add(new Setter(Control.FontFamilyProperty, AppFont));
            headerStyle.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            headerStyle.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Bold));
            headerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            lv.Resources.Add(typeof(GridViewColumnHeader), headerStyle);

            var itemStyle = new Style(typeof(ListViewItem));
            itemStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 4, 4, 4)));
            itemStyle.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            itemStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
            itemStyle.Setters.Add(new Setter(Control.BorderBrushProperty, BorderSubtle));
            itemStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, BgElevated));
            itemStyle.Triggers.Add(hoverTrigger);

            var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, BgHover));
            selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, TextPrimary));
            itemStyle.Triggers.Add(selTrigger);

            lv.ItemContainerStyle = itemStyle;
            return lv;
        }

        private static GridViewColumn CreateTextColumn(string header, string bindingPath, double width, bool bold)
        {
            var col = new GridViewColumn { Header = header, Width = width };
            var tpl = new DataTemplate();
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(bindingPath));
            tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            if (bold) tb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            tpl.VisualTree = tb;
            col.CellTemplate = tpl;
            return col;
        }

        private GridViewColumn CreateRoleColumn(string header, double width)
        {
            var col = new GridViewColumn { Header = header, Width = width };
            var tpl = new DataTemplate();

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
            border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var roleForeground = new CellBrushConverter(value => Equals(value, InstanceRole.AfkSaver) ? TealAccent : AccentBlue);
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Role")
            {
                Converter = new CellBrushConverter(value => Equals(value, InstanceRole.AfkSaver) ? TealBg : AccentBlueBg)
            });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("Role") { Converter = roleForeground });

            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RoleDisplay"));
            tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Role") { Converter = roleForeground });
            tb.SetValue(TextBlock.FontSizeProperty, 12.5);
            tb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);

            border.AppendChild(tb);
            tpl.VisualTree = border;
            col.CellTemplate = tpl;
            return col;
        }

        private GridViewColumn CreateAudioColumn(string header, double width)
        {
            var col = new GridViewColumn { Header = header, Width = width };
            var tpl = new DataTemplate();

            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("AudioDisplay"));
            tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("IsAudioMuted")
            {
                Converter = new CellBrushConverter(value => Equals(value, true) ? TextSecondary : StatusGreen)
            });
            tb.SetValue(TextBlock.FontSizeProperty, 13.0);
            tb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            tb.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

            tpl.VisualTree = tb;
            col.CellTemplate = tpl;
            return col;
        }

        private GridViewColumn CreateStatusColumn(string header, double width)
        {
            var col = new GridViewColumn { Header = header, Width = width };
            var tpl = new DataTemplate();

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.PaddingProperty, new Thickness(8, 2, 8, 2));
            border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            var statusForeground = new CellBrushConverter(value => GetSingletonBrush(value, foreground: true));
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("State")
            {
                Converter = new CellBrushConverter(value => GetSingletonBrush(value, foreground: false))
            });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("State") { Converter = statusForeground });

            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("StateDisplay"));
            tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("State") { Converter = statusForeground });
            tb.SetValue(TextBlock.FontSizeProperty, 12.5);
            tb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);

            border.AppendChild(tb);
            tpl.VisualTree = border;
            col.CellTemplate = tpl;
            return col;
        }

        private GridViewColumn CreateHandleRoleColumn(string header, double width)
        {
            var col = new GridViewColumn { Header = header, Width = width };
            var tpl = new DataTemplate();

            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(Border.PaddingProperty, new Thickness(7, 2, 7, 2));
            border.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("RoleTag")
            {
                Converter = new CellBrushConverter(value => GetHandleRoleBrush(value as string, foreground: false))
            });

            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("RoleTag"));
            tb.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("RoleTag")
            {
                Converter = new CellBrushConverter(value => GetHandleRoleBrush(value as string, foreground: true))
            });
            tb.SetValue(TextBlock.FontSizeProperty, 12.5);
            tb.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);

            border.AppendChild(tb);
            tpl.VisualTree = border;
            col.CellTemplate = tpl;
            return col;
        }

        private static Icon CreateAppIcon()
        {
            using (var bmp = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(System.Drawing.Color.FromArgb(59, 130, 246)))
                {
                    g.FillRectangle(bg, 2, 2, 28, 28);
                }
                using (var font = new Font(AppFontName, 13, System.Drawing.FontStyle.Bold))
                using (var fg = new SolidBrush(System.Drawing.Color.White))
                {
                    g.DrawString("R+", font, fg, 2, 5);
                }
                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (var icon = System.Drawing.Icon.FromHandle(hIcon))
                        return (Icon)icon.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(hIcon);
                }
            }
        }

        private SolidColorBrush GetSingletonBrush(object value, bool foreground)
        {
            if (value is SingletonState)
            {
                switch ((SingletonState)value)
                {
                    case SingletonState.Unlocked: return foreground ? StatusGreen : StatusGreenBg;
                    case SingletonState.Locked: return foreground ? StatusAmber : StatusAmberBg;
                    case SingletonState.AccessDenied: return foreground ? StatusRed : StatusRedBg;
                }
            }
            return foreground ? TextSecondary : BgElevated;
        }

        private SolidColorBrush GetHandleRoleBrush(string tag, bool foreground)
        {
            if (tag == "PRIMARY LOCK") return foreground ? StatusAmber : StatusAmberBg;
            if (tag == "LEGACY LOCK") return foreground ? AccentBlue : AccentBlueBg;
            return foreground ? TextSecondary : BgElevated;
        }

        private sealed class CellBrushConverter : System.Windows.Data.IValueConverter
        {
            private readonly Func<object, SolidColorBrush> _select;

            public CellBrushConverter(Func<object, SolidColorBrush> select)
            {
                _select = select;
            }

            public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                return _select(value);
            }

            public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
            {
                throw new NotSupportedException();
            }
        }
    }
}
