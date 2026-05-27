using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Windows.Media.Control;
using Microsoft.Win32;

internal sealed class DockForm : Form
{
    private const int QuotaTailChunkBytes = 1024 * 1024;
    private const int MaxQuotaRolloutFilesToScan = 80;
    private const int MaxMediaThumbnailBytes = 4 * 1024 * 1024;
    private const double MediaTitleFlipCycleMs = 5000.0;
    private const double MediaTitleFlipDurationMs = 210.0;
    private const double DockResizeAnimationMs = 210.0;
    private const double DockItemEnterAnimationMs = 210.0;
    private const double DockItemExitAnimationMs = 190.0;
    private const double DockAutoHideDelayMs = 5000.0;
    private const double DockAutoHideAnimationMs = 220.0;
    private const double PreviewHideDelayMs = 500.0;
    private readonly Action openSettingsAction;
    private readonly Action exitAction;
    private readonly System.Windows.Forms.Timer timer;
    private readonly List<DockRuntimeItem> runtimeItems;
    private DockPreviewForm previewForm;
    private LaunchpadForm launchpadForm;
    private WidgetSettings currentSettings;
    private float scale;
    private bool hiddenForFullscreen;
    private bool layeredUpdateFailureLogged;
    private Point lastCursorPosition;
    private string loadedItemsText;
    private string loadedRunningSignature;
    private DateTime lastRunningRefreshUtc;
    private DateTime lastMediaRefreshUtc;
    private DateTime lastMediaAnimationRenderUtc;
    private DateTime mediaButtonPressAnimationUtc;
    private DateTime lastQuotaRefreshUtc;
    private IntPtr lastForegroundWindow;
    private DockMediaInfo mediaInfo;
    private DockQuotaSnapshot quotaSnapshot;
    private bool mediaRefreshInFlight;
    private bool mediaSessionUnavailableLogged;
    private int previewItemIndex;
    private bool previewHidePending;
    private DateTime previewHideDeadlineUtc;
    private int runtimePinnedCount;
    private RectangleF mediaPreviousRect;
    private RectangleF mediaPlayPauseRect;
    private RectangleF mediaNextRect;
    private Bitmap mediaAppBitmap;
    private string mediaAppIconKey;
    private string mediaSharedIconKey;
    private bool mediaBitmapIsArtwork;
    private bool mediaBitmapIsShared;
    private bool mediaTitleOverflow;
    private int mediaButtonPressKind;
    private int mediaTitlePageCount;
    private int mediaTitlePageIndex;
    private bool dockResizeAnimating;
    private bool startPendingEntriesAfterResize;
    private DateTime dockResizeStartedUtc;
    private Size dockResizeStartSize;
    private Size dockResizeTargetSize;
    private bool dockAutoHidden;
    private bool dockAutoHideAnimating;
    private bool dockAutoHideTargetHidden;
    private DateTime dockAutoHideStartedUtc;
    private DateTime dockMouseOutsideSinceUtc;
    private int dockAutoHideStartTop;
    private int dockAutoHideTargetTop;
    private int dockAutoHideAnimatedTop;
    private bool launchpadMouseLeftWasDown;
    private bool dockUnpinMode;
    private bool pendingDockUnpinRebuild;
    private bool dockDropActive;
    private bool dockDropTargetsLaunchpad;
    private int dockDropInsertionIndex;
    private RectangleF dockDropIndicatorRect;

    private enum DockItemAnimationState
    {
        Normal,
        EnteringPending,
        Entering,
        Exiting
    }

    private sealed class DockRuntimeItem : IDisposable
    {
        public DockItem Item { get; set; }
        public Bitmap Bitmap { get; set; }
        public string IconKey { get; set; }
        public bool IsRunning { get; set; }
        public bool IsFocused { get; set; }
        public int InstanceCount { get; set; }
        public IntPtr WindowHandle { get; set; }
        public int ProcessId { get; set; }
        public string ExecutablePath { get; set; }
        public string WindowTitle { get; set; }
        public List<DockPreviewSource> Windows { get; private set; }
        public string AnimationKey { get; set; }
        public DockItemAnimationState AnimationState { get; set; }
        public DateTime AnimationStartedUtc { get; set; }

        public DockRuntimeItem()
        {
            this.Windows = new List<DockPreviewSource>();
        }

        public void Dispose()
        {
            if (this.Bitmap != null)
            {
                this.Bitmap.Dispose();
                this.Bitmap = null;
            }
        }
    }

    private sealed class DockLayout
    {
        public RectangleF[] SystemButtonRects { get; set; }
        public RectangleF[] ItemRects { get; set; }
        public RectangleF SeparatorRect { get; set; }
        public RectangleF MediaRect { get; set; }
        public RectangleF QuotaRect { get; set; }
        public RectangleF[] ActionButtonRects { get; set; }
        public RectangleF PreviousRect { get; set; }
        public RectangleF PlayPauseRect { get; set; }
        public RectangleF NextRect { get; set; }
    }

    private sealed class DockMediaInfo
    {
        public string SourceAppUserModelId { get; set; }
        public string AppName { get; set; }
        public string IconPath { get; set; }
        public string Title { get; set; }
        public string Artist { get; set; }
        public byte[] ThumbnailBytes { get; set; }
        public string ThumbnailKey { get; set; }
        public bool HasSession { get; set; }
        public bool IsPlaying { get; set; }

        public static DockMediaInfo Empty()
        {
            return new DockMediaInfo
            {
                SourceAppUserModelId = string.Empty,
                AppName = string.Empty,
                IconPath = string.Empty,
                Title = string.Empty,
                Artist = string.Empty,
                ThumbnailBytes = null,
                ThumbnailKey = string.Empty,
                HasSession = false,
                IsPlaying = false
            };
        }

        public bool SameContent(DockMediaInfo other)
        {
            if (other == null)
            {
                return false;
            }

            return string.Equals(this.SourceAppUserModelId, other.SourceAppUserModelId, StringComparison.Ordinal) &&
                   string.Equals(this.AppName, other.AppName, StringComparison.Ordinal) &&
                   string.Equals(this.IconPath, other.IconPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(this.Title, other.Title, StringComparison.Ordinal) &&
                   string.Equals(this.Artist, other.Artist, StringComparison.Ordinal) &&
                   string.Equals(this.ThumbnailKey, other.ThumbnailKey, StringComparison.Ordinal) &&
                   this.HasSession == other.HasSession &&
                   this.IsPlaying == other.IsPlaying;
        }
    }

    private sealed class DockQuotaSnapshot
    {
        public int FiveHourPercent { get; set; }
        public int WeeklyPercent { get; set; }
        public DateTime FiveHourResetLocal { get; set; }
        public DateTime WeeklyResetLocal { get; set; }
        public bool FiveHourResetKnown { get; set; }
        public bool WeeklyResetKnown { get; set; }

        public static DockQuotaSnapshot CreateDefault()
        {
            return new DockQuotaSnapshot
            {
                FiveHourPercent = 100,
                WeeklyPercent = 100,
                FiveHourResetLocal = DateTime.MinValue,
                WeeklyResetLocal = DateTime.MinValue,
                FiveHourResetKnown = false,
                WeeklyResetKnown = false
            };
        }
    }

    private sealed class DockQuotaEvent
    {
        public DockQuotaSnapshot Snapshot { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    public DockForm(WidgetSettings settings, Action openSettingsAction, Action exitAction)
    {
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();
        this.openSettingsAction = openSettingsAction;
        this.exitAction = exitAction;
        this.runtimeItems = new List<DockRuntimeItem>();
        this.lastCursorPosition = Point.Empty;
        this.mediaInfo = DockMediaInfo.Empty();
        this.mediaAppIconKey = string.Empty;
        this.mediaSharedIconKey = string.Empty;
        this.mediaButtonPressKind = -1;
        this.mediaTitlePageIndex = -1;
        this.quotaSnapshot = DockQuotaSnapshot.CreateDefault();
        this.previewItemIndex = -1;
        this.dockMouseOutsideSinceUtc = DateTime.MinValue;
        this.dockDropInsertionIndex = -1;

        this.SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        using (Graphics g = this.CreateGraphics())
        {
            this.scale = Math.Max(1.0f, g.DpiX / 96.0f);
        }

        this.FormBorderStyle = FormBorderStyle.None;
        this.ShowInTaskbar = false;
        this.TopMost = false;
        this.AllowDrop = true;
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(18, 19, 22);
        this.Size = GetDesiredDockSize();

        this.timer = new System.Windows.Forms.Timer();
        this.timer.Interval = 30;
        this.timer.Tick += OnTimerTick;
        AppIconCache.IconReady += OnCachedIconReady;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyRuntimeSettings(this.currentSettings);
        EnsurePreviewForm();
        LaunchpadForm.WarmUpIconCacheAsync();
        this.timer.Start();
    }

    private void OnCachedIconReady(object sender, AppIconReadyEventArgs e)
    {
        if (e == null || this.IsDisposed)
        {
            return;
        }

        if (this.InvokeRequired)
        {
            if (!this.IsHandleCreated)
            {
                return;
            }

            try
            {
                this.BeginInvoke(new EventHandler<AppIconReadyEventArgs>(OnCachedIconReady), sender, e);
            }
            catch
            {
            }

            return;
        }

        bool relevant = false;
        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            if (string.Equals(this.runtimeItems[i].IconKey, e.Key, StringComparison.OrdinalIgnoreCase))
            {
                relevant = true;
                break;
            }
        }

        if (string.Equals(this.mediaSharedIconKey, e.Key, StringComparison.OrdinalIgnoreCase))
        {
            this.mediaAppBitmap = AppIconCache.GetBitmap(this.mediaSharedIconKey);
            relevant = true;
        }

        if (relevant)
        {
            RenderLayeredWindow();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        this.timer.Stop();
        this.timer.Tick -= OnTimerTick;
        this.timer.Dispose();
        AppIconCache.IconReady -= OnCachedIconReady;
        ClosePreviewForm();
        CloseLaunchpadForm();
        DisposeMediaAppBitmap();
        DisposeRuntimeItems();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(24)))
        {
            this.Region = new Region(path);
        }

        RenderLayeredWindow();
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_SETTINGCHANGE = 0x001A;

        base.WndProc(ref m);

        if (m.Msg == WM_DISPLAYCHANGE || m.Msg == WM_SETTINGCHANGE)
        {
            PositionDock();
            RepositionLaunchpad();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        UpdatePreviewFromCursor(Cursor.Position);
        RenderLayeredWindow();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        UpdatePreviewFromCursor(Cursor.Position);
        RenderLayeredWindow();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        PointF point = new PointF(e.X, e.Y);
        int systemButtonIndex = FindSystemButtonAtPoint(point);
        if (systemButtonIndex >= 0)
        {
            HidePreview();
            ActivateSystemButton(systemButtonIndex);
            return;
        }

        if (HandleDockUnpinBadgeClick(point))
        {
            HidePreview();
            return;
        }

        if (HandleMediaClick(point))
        {
            HidePreview();
            return;
        }

        if (HandleDockActionClick(point))
        {
            HidePreview();
            return;
        }

        int index = FindItemAtPoint(point);
        if (index >= 0 && index < this.runtimeItems.Count)
        {
            HidePreview();
            ActivateOrLaunchItem(this.runtimeItems[index]);
        }
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        base.OnDragEnter(e);
        UpdateDockDropState(e);
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        UpdateDockDropState(e);
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        ClearDockDropState();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        List<DockItem> items = AppDropHelper.BuildItemsFromData(e.Data);
        bool addToLaunchpad = this.dockDropTargetsLaunchpad;
        int insertionIndex = this.dockDropInsertionIndex;
        ClearDockDropState();

        if (items.Count == 0)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        if (addToLaunchpad)
        {
            AddDraggedItemsToLaunchpad(items);
        }
        else
        {
            AddDraggedItemsToDock(items, insertionIndex);
        }

        e.Effect = DragDropEffects.Copy;
    }

    public void ApplyRuntimeSettings(WidgetSettings settings)
    {
        string oldItemsText = this.loadedItemsText;
        string oldLaunchpadItemsText = this.currentSettings == null ? string.Empty : this.currentSettings.LaunchpadItemsText;
        this.currentSettings = settings.Clone();
        this.currentSettings.Normalize();

        bool dockItemsChanged = !string.Equals(oldItemsText, this.currentSettings.DockItemsText, StringComparison.Ordinal);
        bool launchpadItemsChanged = !string.Equals(oldLaunchpadItemsText, this.currentSettings.LaunchpadItemsText, StringComparison.Ordinal);
        if (dockItemsChanged)
        {
            this.dockUnpinMode = false;
            this.pendingDockUnpinRebuild = false;
            RebuildItems();
        }

        if ((dockItemsChanged || launchpadItemsChanged) &&
            this.launchpadForm != null &&
            !this.launchpadForm.IsDisposed &&
            this.launchpadForm.IsLaunchpadVisible)
        {
            this.launchpadForm.UpdatePinnedItemsText(this.currentSettings.LaunchpadItemsText, this.currentSettings.DockItemsText);
        }

        Size desiredSize = GetDesiredDockSize();
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        bool shouldBeTopMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        if (this.TopMost != shouldBeTopMost)
        {
            this.TopMost = shouldBeTopMost;
        }

        NativeMethods.SetWindowPos(
            this.Handle,
            shouldBeTopMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_NOTOPMOST,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOMOVE |
            NativeMethods.SWP_NOSIZE);

        PositionDock();
        RepositionLaunchpad();
        RenderLayeredWindow();
    }

    public void SetHiddenForFullscreen(bool hidden)
    {
        this.hiddenForFullscreen = hidden;
        if (hidden)
        {
            HidePreview();
            HideLaunchpad();
            if (this.Visible)
            {
                this.Hide();
            }

            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        PositionDock();
        RenderLayeredWindow();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        bool changed = RefreshRunningItemsIfNeeded();
        RefreshMediaInfoIfNeeded();
        RefreshQuotaInfoIfNeeded();
        UpdatePreviewFromCursor(Cursor.Position);
        IntPtr foregroundWindow = NativeMethods.GetForegroundWindowHandle();
        bool foregroundChanged = foregroundWindow != this.lastForegroundWindow;
        if (foregroundChanged)
        {
            this.lastForegroundWindow = foregroundWindow;
            UpdateFocusedRuntimeItems(foregroundWindow);
        }

        Point cursor = Cursor.Position;
        bool launchpadClickChanged = UpdateLaunchpadOutsideClick(cursor);
        bool wasInside = this.Bounds.Contains(this.lastCursorPosition);
        bool isInside = this.Bounds.Contains(cursor);
        DateTime now = DateTime.UtcNow;
        bool autoHideChanged = UpdateAutoHideState(now, cursor, foregroundWindow);
        bool previewHideChanged = UpdatePendingPreviewHide(now, cursor);
        bool dockAnimationChanged = UpdateDockAnimations(now);
        bool pressAnimationActive = this.mediaButtonPressKind >= 0 &&
            (now - this.mediaButtonPressAnimationUtc).TotalMilliseconds < 190.0;
        bool clearPressAnimation = this.mediaButtonPressKind >= 0 && !pressAnimationActive;
        if (clearPressAnimation)
        {
            this.mediaButtonPressKind = -1;
        }

        bool titleFlipActive = false;
        if (this.mediaTitleOverflow && this.mediaTitlePageCount > 1)
        {
            int titlePageIndex = GetMediaTitlePageIndex(this.mediaTitlePageCount, now);
            double titlePhase = GetMediaTitleFlipPhase(now);
            titleFlipActive =
                titlePageIndex != this.mediaTitlePageIndex ||
                titlePhase < MediaTitleFlipDurationMs + 40.0;
        }

        bool shouldAnimateMedia =
            (titleFlipActive || pressAnimationActive) &&
            (now - this.lastMediaAnimationRenderUtc).TotalMilliseconds >= 50.0;
        if (shouldAnimateMedia)
        {
            this.lastMediaAnimationRenderUtc = now;
        }

        if (changed || foregroundChanged || cursor != this.lastCursorPosition || wasInside != isInside || shouldAnimateMedia || clearPressAnimation || dockAnimationChanged || autoHideChanged || previewHideChanged || launchpadClickChanged)
        {
            this.lastCursorPosition = cursor;
            RenderLayeredWindow();
        }
    }

    private bool UpdateLaunchpadOutsideClick(Point cursor)
    {
        bool leftDown = NativeMethods.IsLeftMouseButtonDown();
        bool clicked = leftDown && !this.launchpadMouseLeftWasDown;
        this.launchpadMouseLeftWasDown = leftDown;
        if (!clicked ||
            this.launchpadForm == null ||
            this.launchpadForm.IsDisposed ||
            !this.launchpadForm.IsLaunchpadVisible)
        {
            return false;
        }

        if (this.Bounds.Contains(cursor) || this.launchpadForm.ContainsScreenPoint(cursor))
        {
            return false;
        }

        this.launchpadForm.HideLaunchpad();
        return true;
    }

    private void RebuildItems()
    {
        RebuildItems(GetRunningWindowInfos());
    }

    private void RebuildItems(List<NativeMethods.ApplicationWindowInfo> runningWindows)
    {
        int pinnedCount;
        List<DockRuntimeItem> rebuiltItems = BuildRuntimeItems(runningWindows, out pinnedCount);
        this.loadedItemsText = this.currentSettings.DockItemsText;
        this.loadedRunningSignature = BuildRunningSignature(runningWindows);
        this.lastRunningRefreshUtc = DateTime.UtcNow;
        this.runtimePinnedCount = pinnedCount;
        IntPtr foregroundWindow = NativeMethods.GetForegroundWindowHandle();
        this.lastForegroundWindow = foregroundWindow;

        DisposeRuntimeItems();
        this.runtimeItems.AddRange(rebuiltItems);
        ResetDockItemAnimations();
        UpdateFocusedRuntimeItems(foregroundWindow);
    }

    private List<DockRuntimeItem> BuildRuntimeItems(List<NativeMethods.ApplicationWindowInfo> runningWindows, out int pinnedCount)
    {
        List<DockRuntimeItem> runtimeItems = new List<DockRuntimeItem>();
        pinnedCount = 0;
        List<DockItem> pinnedItems = DockItem.ParseItems(this.currentSettings.DockItemsText);
        int count = Math.Min(pinnedItems.Count, 24);
        for (int i = 0; i < count; i++)
        {
            string executablePath = ResolveExecutablePath(pinnedItems[i].Command);
            DockRuntimeItem runtimeItem = new DockRuntimeItem();
            runtimeItem.Item = pinnedItems[i];
            runtimeItem.IconKey = AppIconCache.RequestIcon(executablePath, pinnedItems[i].Label);
            runtimeItem.IsRunning = false;
            runtimeItem.IsFocused = false;
            runtimeItem.InstanceCount = 0;
            runtimeItem.WindowHandle = IntPtr.Zero;
            runtimeItem.ProcessId = 0;
            runtimeItem.ExecutablePath = executablePath;
            runtimeItem.WindowTitle = string.Empty;
            runtimeItem.AnimationKey = BuildPinnedAnimationKey(i, pinnedItems[i]);
            runtimeItem.AnimationState = DockItemAnimationState.Normal;
            runtimeItems.Add(runtimeItem);
            pinnedCount++;
        }

        int runningCount = 0;
        for (int i = 0; i < runningWindows.Count && runningCount < 24; i++)
        {
            NativeMethods.ApplicationWindowInfo window = runningWindows[i];
            string executablePath = GetProcessExecutablePath(window.ProcessId);
            if (TryMarkPinnedRunning(runtimeItems, pinnedCount, executablePath, window))
            {
                continue;
            }

            if (TryIncrementRunningProcess(runtimeItems, pinnedCount, window.ProcessId, executablePath, window))
            {
                continue;
            }

            string label = BuildRunningLabel(window, executablePath);
            DockRuntimeItem runtimeItem = new DockRuntimeItem();
            runtimeItem.Item = new DockItem(label, executablePath);
            runtimeItem.IconKey = AppIconCache.RequestIcon(executablePath, label);
            runtimeItem.IsRunning = true;
            runtimeItem.IsFocused = false;
            runtimeItem.InstanceCount = 1;
            runtimeItem.WindowHandle = window.Handle;
            runtimeItem.ProcessId = window.ProcessId;
            runtimeItem.ExecutablePath = executablePath;
            runtimeItem.WindowTitle = window.Title;
            runtimeItem.Windows.Add(new DockPreviewSource(window.Handle, window.Title));
            runtimeItem.AnimationKey = BuildRunningAnimationKey(runtimeItem);
            runtimeItem.AnimationState = DockItemAnimationState.Normal;
            runtimeItems.Add(runtimeItem);
            runningCount++;
        }

        return runtimeItems;
    }

    private static string BuildPinnedAnimationKey(int index, DockItem item)
    {
        string command = item == null ? string.Empty : (item.Command ?? string.Empty);
        return "P|" + index.ToString(CultureInfo.InvariantCulture) + "|" + command.ToUpperInvariant();
    }

    private static string BuildRunningAnimationKey(DockRuntimeItem item)
    {
        if (item == null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrEmpty(item.ExecutablePath))
        {
            return "R|" + item.ExecutablePath.ToUpperInvariant();
        }

        if (item.ProcessId > 0)
        {
            return "R|PID|" + item.ProcessId.ToString(CultureInfo.InvariantCulture);
        }

        if (item.WindowHandle != IntPtr.Zero)
        {
            return "R|HWND|" + item.WindowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture);
        }

        return "R|" + (item.Item == null ? string.Empty : item.Item.Label ?? string.Empty).ToUpperInvariant();
    }

    private void ResetDockItemAnimations()
    {
        this.dockResizeAnimating = false;
        this.startPendingEntriesAfterResize = false;
        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            this.runtimeItems[i].AnimationState = DockItemAnimationState.Normal;
            this.runtimeItems[i].AnimationStartedUtc = DateTime.MinValue;
        }
    }

    private bool RefreshRunningItemsIfNeeded()
    {
        DateTime now = DateTime.UtcNow;
        if ((now - this.lastRunningRefreshUtc).TotalSeconds < 2.0)
        {
            return false;
        }

        List<NativeMethods.ApplicationWindowInfo> runningWindows = GetRunningWindowInfos();
        string signature = BuildRunningSignature(runningWindows);
        this.lastRunningRefreshUtc = now;
        if (string.Equals(signature, this.loadedRunningSignature, StringComparison.Ordinal))
        {
            return false;
        }

        int pinnedCount;
        List<DockRuntimeItem> rebuiltItems = BuildRuntimeItems(runningWindows, out pinnedCount);
        ApplyAnimatedRuntimeItems(rebuiltItems, pinnedCount, signature);
        HidePreview();
        return true;
    }

    private void ApplyAnimatedRuntimeItems(List<DockRuntimeItem> rebuiltItems, int newPinnedCount, string signature)
    {
        DateTime now = DateTime.UtcNow;
        Dictionary<string, DockRuntimeItem> rebuiltByKey = new Dictionary<string, DockRuntimeItem>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < rebuiltItems.Count; i++)
        {
            DockRuntimeItem item = rebuiltItems[i];
            if (!string.IsNullOrEmpty(item.AnimationKey) && !rebuiltByKey.ContainsKey(item.AnimationKey))
            {
                rebuiltByKey.Add(item.AnimationKey, item);
            }
        }

        HashSet<string> usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<DockRuntimeItem> mergedItems = new List<DockRuntimeItem>();
        bool hasEntering = false;
        bool hasExiting = false;

        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            DockRuntimeItem oldItem = this.runtimeItems[i];
            DockRuntimeItem rebuiltItem;
            if (!string.IsNullOrEmpty(oldItem.AnimationKey) &&
                rebuiltByKey.TryGetValue(oldItem.AnimationKey, out rebuiltItem) &&
                !usedKeys.Contains(oldItem.AnimationKey))
            {
                TransferRuntimeBitmap(oldItem, rebuiltItem);
                if (oldItem.AnimationState == DockItemAnimationState.Entering ||
                    oldItem.AnimationState == DockItemAnimationState.EnteringPending)
                {
                    rebuiltItem.AnimationState = oldItem.AnimationState;
                    rebuiltItem.AnimationStartedUtc = oldItem.AnimationStartedUtc;
                }
                else
                {
                    rebuiltItem.AnimationState = DockItemAnimationState.Normal;
                    rebuiltItem.AnimationStartedUtc = DateTime.MinValue;
                }

                mergedItems.Add(rebuiltItem);
                usedKeys.Add(oldItem.AnimationKey);
                continue;
            }

            if (i >= this.runtimePinnedCount && oldItem.IsRunning)
            {
                if (oldItem.AnimationState != DockItemAnimationState.Exiting)
                {
                    oldItem.AnimationState = DockItemAnimationState.Exiting;
                    oldItem.AnimationStartedUtc = now;
                }

                mergedItems.Add(oldItem);
                hasExiting = true;
            }
            else
            {
                oldItem.Dispose();
            }
        }

        for (int i = 0; i < rebuiltItems.Count; i++)
        {
            DockRuntimeItem item = rebuiltItems[i];
            if (!string.IsNullOrEmpty(item.AnimationKey) && usedKeys.Contains(item.AnimationKey))
            {
                continue;
            }

            item.AnimationState = i >= newPinnedCount ? DockItemAnimationState.EnteringPending : DockItemAnimationState.Normal;
            item.AnimationStartedUtc = DateTime.MinValue;
            if (item.AnimationState == DockItemAnimationState.EnteringPending)
            {
                hasEntering = true;
            }

            mergedItems.Add(item);
        }

        this.runtimeItems.Clear();
        this.runtimeItems.AddRange(mergedItems);
        this.runtimePinnedCount = newPinnedCount;
        this.loadedRunningSignature = signature;
        this.lastRunningRefreshUtc = now;
        this.lastForegroundWindow = NativeMethods.GetForegroundWindowHandle();
        UpdateFocusedRuntimeItems(this.lastForegroundWindow);

        Size targetSize = GetDesiredDockSize();
        if (targetSize != this.Size)
        {
            BeginDockResize(targetSize, hasEntering);
        }
        else if (hasEntering)
        {
            StartPendingEnterAnimations(now);
        }

        if (hasExiting || hasEntering)
        {
            HidePreview();
        }
    }

    private static void TransferRuntimeBitmap(DockRuntimeItem source, DockRuntimeItem target)
    {
        if (source == null || target == null || source.Bitmap == null)
        {
            return;
        }

        if (target.Bitmap != null && !object.ReferenceEquals(target.Bitmap, source.Bitmap))
        {
            target.Bitmap.Dispose();
        }

        target.Bitmap = source.Bitmap;
        source.Bitmap = null;
    }

    private void BeginDockResize(Size targetSize, bool startEntriesAfterResize)
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        if (this.Size == targetSize)
        {
            this.dockResizeAnimating = false;
            if (startEntriesAfterResize)
            {
                StartPendingEnterAnimations(DateTime.UtcNow);
            }

            return;
        }

        this.dockResizeStartSize = this.Size;
        this.dockResizeTargetSize = targetSize;
        this.dockResizeStartedUtc = DateTime.UtcNow;
        this.dockResizeAnimating = true;
        this.startPendingEntriesAfterResize = startEntriesAfterResize;
    }

    private bool UpdateDockAnimations(DateTime now)
    {
        bool changed = false;
        if (this.dockResizeAnimating)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - this.dockResizeStartedUtc).TotalMilliseconds / DockResizeAnimationMs));
            double eased = EaseOutCubic(progress);
            Size nextSize = new Size(
                (int)Math.Round(this.dockResizeStartSize.Width + (this.dockResizeTargetSize.Width - this.dockResizeStartSize.Width) * eased),
                (int)Math.Round(this.dockResizeStartSize.Height + (this.dockResizeTargetSize.Height - this.dockResizeStartSize.Height) * eased));
            ApplyDockBounds(nextSize);
            changed = true;

            if (progress >= 1.0)
            {
                this.dockResizeAnimating = false;
                ApplyDockBounds(this.dockResizeTargetSize);
                if (this.startPendingEntriesAfterResize)
                {
                    this.startPendingEntriesAfterResize = false;
                    StartPendingEnterAnimations(now);
                }
            }
        }

        bool removedExitingItems = false;
        for (int i = this.runtimeItems.Count - 1; i >= 0; i--)
        {
            DockRuntimeItem item = this.runtimeItems[i];
            if (item.AnimationState == DockItemAnimationState.Entering)
            {
                if ((now - item.AnimationStartedUtc).TotalMilliseconds >= DockItemEnterAnimationMs)
                {
                    item.AnimationState = DockItemAnimationState.Normal;
                    item.AnimationStartedUtc = DateTime.MinValue;
                    changed = true;
                }
            }
            else if (item.AnimationState == DockItemAnimationState.Exiting)
            {
                if ((now - item.AnimationStartedUtc).TotalMilliseconds >= DockItemExitAnimationMs)
                {
                    item.Dispose();
                    this.runtimeItems.RemoveAt(i);
                    removedExitingItems = true;
                    changed = true;
                }
                else
                {
                    changed = true;
                }
            }
            else if (item.AnimationState == DockItemAnimationState.EnteringPending)
            {
                changed = true;
            }
        }

        if (removedExitingItems)
        {
            if (this.pendingDockUnpinRebuild)
            {
                SyncRuntimeItemsAfterDockUnpin();
                changed = true;
            }
            else
            {
                Size targetSize = GetDesiredDockSize();
                if (targetSize != this.Size)
                {
                    BeginDockResize(targetSize, false);
                }
            }
        }

        return changed;
    }

    private void SyncRuntimeItemsAfterDockUnpin()
    {
        this.pendingDockUnpinRebuild = false;
        SyncRuntimeItemsAfterDockPinnedChange();

        if (this.runtimePinnedCount <= 0)
        {
            this.dockUnpinMode = false;
        }
    }

    private void SyncRuntimeItemsAfterDockPinnedChange()
    {
        List<NativeMethods.ApplicationWindowInfo> runningWindows = GetRunningWindowInfos();
        int pinnedCount;
        List<DockRuntimeItem> rebuiltItems = BuildRuntimeItems(runningWindows, out pinnedCount);
        this.loadedItemsText = this.currentSettings.DockItemsText;
        ApplyAnimatedRuntimeItems(rebuiltItems, pinnedCount, BuildRunningSignature(runningWindows));
    }

    private bool UpdateAutoHideState(DateTime now, Point cursor, IntPtr foregroundWindow)
    {
        if (this.hiddenForFullscreen)
        {
            return false;
        }

        bool changed = UpdateDockAutoHideAnimation(now);
        bool forceVisible = IsDockForcedVisibleOnDesktop() ||
            !NativeMethods.IsForegroundWindowMaximizedOrFullscreen(this.Handle);
        if (forceVisible)
        {
            this.dockMouseOutsideSinceUtc = DateTime.MinValue;
            if (this.dockAutoHidden || this.dockAutoHideAnimating)
            {
                changed |= BeginDockAutoHideTransition(false, now);
            }

            return changed;
        }

        bool hiddenOrHiding = this.dockAutoHidden || (this.dockAutoHideAnimating && this.dockAutoHideTargetHidden);
        if (hiddenOrHiding)
        {
            this.dockMouseOutsideSinceUtc = DateTime.MinValue;
            if (IsCursorAtScreenBottom(cursor))
            {
                changed |= BeginDockAutoHideTransition(false, now);
            }

            return changed;
        }

        if (this.Bounds.Contains(cursor))
        {
            this.dockMouseOutsideSinceUtc = DateTime.MinValue;
            return changed;
        }

        if (this.dockMouseOutsideSinceUtc == DateTime.MinValue)
        {
            this.dockMouseOutsideSinceUtc = now;
            return changed;
        }

        if ((now - this.dockMouseOutsideSinceUtc).TotalMilliseconds >= DockAutoHideDelayMs)
        {
            changed |= BeginDockAutoHideTransition(true, now);
        }

        return changed;
    }

    private bool IsDockForcedVisibleOnDesktop()
    {
        if (NativeMethods.IsForegroundDesktopOrShell(this.Handle))
        {
            return true;
        }

        return this.launchpadForm != null &&
            !this.launchpadForm.IsDisposed &&
            this.launchpadForm.Visible;
    }

    private bool IsCursorAtScreenBottom(Point cursor)
    {
        Rectangle screenBounds = Screen.FromPoint(cursor).Bounds;
        int triggerHeight = Math.Max(S(4), 4);
        return cursor.X >= screenBounds.Left &&
            cursor.X < screenBounds.Right &&
            cursor.Y >= screenBounds.Bottom - triggerHeight &&
            cursor.Y < screenBounds.Bottom + triggerHeight;
    }

    private bool BeginDockAutoHideTransition(bool hide, DateTime now)
    {
        if (this.hiddenForFullscreen)
        {
            return false;
        }

        if (hide)
        {
            HidePreview();
        }

        if (!this.Visible)
        {
            this.Show();
        }

        int targetTop = CalculateDockTop(this.Size, hide);
        int startTop = this.Location.Y;
        if (!this.dockAutoHideAnimating && this.dockAutoHidden == hide && Math.Abs(startTop - targetTop) <= 1)
        {
            return false;
        }

        this.dockAutoHideAnimating = true;
        this.dockAutoHideTargetHidden = hide;
        this.dockAutoHideStartedUtc = now;
        this.dockAutoHideStartTop = startTop;
        this.dockAutoHideTargetTop = targetTop;
        this.dockAutoHideAnimatedTop = startTop;
        this.dockMouseOutsideSinceUtc = DateTime.MinValue;
        return true;
    }

    private bool UpdateDockAutoHideAnimation(DateTime now)
    {
        if (!this.dockAutoHideAnimating)
        {
            return false;
        }

        this.dockAutoHideTargetTop = CalculateDockTop(this.Size, this.dockAutoHideTargetHidden);
        double progress = Math.Max(0.0, Math.Min(1.0, (now - this.dockAutoHideStartedUtc).TotalMilliseconds / DockAutoHideAnimationMs));
        double eased = this.dockAutoHideTargetHidden ? EaseInCubic(progress) : EaseOutCubic(progress);
        int nextTop = (int)Math.Round(this.dockAutoHideStartTop + (this.dockAutoHideTargetTop - this.dockAutoHideStartTop) * eased);
        bool changed = nextTop != this.dockAutoHideAnimatedTop || progress >= 1.0;
        this.dockAutoHideAnimatedTop = nextTop;
        if (progress >= 1.0)
        {
            this.dockAutoHideAnimating = false;
            this.dockAutoHidden = this.dockAutoHideTargetHidden;
            this.dockAutoHideAnimatedTop = this.dockAutoHideTargetTop;
            this.dockMouseOutsideSinceUtc = DateTime.MinValue;
        }

        if (changed)
        {
            ApplyDockBounds(this.Size);
        }

        return changed;
    }

    private void StartPendingEnterAnimations(DateTime now)
    {
        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            DockRuntimeItem item = this.runtimeItems[i];
            if (item.AnimationState == DockItemAnimationState.EnteringPending)
            {
                item.AnimationState = DockItemAnimationState.Entering;
                item.AnimationStartedUtc = now;
            }
        }
    }

    private static double EaseOutCubic(double value)
    {
        value = Math.Max(0.0, Math.Min(1.0, value));
        double inverse = 1.0 - value;
        return 1.0 - inverse * inverse * inverse;
    }

    private static double EaseInCubic(double value)
    {
        value = Math.Max(0.0, Math.Min(1.0, value));
        return value * value * value;
    }

    private void RefreshQuotaInfoIfNeeded()
    {
        DateTime now = DateTime.UtcNow;
        bool resetDue = IsQuotaResetDue(this.quotaSnapshot, DateTime.Now);
        if (!resetDue && (now - this.lastQuotaRefreshUtc).TotalSeconds < 30.0)
        {
            return;
        }

        this.lastQuotaRefreshUtc = now;
        this.quotaSnapshot = ReadQuotaSnapshot();
        RenderLayeredWindow();
    }

    private static bool IsQuotaResetDue(DockQuotaSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null)
        {
            return true;
        }

        return (snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal) ||
            (snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal);
    }

    private static DockQuotaSnapshot ReadQuotaSnapshot()
    {
        DockQuotaSnapshot snapshot;
        if (TryReadCodexSessionQuota(out snapshot))
        {
            return NormalizeQuotaSnapshot(snapshot, DateTime.Now);
        }

        if (TryReadQuotaIniSnapshot(out snapshot))
        {
            return NormalizeQuotaSnapshot(snapshot, DateTime.Now);
        }

        return DockQuotaSnapshot.CreateDefault();
    }

    private static DockQuotaSnapshot NormalizeQuotaSnapshot(DockQuotaSnapshot snapshot, DateTime nowLocal)
    {
        if (snapshot == null)
        {
            return DockQuotaSnapshot.CreateDefault();
        }

        snapshot.FiveHourPercent = ClampPercent(snapshot.FiveHourPercent);
        snapshot.WeeklyPercent = ClampPercent(snapshot.WeeklyPercent);
        if (snapshot.FiveHourResetKnown && snapshot.FiveHourResetLocal <= nowLocal)
        {
            snapshot.FiveHourPercent = 100;
            snapshot.FiveHourResetKnown = false;
            snapshot.FiveHourResetLocal = DateTime.MinValue;
        }

        if (snapshot.WeeklyResetKnown && snapshot.WeeklyResetLocal <= nowLocal)
        {
            snapshot.WeeklyPercent = 100;
            snapshot.WeeklyResetKnown = false;
            snapshot.WeeklyResetLocal = DateTime.MinValue;
        }

        return snapshot;
    }

    private static bool TryReadCodexSessionQuota(out DockQuotaSnapshot snapshot)
    {
        snapshot = null;
        string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profilePath))
        {
            return false;
        }

        string sessionsPath = Path.Combine(Path.Combine(profilePath, ".codex"), "sessions");
        if (!Directory.Exists(sessionsPath))
        {
            return false;
        }

        List<string> rolloutFiles = new List<string>();
        try
        {
            foreach (string file in Directory.EnumerateFiles(sessionsPath, "*.jsonl", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (name != null && name.StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
                {
                    rolloutFiles.Add(file);
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        if (rolloutFiles.Count == 0)
        {
            return false;
        }

        rolloutFiles.Sort(delegate(string left, string right)
        {
            return SafeGetLastWriteTimeUtc(right).CompareTo(SafeGetLastWriteTimeUtc(left));
        });

        JavaScriptSerializer serializer = new JavaScriptSerializer();
        serializer.MaxJsonLength = int.MaxValue;

        DockQuotaEvent latestEvent = null;
        int count = Math.Min(rolloutFiles.Count, MaxQuotaRolloutFilesToScan);
        for (int i = 0; i < count; i++)
        {
            string file = rolloutFiles[i];
            if (latestEvent != null && SafeGetLastWriteTimeUtc(file) < latestEvent.UpdatedUtc)
            {
                break;
            }

            DockQuotaEvent quotaEvent;
            if (TryParseLatestQuotaEventFromFile(file, serializer, out quotaEvent) &&
                (latestEvent == null || quotaEvent.UpdatedUtc > latestEvent.UpdatedUtc))
            {
                latestEvent = quotaEvent;
            }
        }

        if (latestEvent == null)
        {
            return false;
        }

        snapshot = latestEvent.Snapshot;
        return snapshot != null;
    }

    private static bool TryParseLatestQuotaEventFromFile(string path, JavaScriptSerializer serializer, out DockQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        try
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long offset = stream.Length;
                byte[] tail = new byte[0];
                while (offset > 0)
                {
                    int readSize = (int)Math.Min(QuotaTailChunkBytes, offset);
                    offset -= readSize;
                    stream.Seek(offset, SeekOrigin.Begin);

                    byte[] chunk = new byte[readSize];
                    int read = stream.Read(chunk, 0, readSize);
                    if (read <= 0)
                    {
                        continue;
                    }

                    byte[] expandedTail = new byte[read + tail.Length];
                    Buffer.BlockCopy(chunk, 0, expandedTail, 0, read);
                    if (tail.Length > 0)
                    {
                        Buffer.BlockCopy(tail, 0, expandedTail, read, tail.Length);
                    }

                    tail = expandedTail;
                    string text = Encoding.UTF8.GetString(tail, 0, tail.Length);
                    if (TryParseLatestQuotaEventFromText(text, path, serializer, out quotaEvent))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
        }

        return false;
    }

    private static bool TryParseLatestQuotaEventFromText(string text, string path, JavaScriptSerializer serializer, out DockQuotaEvent quotaEvent)
    {
        quotaEvent = null;
        string[] lines = text.Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 ||
                line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"rate_limits\"", StringComparison.Ordinal) < 0)
            {
                continue;
            }

            Dictionary<string, object> root;
            try
            {
                root = serializer.DeserializeObject(line) as Dictionary<string, object>;
            }
            catch
            {
                continue;
            }

            if (root == null ||
                !string.Equals(GetQuotaString(root, "type"), "event_msg", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> payload = GetQuotaObject(root, "payload");
            if (payload == null ||
                !string.Equals(GetQuotaString(payload, "type"), "token_count", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, object> rateLimits = GetQuotaObject(payload, "rate_limits");
            DockQuotaSnapshot snapshot;
            if (rateLimits == null || !TryBuildQuotaSnapshot(rateLimits, out snapshot))
            {
                continue;
            }

            DateTime updatedLocal;
            DateTime updatedUtc = SafeGetLastWriteTimeUtc(path);
            if (TryGetQuotaDate(root, "timestamp", out updatedLocal))
            {
                updatedUtc = updatedLocal.ToUniversalTime();
            }
            else if (updatedUtc == DateTime.MinValue)
            {
                updatedUtc = DateTime.UtcNow;
            }

            quotaEvent = new DockQuotaEvent();
            quotaEvent.Snapshot = snapshot;
            quotaEvent.UpdatedUtc = updatedUtc;
            return true;
        }

        return false;
    }

    private static bool TryBuildQuotaSnapshot(Dictionary<string, object> rateLimits, out DockQuotaSnapshot snapshot)
    {
        snapshot = DockQuotaSnapshot.CreateDefault();
        bool found = false;
        found = ApplyQuotaSlot(rateLimits, "primary", snapshot) || found;
        found = ApplyQuotaSlot(rateLimits, "secondary", snapshot) || found;
        return found;
    }

    private static bool ApplyQuotaSlot(Dictionary<string, object> rateLimits, string key, DockQuotaSnapshot snapshot)
    {
        Dictionary<string, object> slot = GetQuotaObject(rateLimits, key);
        if (slot == null)
        {
            return false;
        }

        double usedPercent;
        if (!TryGetQuotaNumber(slot, "used_percent", out usedPercent) &&
            !TryGetQuotaNumber(slot, "used_percentage", out usedPercent))
        {
            return false;
        }

        double windowMinutes;
        bool hasWindowMinutes = TryGetQuotaNumber(slot, "window_minutes", out windowMinutes);
        bool isFiveHour = string.Equals(key, "primary", StringComparison.OrdinalIgnoreCase);
        if (hasWindowMinutes)
        {
            isFiveHour = windowMinutes <= 300.0;
        }

        int remainingPercent = ClampPercent((int)Math.Round(100.0 - usedPercent));
        DateTime resetLocal;
        bool hasReset = TryGetQuotaDate(slot, "resets_at", out resetLocal);
        if (isFiveHour)
        {
            snapshot.FiveHourPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.FiveHourResetLocal = resetLocal;
                snapshot.FiveHourResetKnown = true;
            }
        }
        else
        {
            snapshot.WeeklyPercent = remainingPercent;
            if (hasReset)
            {
                snapshot.WeeklyResetLocal = resetLocal;
                snapshot.WeeklyResetKnown = true;
            }
        }

        return true;
    }

    private static bool TryReadQuotaIniSnapshot(out DockQuotaSnapshot snapshot)
    {
        snapshot = DockQuotaSnapshot.CreateDefault();
        string path = Path.Combine(Logger.DirectoryPath, "quota.ini");
        if (!File.Exists(path))
        {
            return false;
        }

        bool found = false;
        try
        {
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim();
                string value = line.Substring(split + 1).Trim();
                int percent;
                DateTime dateTime;
                if (string.Equals(key, "FiveHourPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.FiveHourPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "WeeklyPercent", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out percent))
                {
                    snapshot.WeeklyPercent = ClampPercent(percent);
                    found = true;
                }
                else if (string.Equals(key, "FiveHourReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.FiveHourResetLocal = dateTime;
                    snapshot.FiveHourResetKnown = true;
                    found = true;
                }
                else if (string.Equals(key, "WeeklyReset", StringComparison.OrdinalIgnoreCase) && DateTime.TryParse(value, out dateTime))
                {
                    snapshot.WeeklyResetLocal = dateTime;
                    snapshot.WeeklyResetKnown = true;
                    found = true;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            return false;
        }

        return found;
    }

    private static Dictionary<string, object> GetQuotaObject(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value))
        {
            return null;
        }

        return value as Dictionary<string, object>;
    }

    private static string GetQuotaString(Dictionary<string, object> values, string key)
    {
        object value;
        if (values == null || !values.TryGetValue(key, out value) || value == null)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static bool TryGetQuotaNumber(Dictionary<string, object> values, string key, out double number)
    {
        number = 0.0;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaNumber(value, out number);
    }

    private static bool TryReadQuotaNumber(object value, out double number)
    {
        number = 0.0;
        if (value == null)
        {
            return false;
        }

        string text = value as string;
        if (text != null)
        {
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            number = 0.0;
            return false;
        }
    }

    private static bool TryGetQuotaDate(Dictionary<string, object> values, string key, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        object value;
        return values != null &&
            values.TryGetValue(key, out value) &&
            TryReadQuotaDate(value, out localDate);
    }

    private static bool TryReadQuotaDate(object value, out DateTime localDate)
    {
        localDate = DateTime.MinValue;
        double seconds;
        if (TryReadQuotaNumber(value, out seconds))
        {
            if (seconds > 10000000000.0)
            {
                seconds /= 1000.0;
            }

            try
            {
                DateTimeOffset epoch = new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);
                localDate = epoch.AddSeconds(seconds).LocalDateTime;
                return true;
            }
            catch
            {
                localDate = DateTime.MinValue;
                return false;
            }
        }

        string text = value as string;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        DateTimeOffset offsetDate;
        if (DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out offsetDate))
        {
            localDate = offsetDate.LocalDateTime;
            return true;
        }

        DateTime dateTime;
        if (DateTime.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out dateTime))
        {
            localDate = dateTime.ToLocalTime();
            return true;
        }

        return false;
    }

    private static DateTime SafeGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static int ClampPercent(int value)
    {
        return Math.Max(0, Math.Min(100, value));
    }

    private void RefreshMediaInfoIfNeeded()
    {
        RefreshMediaInfoIfNeeded(false);
    }

    private async void RefreshMediaInfoIfNeeded(bool force)
    {
        DateTime now = DateTime.UtcNow;
        if (this.mediaRefreshInFlight || (!force && (now - this.lastMediaRefreshUtc).TotalSeconds < 2.0))
        {
            return;
        }

        this.lastMediaRefreshUtc = now;
        this.mediaRefreshInFlight = true;
        try
        {
            DockMediaInfo next = await ReadCurrentMediaInfoAsync();
            if (!this.IsDisposed)
            {
                ApplyMediaInfo(next);
            }
        }
        catch (Exception ex)
        {
            if (!this.mediaSessionUnavailableLogged)
            {
                this.mediaSessionUnavailableLogged = true;
                Program.LogException(ex);
            }

            if (!this.IsDisposed)
            {
                ApplyMediaInfo(DockMediaInfo.Empty());
            }
        }
        finally
        {
            this.mediaRefreshInFlight = false;
        }
    }

    private async void RefreshMediaInfoSoon(int delayMs)
    {
        if (delayMs > 0)
        {
            await Task.Delay(delayMs);
        }

        if (!this.IsDisposed)
        {
            RefreshMediaInfoIfNeeded(true);
        }
    }

    private void ToggleMediaPlaybackStateOptimistically()
    {
        if (this.mediaInfo == null || !this.mediaInfo.HasSession)
        {
            return;
        }

        this.mediaInfo.IsPlaying = !this.mediaInfo.IsPlaying;
        this.lastMediaRefreshUtc = DateTime.MinValue;
        RenderLayeredWindow();
    }

    private void ApplyMediaInfo(DockMediaInfo next)
    {
        if (next == null)
        {
            next = DockMediaInfo.Empty();
        }

        if (this.mediaInfo != null && this.mediaInfo.SameContent(next))
        {
            return;
        }

        UpdateMediaAppBitmap(next);
        this.mediaInfo = next;
        RenderLayeredWindow();
    }

    private void UpdateMediaAppBitmap(DockMediaInfo next)
    {
        string iconPath = next == null || !next.HasSession ? string.Empty : (next.IconPath ?? string.Empty);
        string appName = next == null || !next.HasSession ? string.Empty : (next.AppName ?? string.Empty);
        string thumbnailKey = next == null || !next.HasSession ? string.Empty : (next.ThumbnailKey ?? string.Empty);
        string iconKey = thumbnailKey + "|" + iconPath + "|" + appName;
        if (string.Equals(this.mediaAppIconKey, iconKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposeMediaAppBitmap();
        this.mediaAppIconKey = iconKey;
        this.mediaSharedIconKey = string.Empty;
        if (next == null || !next.HasSession)
        {
            return;
        }

        if (next.ThumbnailBytes != null && next.ThumbnailBytes.Length > 0)
        {
            this.mediaAppBitmap = LoadBitmapFromBytes(next.ThumbnailBytes);
            this.mediaBitmapIsArtwork = this.mediaAppBitmap != null;
        }

        if (this.mediaAppBitmap == null && !string.IsNullOrEmpty(iconPath) && File.Exists(iconPath))
        {
            this.mediaSharedIconKey = AppIconCache.RequestIcon(iconPath, appName);
            this.mediaAppBitmap = AppIconCache.GetBitmap(this.mediaSharedIconKey);
            this.mediaBitmapIsShared = true;
        }

        if (this.mediaAppBitmap == null)
        {
            this.mediaSharedIconKey = AppIconCache.RequestIcon(string.Empty, string.IsNullOrEmpty(appName) ? "Media" : appName);
            this.mediaAppBitmap = AppIconCache.GetBitmap(this.mediaSharedIconKey);
            this.mediaBitmapIsShared = true;
        }
    }

    private static Bitmap LoadBitmapFromBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return null;
        }

        try
        {
            using (MemoryStream stream = new MemoryStream(bytes))
            using (Image image = Image.FromStream(stream))
            {
                return new Bitmap(image);
            }
        }
        catch
        {
            return null;
        }
    }

    private void DisposeMediaAppBitmap()
    {
        if (this.mediaAppBitmap != null && !this.mediaBitmapIsShared)
        {
            this.mediaAppBitmap.Dispose();
        }

        this.mediaAppBitmap = null;
        this.mediaSharedIconKey = string.Empty;
        this.mediaBitmapIsArtwork = false;
        this.mediaBitmapIsShared = false;
    }

    private static async Task<DockMediaInfo> ReadCurrentMediaInfoAsync()
    {
        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (manager == null)
        {
            return DockMediaInfo.Empty();
        }

        GlobalSystemMediaTransportControlsSession session = manager.GetCurrentSession();
        if (session == null)
        {
            return DockMediaInfo.Empty();
        }

        GlobalSystemMediaTransportControlsSessionMediaProperties properties =
            await session.TryGetMediaPropertiesAsync();
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo = session.GetPlaybackInfo();
        DockMediaInfo info = new DockMediaInfo();
        info.HasSession = true;
        info.IsPlaying = playbackInfo != null &&
            playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        info.SourceAppUserModelId = CleanMediaText(session.SourceAppUserModelId);
        info.AppName = BuildMediaAppName(info.SourceAppUserModelId);
        info.IconPath = ResolveMediaIconPath(info.SourceAppUserModelId, info.AppName);
        info.Title = properties == null ? string.Empty : CleanMediaText(properties.Title);
        info.Artist = properties == null ? string.Empty : CleanMediaText(properties.Artist);
        info.ThumbnailBytes = await ReadMediaThumbnailBytesAsync(properties);
        info.ThumbnailKey = BuildMediaThumbnailKey(info.ThumbnailBytes);

        if (string.IsNullOrEmpty(info.AppName))
        {
            info.AppName = "Media";
        }

        return info;
    }

    private static async Task<byte[]> ReadMediaThumbnailBytesAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties)
    {
        if (properties == null || properties.Thumbnail == null)
        {
            return null;
        }

        try
        {
            using (Windows.Storage.Streams.IRandomAccessStreamWithContentType thumbnailStream = await properties.Thumbnail.OpenReadAsync())
            using (Stream stream = thumbnailStream.AsStreamForRead())
            using (MemoryStream memory = new MemoryStream())
            {
                byte[] buffer = new byte[8192];
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (memory.Length + read > MaxMediaThumbnailBytes)
                    {
                        return null;
                    }

                    memory.Write(buffer, 0, read);
                }

                return memory.Length == 0 ? null : memory.ToArray();
            }
        }
        catch
        {
            return null;
        }
    }

    private static string BuildMediaThumbnailKey(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            return string.Empty;
        }

        unchecked
        {
            uint hash = 2166136261;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619;
            }

            return bytes.Length.ToString(CultureInfo.InvariantCulture) + "-" + hash.ToString("X8", CultureInfo.InvariantCulture);
        }
    }

    private void DisposeRuntimeItems()
    {
        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            this.runtimeItems[i].Dispose();
        }

        this.runtimeItems.Clear();
    }

    private List<NativeMethods.ApplicationWindowInfo> GetRunningWindowInfos()
    {
        return NativeMethods.EnumerateApplicationWindows(this.Handle);
    }

    private void UpdateFocusedRuntimeItems(IntPtr foregroundWindow)
    {
        int foregroundProcessId = 0;
        string foregroundExecutablePath = string.Empty;
        if (foregroundWindow != IntPtr.Zero && NativeMethods.TryGetWindowProcessId(foregroundWindow, out foregroundProcessId))
        {
            foregroundExecutablePath = GetProcessExecutablePath(foregroundProcessId);
        }

        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            DockRuntimeItem item = this.runtimeItems[i];
            bool isFocused = false;
            if (item.IsRunning && foregroundWindow != IntPtr.Zero)
            {
                isFocused = ContainsRuntimeWindow(item, foregroundWindow);
                if (!isFocused && foregroundProcessId > 0 && item.ProcessId == foregroundProcessId)
                {
                    isFocused = true;
                }

                if (!isFocused &&
                    !string.IsNullOrEmpty(foregroundExecutablePath) &&
                    !string.IsNullOrEmpty(item.ExecutablePath) &&
                    string.Equals(item.ExecutablePath, foregroundExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    isFocused = true;
                }
            }

            item.IsFocused = isFocused;
        }
    }

    private static bool ContainsRuntimeWindow(DockRuntimeItem item, IntPtr handle)
    {
        if (item == null || handle == IntPtr.Zero)
        {
            return false;
        }

        if (item.WindowHandle == handle)
        {
            return true;
        }

        for (int i = 0; i < item.Windows.Count; i++)
        {
            if (item.Windows[i].Handle == handle)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildRunningSignature(List<NativeMethods.ApplicationWindowInfo> runningWindows)
    {
        StringBuilder builder = new StringBuilder();
        for (int i = 0; i < runningWindows.Count; i++)
        {
            builder.Append(runningWindows[i].ProcessId);
            builder.Append(':');
            builder.Append(runningWindows[i].Handle.ToInt64().ToString("X"));
            builder.Append(';');
        }

        return builder.ToString();
    }

    private bool TryMarkPinnedRunning(List<DockRuntimeItem> items, int pinnedCount, string executablePath, NativeMethods.ApplicationWindowInfo window)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return false;
        }

        for (int i = 0; i < pinnedCount && i < items.Count; i++)
        {
            string pinnedPath = items[i].ExecutablePath;
            if (!string.IsNullOrEmpty(pinnedPath) &&
                string.Equals(pinnedPath, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                DockRuntimeItem item = items[i];
                item.IsRunning = true;
                AddRuntimeWindow(item, window);
                item.ExecutablePath = executablePath;
                return true;
            }
        }

        return false;
    }

    private bool TryIncrementRunningProcess(List<DockRuntimeItem> items, int pinnedCount, int processId, string executablePath, NativeMethods.ApplicationWindowInfo window)
    {
        for (int i = pinnedCount; i < items.Count; i++)
        {
            DockRuntimeItem item = items[i];
            if (!item.IsRunning)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(executablePath) &&
                !string.IsNullOrEmpty(item.ExecutablePath) &&
                string.Equals(item.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                AddRuntimeWindow(item, window);
                return true;
            }

            if (processId > 0 && item.ProcessId == processId)
            {
                AddRuntimeWindow(item, window);
                return true;
            }
        }

        return false;
    }

    private static void AddRuntimeWindow(DockRuntimeItem item, NativeMethods.ApplicationWindowInfo window)
    {
        if (item == null || window == null || window.Handle == IntPtr.Zero)
        {
            return;
        }

        for (int i = 0; i < item.Windows.Count; i++)
        {
            if (item.Windows[i].Handle == window.Handle)
            {
                return;
            }
        }

        item.Windows.Add(new DockPreviewSource(window.Handle, window.Title));
        item.InstanceCount = item.Windows.Count;
        if (item.WindowHandle == IntPtr.Zero)
        {
            item.WindowHandle = window.Handle;
            item.ProcessId = window.ProcessId;
            item.WindowTitle = window.Title;
        }
        else if (string.IsNullOrEmpty(item.WindowTitle))
        {
            item.WindowTitle = window.Title;
        }
    }

    private static string BuildRunningLabel(NativeMethods.ApplicationWindowInfo window, string executablePath)
    {
        if (window != null && !string.IsNullOrEmpty(window.Title))
        {
            return window.Title.Trim();
        }

        if (!string.IsNullOrEmpty(executablePath))
        {
            string name = Path.GetFileNameWithoutExtension(executablePath);
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }

        if (window != null && window.ProcessId > 0)
        {
            try
            {
                using (Process process = Process.GetProcessById(window.ProcessId))
                {
                    if (!string.IsNullOrEmpty(process.ProcessName))
                    {
                        return process.ProcessName;
                    }
                }
            }
            catch
            {
            }
        }

        return "App";
    }

    private static string ResolveMediaIconPath(string sourceAppUserModelId, string appName)
    {
        string source = CleanMediaText(sourceAppUserModelId);
        if (string.IsNullOrEmpty(source))
        {
            return string.Empty;
        }

        string fileName;
        string arguments;
        SplitCommandLine(source, out fileName, out arguments);
        string resolved = ResolveExecutablePath(fileName);
        if (!string.IsNullOrEmpty(resolved))
        {
            return resolved;
        }

        string packagePrefix = source;
        int bang = packagePrefix.IndexOf('!');
        if (bang > 0)
        {
            packagePrefix = packagePrefix.Substring(0, bang);
        }

        string packageFamily = packagePrefix;
        int underscore = packageFamily.IndexOf('_');
        if (underscore > 0)
        {
            packageFamily = packageFamily.Substring(0, underscore);
        }

        if (source.IndexOf('!') > 0)
        {
            return "shell:AppsFolder\\" + source;
        }

        string fromRunningProcess = FindRunningExecutableForMediaApp(packagePrefix, packageFamily, appName);
        if (!string.IsNullOrEmpty(fromRunningProcess))
        {
            return fromRunningProcess;
        }

        if (source.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveExecutablePath(source);
        }

        return string.Empty;
    }

    private static string FindRunningExecutableForMediaApp(string packagePrefix, string packageFamily, string appName)
    {
        string normalizedAppName = CleanProcessName(appName);
        try
        {
            Process[] processes = Process.GetProcesses();
            for (int i = 0; i < processes.Length; i++)
            {
                using (Process process = processes[i])
                {
                    string path = string.Empty;
                    try
                    {
                        if (process.MainModule != null)
                        {
                            path = process.MainModule.FileName;
                        }
                    }
                    catch
                    {
                    }

                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(packagePrefix) &&
                        path.IndexOf(packagePrefix, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return path;
                    }

                    if (!string.IsNullOrEmpty(packageFamily) &&
                        path.IndexOf(packageFamily, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return path;
                    }

                    string processName = CleanProcessName(process.ProcessName);
                    if (!string.IsNullOrEmpty(normalizedAppName) &&
                        string.Equals(processName, normalizedAppName, StringComparison.OrdinalIgnoreCase))
                    {
                        return path;
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string CleanProcessName(string value)
    {
        value = CleanMediaText(value);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string fileName = Path.GetFileNameWithoutExtension(value);
        if (!string.IsNullOrEmpty(fileName))
        {
            value = fileName;
        }

        return value.Replace(" ", string.Empty).Replace("-", string.Empty).Replace("_", string.Empty);
    }

    private static string BuildMediaAppName(string sourceAppUserModelId)
    {
        string value = CleanMediaText(sourceAppUserModelId);
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int bang = value.IndexOf('!');
        if (bang > 0)
        {
            value = value.Substring(0, bang);
        }

        try
        {
            string fileName = Path.GetFileNameWithoutExtension(value);
            if (!string.IsNullOrEmpty(fileName) && !string.Equals(fileName, value, StringComparison.OrdinalIgnoreCase))
            {
                value = fileName;
            }
        }
        catch
        {
        }

        int underscore = value.IndexOf('_');
        if (underscore > 0)
        {
            value = value.Substring(0, underscore);
        }

        int dot = value.LastIndexOf('.');
        if (dot >= 0 && dot < value.Length - 1)
        {
            value = value.Substring(dot + 1);
        }

        value = value.Replace('-', ' ').Replace('_', ' ').Trim();
        return value.Length == 0 ? string.Empty : value;
    }

    private static string CleanMediaText(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string BuildMediaDisplayTitle(DockMediaInfo info)
    {
        if (info == null || !info.HasSession)
        {
            return "Nothing is playing";
        }

        if (!string.IsNullOrEmpty(info.Title))
        {
            return info.Title;
        }

        return info.IsPlaying ? "Playing" : "Paused";
    }

    private static string BuildMediaDisplaySubtitle(DockMediaInfo info)
    {
        if (info == null || !info.HasSession)
        {
            return string.Empty;
        }

        return info.Artist ?? string.Empty;
    }

    private static string GetProcessExecutablePath(int processId)
    {
        if (processId <= 0)
        {
            return string.Empty;
        }

        string nativePath = NativeMethods.TryGetProcessImagePath(processId);
        if (!string.IsNullOrEmpty(nativePath) && File.Exists(nativePath))
        {
            return nativePath;
        }

        string processName = string.Empty;
        try
        {
            using (Process process = Process.GetProcessById(processId))
            {
                processName = process.ProcessName;
                if (process.MainModule != null)
                {
                    return process.MainModule.FileName;
                }
            }
        }
        catch
        {
        }

        string fallbackPath = ResolveExecutablePath(processName);
        if (!string.IsNullOrEmpty(fallbackPath))
        {
            return fallbackPath;
        }

        if (!string.IsNullOrEmpty(processName) &&
            !processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fallbackPath = ResolveExecutablePath(processName + ".exe");
            if (!string.IsNullOrEmpty(fallbackPath))
            {
                return fallbackPath;
            }
        }

        return string.Empty;
    }

    private void PositionDock()
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        if (!this.Visible)
        {
            this.Show();
        }

        Size desiredSize = GetDesiredDockSize();
        ApplyDockBounds(desiredSize);
    }

    private void ApplyDockBounds(Size desiredSize)
    {
        if (this.hiddenForFullscreen)
        {
            return;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int left = workArea.Left + (workArea.Width - desiredSize.Width) / 2;
        left = Math.Max(workArea.Left, Math.Min(left, workArea.Right - desiredSize.Width));
        int top = this.dockAutoHideAnimating ? this.dockAutoHideAnimatedTop : CalculateDockTop(desiredSize, this.dockAutoHidden);
        if (this.Size != desiredSize)
        {
            this.Size = desiredSize;
        }

        this.Location = new Point(left, top);

        NativeMethods.SetWindowPos(
            this.Handle,
            this.currentSettings.VisibilityMode == WidgetVisibilityMode.DesktopOnly ? NativeMethods.HWND_TOP : NativeMethods.HWND_TOPMOST,
            left,
            top,
            desiredSize.Width,
            desiredSize.Height,
            NativeMethods.SWP_NOACTIVATE |
            NativeMethods.SWP_NOOWNERZORDER |
            NativeMethods.SWP_FRAMECHANGED |
            NativeMethods.SWP_SHOWWINDOW);
    }

    private int CalculateDockTop(Size desiredSize, bool hidden)
    {
        if (hidden)
        {
            return Screen.PrimaryScreen.Bounds.Bottom;
        }

        Rectangle workArea = Screen.PrimaryScreen.WorkingArea;
        int top = workArea.Bottom - desiredSize.Height - this.currentSettings.DockBottomMargin;
        return Math.Max(workArea.Top, Math.Min(top, workArea.Bottom - desiredSize.Height));
    }

    private Size GetDesiredDockSize()
    {
        int count = this.runtimeItems.Count;
        int maxIcon = GetMaxIconSize();
        int baseItem = GetBaseDockItemSize();
        int gap = GetItemGap();
        int padX = GetDockPadding();
        int mediaWidth = GetMediaWidth(baseItem);
        int mediaGap = count > 0 ? S(8) : 0;
        int separatorSpace = HasPinnedRunningSeparator() ? S(14) : 0;
        int systemReserved = GetSystemButtonsWidth(baseItem);
        int systemSectionGap = count > 0 ? GetSystemSectionGap() : 0;
        int quotaGap = GetItemGap();
        int rightReserved =
            quotaGap +
            GetQuotaWidgetWidth(baseItem) +
            GetActionSectionGap() +
            GetActionButtonColumnWidth(baseItem);
        int itemGaps = Math.Max(0, this.runtimePinnedCount - 1) * gap +
            Math.Max(0, count - this.runtimePinnedCount - 1) * gap;
        int width = padX * 2 + systemReserved + systemSectionGap + count * maxIcon + itemGaps + separatorSpace + mediaGap + mediaWidth + rightReserved;
        int maxWidth = Math.Max(S(260), Screen.PrimaryScreen.WorkingArea.Width - S(16));
        width = Math.Min(width, maxWidth);
        int height = Math.Max(maxIcon + GetDockPadding() * 2, GetMediaHeight(baseItem) + GetDockPadding() * 2);
        return new Size(Math.Max(S(150), width), Math.Max(S(34), height));
    }

    private int GetBaseDockItemSize()
    {
        return Math.Max(S(24), this.currentSettings.DockIconSize);
    }

    private int GetMaxIconSize()
    {
        double multiplier = Math.Max(1.0, this.currentSettings.DockMagnificationPercent / 100.0);
        int size = (int)Math.Round(this.currentSettings.DockIconSize * multiplier);
        return Math.Max(S(28), size);
    }

    private int GetItemGap()
    {
        return S(8);
    }

    private int GetSystemButtonCount()
    {
        return 2;
    }

    private int GetSystemButtonsWidth(float itemSize)
    {
        int count = GetSystemButtonCount();
        if (count <= 0)
        {
            return 0;
        }

        return (int)Math.Round(count * itemSize + Math.Max(0, count - 1) * GetItemGap());
    }

    private int GetQuotaWidgetWidth(float itemSize)
    {
        return Math.Max(S(82), (int)Math.Round(itemSize * 1.72f));
    }

    private int GetActionButtonCount()
    {
        return 3;
    }

    private int GetActionButtonColumnWidth(float itemSize)
    {
        return Math.Max(S(24), (int)Math.Round(itemSize * 0.48f));
    }

    private int GetActionSectionGap()
    {
        return S(2);
    }

    private int GetActionButtonGap()
    {
        return S(3);
    }

    private int GetSystemSectionGap()
    {
        return S(10);
    }

    private int GetDockPadding()
    {
        return S(6);
    }

    private int GetMediaWidth(float itemSize)
    {
        return (int)Math.Round(itemSize * 4.55f + GetItemGap() * 2.0f);
    }

    private int GetMediaHeight(float itemSize)
    {
        return Math.Max(S(24), (int)Math.Round(itemSize));
    }

    private bool HasPinnedRunningSeparator()
    {
        return this.runtimePinnedCount > 0 && this.runtimeItems.Count > this.runtimePinnedCount;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawDock(e.Graphics);
    }

    private void DrawDock(Graphics g)
    {
        ConfigureDockGraphics(g);
        DrawDockBackground(g);
        DrawDockContent(g);
    }

    private void ConfigureDockGraphics(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    private void DrawDockBackground(Graphics g)
    {
        int alpha = GetBackgroundOpacityAlpha();
        RectangleF shellRect = new RectangleF(0, S(1), this.Width - 1, this.Height - S(2));
        using (GraphicsPath shell = RoundedRectangle(shellRect, Math.Min(shellRect.Height / 2.0f, S(25))))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(alpha, 18, 19, 22)))
        using (Pen outline = new Pen(Color.FromArgb(95, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, shell);
            g.DrawPath(outline, shell);
        }
    }

    private void DrawDockContent(Graphics g)
    {
        int contentAlpha = GetContentOpacityAlpha();
        if (contentAlpha <= 0)
        {
            return;
        }

        if (contentAlpha < 255)
        {
            using (Bitmap contentBitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
            using (Graphics contentGraphics = Graphics.FromImage(contentBitmap))
            {
                ConfigureDockGraphics(contentGraphics);
                contentGraphics.Clear(Color.Transparent);
                DrawDockItems(contentGraphics);
                DrawingUtil.DrawImageWithAlpha(g, contentBitmap, contentAlpha);
            }

            return;
        }

        DrawDockItems(g);
    }

    private void DrawDockItems(Graphics g)
    {
        Point cursor = Cursor.Position;
        Point client = this.PointToClient(cursor);
        int hoverIndex;
        DockLayout layout = CalculateDockLayout(new PointF(client.X, client.Y), this.Bounds.Contains(cursor), out hoverIndex);
        DrawSystemButtons(g, layout.SystemButtonRects, new PointF(client.X, client.Y), this.Bounds.Contains(cursor));
        RectangleF[] rects = layout.ItemRects;
        DrawDockSeparator(g, layout.SeparatorRect);
        DateTime now = DateTime.UtcNow;

        for (int i = 0; i < this.runtimeItems.Count; i++)
        {
            RectangleF rect = rects[i];
            DockRuntimeItem item = this.runtimeItems[i];
            float alpha = 1.0f;
            float yOffset = 0.0f;
            if (!GetRuntimeItemAnimationVisuals(item, rect, now, out alpha, out yOffset))
            {
                continue;
            }

            RectangleF animatedRect = new RectangleF(rect.Left, rect.Top + yOffset, rect.Width, rect.Height);
            bool hovered = hoverIndex == i && item.AnimationState == DockItemAnimationState.Normal;
            DrawRuntimeItem(g, i, item, animatedRect, hovered, alpha);
        }

        DrawDockDropIndicator(g);
        DrawMediaControls(g, layout);
        DrawQuotaWidget(g, layout.QuotaRect);
        DrawDockActionButtons(g, layout.ActionButtonRects);
    }

    private bool GetRuntimeItemAnimationVisuals(DockRuntimeItem item, RectangleF rect, DateTime now, out float alpha, out float yOffset)
    {
        alpha = 1.0f;
        yOffset = 0.0f;
        if (item == null)
        {
            return false;
        }

        if (item.AnimationState == DockItemAnimationState.EnteringPending)
        {
            alpha = 0.0f;
            yOffset = rect.Height * 0.62f;
            return false;
        }

        if (item.AnimationState == DockItemAnimationState.Entering)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - item.AnimationStartedUtc).TotalMilliseconds / DockItemEnterAnimationMs));
            double eased = EaseOutCubic(progress);
            alpha = (float)eased;
            yOffset = (float)(rect.Height * 0.62f * (1.0 - eased));
            return alpha > 0.01f;
        }

        if (item.AnimationState == DockItemAnimationState.Exiting)
        {
            double progress = Math.Max(0.0, Math.Min(1.0, (now - item.AnimationStartedUtc).TotalMilliseconds / DockItemExitAnimationMs));
            double eased = EaseInCubic(progress);
            alpha = (float)(1.0 - eased);
            yOffset = (float)(rect.Height * 0.62f * eased);
            return alpha > 0.01f;
        }

        return true;
    }

    private void DrawRuntimeItem(Graphics g, int index, DockRuntimeItem item, RectangleF rect, bool hovered, float alpha)
    {
        int alphaByte = Math.Max(0, Math.Min(255, (int)Math.Round(255.0f * alpha)));
        if (alphaByte <= 0)
        {
            return;
        }

        DrawDockItemTile(g, rect, hovered, alphaByte);

        Bitmap bitmap = AppIconCache.GetBitmap(item.IconKey);
        if (bitmap == null)
        {
            bitmap = item.Bitmap;
        }

        if (bitmap != null)
        {
            RectangleF iconRect = GetIconRect(rect);
            DrawingUtil.DrawImageWithAlpha(g, bitmap, iconRect, alphaByte);
        }

        if (item.IsRunning)
        {
            DrawRunningIndicator(g, rect, item.IsFocused, alphaByte);
        }

        if (item.InstanceCount > 1)
        {
            DrawInstanceBadge(g, rect, item.InstanceCount, alphaByte);
        }

        if (this.dockUnpinMode &&
            index >= 0 &&
            index < this.runtimePinnedCount &&
            item.AnimationState != DockItemAnimationState.Exiting)
        {
            DrawUnpinBadge(g, GetUnpinBadgeRect(rect), alphaByte);
        }
    }

    private void DrawSystemButtons(Graphics g, RectangleF[] rects, PointF cursor, bool hasCursor)
    {
        if (rects == null)
        {
            return;
        }

        for (int i = 0; i < rects.Length; i++)
        {
            RectangleF rect = rects[i];
            bool hovered = hasCursor && rect.Contains(cursor.X, cursor.Y);
            DrawDockItemTile(g, rect, hovered);
            if (i == 0)
            {
                DrawShowDesktopGlyph(g, rect);
            }
            else if (i == 1)
            {
                DrawStartMenuGlyph(g, rect);
            }
        }
    }

    private void DrawDockItemTile(Graphics g, RectangleF rect, bool hovered)
    {
        DrawDockItemTile(g, rect, hovered, 255);
    }

    private void DrawDockItemTile(Graphics g, RectangleF rect, bool hovered, int alpha)
    {
        alpha = Math.Max(0, Math.Min(255, alpha));
        if (alpha <= 0)
        {
            return;
        }

        float radius = Math.Max(S(5), rect.Height * 0.25f);
        RectangleF shadow = new RectangleF(rect.Left + rect.Width * 0.12f, rect.Bottom - S(2), rect.Width * 0.76f, S(4));
        using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(ScaleAlpha(80, alpha), 0, 0, 0)))
        {
            g.FillEllipse(shadowBrush, shadow);
        }

        using (GraphicsPath path = RoundedRectangle(rect, radius))
        using (SolidBrush background = new SolidBrush(hovered ? Color.FromArgb(ScaleAlpha(116, alpha), 82, 211, 255) : Color.FromArgb(ScaleAlpha(74, alpha), 248, 250, 255)))
        using (Pen border = new Pen(Color.FromArgb(ScaleAlpha(hovered ? 100 : 45, alpha), 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }
    }

    private static int ScaleAlpha(int value, int alpha)
    {
        return Math.Max(0, Math.Min(255, (int)Math.Round(value * alpha / 255.0)));
    }

    private RectangleF GetIconRect(RectangleF tileRect)
    {
        float inset = Math.Max(2.0f, tileRect.Height * 0.14f);
        return new RectangleF(
            tileRect.Left + inset,
            tileRect.Top + inset,
            Math.Max(1.0f, tileRect.Width - inset * 2.0f),
            Math.Max(1.0f, tileRect.Height - inset * 2.0f));
    }

    private void DrawShowDesktopGlyph(Graphics g, RectangleF rect)
    {
        RectangleF icon = GetIconRect(rect);
        float stroke = Math.Max(1.5f, icon.Width * 0.055f);
        RectangleF monitor = new RectangleF(
            icon.Left + icon.Width * 0.14f,
            icon.Top + icon.Height * 0.18f,
            icon.Width * 0.72f,
            icon.Height * 0.50f);
        using (Pen pen = new Pen(Color.FromArgb(235, 238, 245, 250), stroke))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            g.DrawRectangle(pen, monitor.X, monitor.Y, monitor.Width, monitor.Height);
            float standX = monitor.Left + monitor.Width / 2.0f;
            float standTop = monitor.Bottom;
            float standBottom = icon.Top + icon.Height * 0.82f;
            g.DrawLine(pen, standX, standTop, standX, standBottom);
            g.DrawLine(pen, icon.Left + icon.Width * 0.32f, standBottom, icon.Left + icon.Width * 0.68f, standBottom);
        }

        using (SolidBrush brush = new SolidBrush(Color.FromArgb(235, 85, 211, 255)))
        {
            PointF[] arrow = new PointF[]
            {
                new PointF(icon.Left + icon.Width * 0.50f, icon.Top + icon.Height * 0.38f),
                new PointF(icon.Left + icon.Width * 0.64f, icon.Top + icon.Height * 0.52f),
                new PointF(icon.Left + icon.Width * 0.56f, icon.Top + icon.Height * 0.52f),
                new PointF(icon.Left + icon.Width * 0.56f, icon.Top + icon.Height * 0.65f),
                new PointF(icon.Left + icon.Width * 0.44f, icon.Top + icon.Height * 0.65f),
                new PointF(icon.Left + icon.Width * 0.44f, icon.Top + icon.Height * 0.52f),
                new PointF(icon.Left + icon.Width * 0.36f, icon.Top + icon.Height * 0.52f)
            };
            g.FillPolygon(brush, arrow);
        }
    }

    private void DrawStartMenuGlyph(Graphics g, RectangleF rect)
    {
        RectangleF icon = GetIconRect(rect);
        float gap = Math.Max(2.0f, icon.Width * 0.06f);
        float paneWidth = (icon.Width - gap) / 2.0f;
        float paneHeight = (icon.Height - gap) / 2.0f;
        using (LinearGradientBrush brush = new LinearGradientBrush(icon, Color.FromArgb(245, 32, 189, 255), Color.FromArgb(245, 68, 126, 255), LinearGradientMode.ForwardDiagonal))
        {
            g.FillRectangle(brush, icon.Left, icon.Top, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left + paneWidth + gap, icon.Top, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left, icon.Top + paneHeight + gap, paneWidth, paneHeight);
            g.FillRectangle(brush, icon.Left + paneWidth + gap, icon.Top + paneHeight + gap, paneWidth, paneHeight);
        }
    }

    private DockLayout CalculateDockLayout(PointF cursor, bool hasCursor, out int hoverIndex)
    {
        hoverIndex = -1;
        int count = this.runtimeItems.Count;
        DockLayout layout = new DockLayout();
        layout.SystemButtonRects = new RectangleF[GetSystemButtonCount()];
        layout.ItemRects = new RectangleF[count];
        layout.ActionButtonRects = new RectangleF[GetActionButtonCount()];
        float padX = GetDockPadding();
        float gap = GetItemGap();
        float mediaGap = count > 0 ? S(8) : 0;
        bool hasSeparator = HasPinnedRunningSeparator();
        float separatorSpace = hasSeparator ? S(14) : 0;
        int pinnedCount = Math.Min(this.runtimePinnedCount, count);
        int runningCount = Math.Max(0, count - pinnedCount);
        float itemGaps = Math.Max(0, pinnedCount - 1) * gap + Math.Max(0, runningCount - 1) * gap;
        float configuredBaseIcon = GetBaseDockItemSize();
        float systemButtonSize = configuredBaseIcon;
        float systemReserved = GetSystemButtonsWidth(systemButtonSize);
        float systemSectionGap = count > 0 ? GetSystemSectionGap() : 0;
        float quotaGap = GetItemGap();
        float quotaWidth = GetQuotaWidgetWidth(systemButtonSize);
        float actionGap = GetActionSectionGap();
        float actionWidth = GetActionButtonColumnWidth(systemButtonSize);
        float rightReserved = quotaGap + quotaWidth + actionGap + actionWidth;
        float mediaItemSize = configuredBaseIcon;
        float mediaWidth = GetMediaWidth(mediaItemSize);
        float mediaHeight = GetMediaHeight(mediaItemSize);
        float availableIcons = 0;
        float maxIcon = 0;
        float baseIcon = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            mediaWidth = GetMediaWidth(mediaItemSize);
            mediaHeight = GetMediaHeight(mediaItemSize);
            availableIcons = Math.Max(S(24), this.Width - padX * 2.0f - systemReserved - systemSectionGap - itemGaps - separatorSpace - mediaGap - mediaWidth - rightReserved);
            maxIcon = count > 0 ? Math.Max(S(24), Math.Min(GetMaxIconSize(), availableIcons / count)) : 0;
            baseIcon = count > 0 ? Math.Max(S(24), Math.Min(configuredBaseIcon, maxIcon)) : configuredBaseIcon;
            mediaItemSize = baseIcon;
        }

        float total = systemReserved + systemSectionGap + count * maxIcon + itemGaps + separatorSpace + mediaGap + mediaWidth + rightReserved;
        float x = Math.Max(padX, (this.Width - total) / 2.0f);
        float itemCenterY = this.Height / 2.0f;
        float influence = Math.Max(baseIcon * 1.8f, S(48));
        double bestProgress = 0.0;

        for (int i = 0; i < layout.SystemButtonRects.Length; i++)
        {
            layout.SystemButtonRects[i] = new RectangleF(x, itemCenterY - systemButtonSize / 2.0f, systemButtonSize, systemButtonSize);
            x += systemButtonSize + (i < layout.SystemButtonRects.Length - 1 ? gap : 0);
        }

        x += systemSectionGap;

        for (int i = 0; i < pinnedCount; i++)
        {
            layout.ItemRects[i] = CalculateItemRect(i, x, maxIcon, baseIcon, itemCenterY, influence, cursor, hasCursor, ref hoverIndex, ref bestProgress);
            x += maxIcon + (i < pinnedCount - 1 ? gap : 0);
        }

        if (hasSeparator)
        {
            float lineX = x + separatorSpace / 2.0f;
            float lineHeight = Math.Max(S(18), this.Height - S(14));
            layout.SeparatorRect = new RectangleF(lineX, (this.Height - lineHeight) / 2.0f, Math.Max(1.0f, this.scale), lineHeight);
            x += separatorSpace;
        }

        for (int i = 0; i < runningCount; i++)
        {
            int itemIndex = pinnedCount + i;
            layout.ItemRects[itemIndex] = CalculateItemRect(itemIndex, x, maxIcon, baseIcon, itemCenterY, influence, cursor, hasCursor, ref hoverIndex, ref bestProgress);
            x += maxIcon + (i < runningCount - 1 ? gap : 0);
        }

        if (bestProgress <= 0.05)
        {
            hoverIndex = -1;
        }

        if (count > 0)
        {
            x += mediaGap;
        }

        layout.MediaRect = new RectangleF(x, itemCenterY - mediaHeight / 2.0f, mediaWidth, mediaHeight);
        layout.QuotaRect = new RectangleF(layout.MediaRect.Right + quotaGap, itemCenterY - mediaHeight / 2.0f, quotaWidth, mediaHeight);
        RectangleF actionColumn = new RectangleF(layout.QuotaRect.Right + actionGap, itemCenterY - mediaHeight / 2.0f, actionWidth, mediaHeight);
        CalculateActionButtonRects(layout, actionColumn);
        CalculateMediaButtonRects(layout);
        return layout;
    }

    private void CalculateActionButtonRects(DockLayout layout, RectangleF column)
    {
        if (layout.ActionButtonRects == null)
        {
            return;
        }

        int count = layout.ActionButtonRects.Length;
        if (count <= 0)
        {
            return;
        }

        float gap = GetActionButtonGap();
        float buttonHeight = Math.Max(1.0f, (column.Height - gap * Math.Max(0, count - 1)) / count);
        float y = column.Top;
        for (int i = 0; i < count; i++)
        {
            layout.ActionButtonRects[i] = new RectangleF(column.Left, y, column.Width, buttonHeight);
            y += buttonHeight + gap;
        }
    }

    private RectangleF CalculateItemRect(
        int itemIndex,
        float slotLeft,
        float maxIcon,
        float baseIcon,
        float itemCenterY,
        float influence,
        PointF cursor,
        bool hasCursor,
        ref int hoverIndex,
        ref double bestProgress)
    {
        float itemCenterX = slotLeft + maxIcon / 2.0f;
        double progress = 0.0;
        if (hasCursor && maxIcon > 0)
        {
            double distance = Math.Abs(cursor.X - itemCenterX);
            if (distance < influence)
            {
                progress = 1.0 - distance / influence;
                progress = Math.Sin(progress * Math.PI / 2.0);
            }
        }

        if (progress > bestProgress)
        {
            bestProgress = progress;
            hoverIndex = itemIndex;
        }

        double maxFactor = baseIcon <= 0 ? 1.0 : Math.Max(1.0, maxIcon / baseIcon);
        float iconSize = (float)Math.Min(maxIcon, baseIcon * (1.0 + (maxFactor - 1.0) * progress));
        return new RectangleF(
            itemCenterX - iconSize / 2.0f,
            itemCenterY - iconSize / 2.0f,
            iconSize,
            iconSize);
    }

    private int FindItemAtPoint(PointF point)
    {
        int hoverIndex;
        RectangleF[] rects = CalculateDockLayout(point, true, out hoverIndex).ItemRects;
        for (int i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateDockDropState(DragEventArgs e)
    {
        if (e == null)
        {
            ClearDockDropState();
            return;
        }

        List<DockItem> items = AppDropHelper.BuildItemsFromData(e.Data);
        if (items.Count == 0)
        {
            e.Effect = DragDropEffects.None;
            ClearDockDropState();
            return;
        }

        Point client = this.PointToClient(new Point(e.X, e.Y));
        PointF point = new PointF(client.X, client.Y);
        int hoverIndex;
        DockLayout layout = CalculateDockLayout(point, true, out hoverIndex);
        bool targetLaunchpad = layout.SystemButtonRects != null &&
            layout.SystemButtonRects.Length > 1 &&
            layout.SystemButtonRects[1].Contains(point.X, point.Y);
        if (!targetLaunchpad && !IsDockPinnedDropPoint(layout, point))
        {
            e.Effect = DragDropEffects.None;
            ClearDockDropState();
            return;
        }

        int insertionIndex = targetLaunchpad ? -1 : CalculateDockDropInsertionIndex(layout, point);
        RectangleF indicator = targetLaunchpad ? RectangleF.Empty : GetDockDropIndicatorRect(layout, insertionIndex);
        bool changed =
            !this.dockDropActive ||
            this.dockDropTargetsLaunchpad != targetLaunchpad ||
            this.dockDropInsertionIndex != insertionIndex ||
            !SameRect(this.dockDropIndicatorRect, indicator);

        this.dockDropActive = true;
        this.dockDropTargetsLaunchpad = targetLaunchpad;
        this.dockDropInsertionIndex = insertionIndex;
        this.dockDropIndicatorRect = indicator;
        e.Effect = DragDropEffects.Copy;

        if (changed)
        {
            RenderLayeredWindow();
        }
    }

    private void ClearDockDropState()
    {
        if (!this.dockDropActive &&
            !this.dockDropTargetsLaunchpad &&
            this.dockDropInsertionIndex < 0 &&
            this.dockDropIndicatorRect.IsEmpty)
        {
            return;
        }

        this.dockDropActive = false;
        this.dockDropTargetsLaunchpad = false;
        this.dockDropInsertionIndex = -1;
        this.dockDropIndicatorRect = RectangleF.Empty;
        RenderLayeredWindow();
    }

    private int CalculateDockDropInsertionIndex(DockLayout layout, PointF point)
    {
        int pinnedCount = Math.Min(this.runtimePinnedCount, layout.ItemRects == null ? 0 : layout.ItemRects.Length);
        if (pinnedCount <= 0)
        {
            return 0;
        }

        for (int i = 0; i < pinnedCount; i++)
        {
            RectangleF rect = layout.ItemRects[i];
            if (point.X < rect.Left + rect.Width / 2.0f)
            {
                return i;
            }
        }

        return pinnedCount;
    }

    private bool IsDockPinnedDropPoint(DockLayout layout, PointF point)
    {
        if (layout == null)
        {
            return false;
        }

        if (!layout.SeparatorRect.IsEmpty && point.X >= layout.SeparatorRect.Left)
        {
            return false;
        }

        int pinnedCount = Math.Min(this.runtimePinnedCount, layout.ItemRects == null ? 0 : layout.ItemRects.Length);
        if (pinnedCount > 0)
        {
            float left = layout.ItemRects[0].Left - GetItemGap();
            float right = layout.SeparatorRect.IsEmpty ?
                layout.ItemRects[pinnedCount - 1].Right + GetItemGap() :
                layout.SeparatorRect.Left;
            return point.X >= left && point.X <= right;
        }

        if (layout.ItemRects != null && layout.ItemRects.Length > 0)
        {
            float left = layout.ItemRects[0].Left - GetItemGap();
            float right = layout.ItemRects[0].Left + GetItemGap();
            return point.X >= left && point.X <= right;
        }

        if (!layout.MediaRect.IsEmpty)
        {
            return point.X < layout.MediaRect.Left;
        }

        return true;
    }

    private RectangleF GetDockDropIndicatorRect(DockLayout layout, int insertionIndex)
    {
        float lineX;
        int pinnedCount = Math.Min(this.runtimePinnedCount, layout.ItemRects == null ? 0 : layout.ItemRects.Length);
        if (pinnedCount > 0)
        {
            if (insertionIndex <= 0)
            {
                lineX = layout.ItemRects[0].Left - GetItemGap() / 2.0f;
            }
            else if (insertionIndex >= pinnedCount)
            {
                lineX = layout.ItemRects[pinnedCount - 1].Right + GetItemGap() / 2.0f;
            }
            else
            {
                lineX = (layout.ItemRects[insertionIndex - 1].Right + layout.ItemRects[insertionIndex].Left) / 2.0f;
            }
        }
        else if (layout.ItemRects != null && layout.ItemRects.Length > 0)
        {
            lineX = layout.ItemRects[0].Left - GetItemGap() / 2.0f;
        }
        else if (!layout.MediaRect.IsEmpty)
        {
            lineX = layout.MediaRect.Left - S(8);
        }
        else
        {
            lineX = this.Width / 2.0f;
        }

        float lineHeight = Math.Max(S(34), this.Height - GetDockPadding() * 2.0f);
        return new RectangleF(
            lineX - Math.Max(1.0f, this.scale),
            (this.Height - lineHeight) / 2.0f,
            Math.Max(2.0f, this.scale * 2.0f),
            lineHeight);
    }

    private static bool SameRect(RectangleF left, RectangleF right)
    {
        return Math.Abs(left.X - right.X) < 0.5f &&
            Math.Abs(left.Y - right.Y) < 0.5f &&
            Math.Abs(left.Width - right.Width) < 0.5f &&
            Math.Abs(left.Height - right.Height) < 0.5f;
    }

    private void DrawDockDropIndicator(Graphics g)
    {
        if (!this.dockDropActive || this.dockDropTargetsLaunchpad || this.dockDropIndicatorRect.IsEmpty)
        {
            return;
        }

        using (Pen shadow = new Pen(Color.FromArgb(120, 0, 0, 0), Math.Max(3.0f, this.scale * 3.0f)))
        using (Pen line = new Pen(Color.FromArgb(235, 94, 211, 255), Math.Max(2.0f, this.scale * 2.0f)))
        {
            shadow.DashStyle = DashStyle.Custom;
            shadow.DashCap = DashCap.Round;
            shadow.DashPattern = new float[] { 2.0f, 2.0f };
            line.DashStyle = DashStyle.Custom;
            line.DashCap = DashCap.Round;
            line.DashPattern = new float[] { 2.0f, 2.0f };
            float x = this.dockDropIndicatorRect.Left + this.dockDropIndicatorRect.Width / 2.0f;
            g.DrawLine(shadow, x, this.dockDropIndicatorRect.Top, x, this.dockDropIndicatorRect.Bottom);
            g.DrawLine(line, x, this.dockDropIndicatorRect.Top, x, this.dockDropIndicatorRect.Bottom);
        }
    }

    private void AddDraggedItemsToDock(IList<DockItem> items, int insertionIndex)
    {
        int changedCount;
        string updated = AppDropHelper.InsertOrMoveItemsText(this.currentSettings.DockItemsText, items, insertionIndex, out changedCount);
        if (changedCount <= 0)
        {
            return;
        }

        this.currentSettings.DockItemsText = updated;
        this.currentSettings.DockEnabled = true;
        this.currentSettings.Save();
        SyncRuntimeItemsAfterDockPinnedChange();
        RenderLayeredWindow();
        Program.LogInfo("Dock drag-add/move completed. Count=" + changedCount.ToString(CultureInfo.InvariantCulture));
    }

    private void AddDraggedItemsToLaunchpad(IList<DockItem> items)
    {
        int addedCount;
        string updated = AppDropHelper.AppendItemsText(
            this.currentSettings.LaunchpadItemsText,
            items,
            this.currentSettings.DockItemsText,
            out addedCount);
        if (addedCount <= 0)
        {
            return;
        }

        this.currentSettings.LaunchpadItemsText = updated;
        this.currentSettings.Save();
        if (this.launchpadForm != null && !this.launchpadForm.IsDisposed && this.launchpadForm.IsLaunchpadVisible)
        {
            this.launchpadForm.UpdatePinnedItemsText(this.currentSettings.LaunchpadItemsText, this.currentSettings.DockItemsText);
        }

        Program.LogInfo("Launchpad drag-add completed. Count=" + addedCount.ToString(CultureInfo.InvariantCulture));
    }

    private int FindSystemButtonAtPoint(PointF point)
    {
        int hoverIndex;
        RectangleF[] rects = CalculateDockLayout(point, true, out hoverIndex).SystemButtonRects;
        if (rects == null)
        {
            return -1;
        }

        for (int i = 0; i < rects.Length; i++)
        {
            if (rects[i].Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    private void ActivateSystemButton(int index)
    {
        if (index == 0)
        {
            NativeMethods.ToggleDesktop();
        }
        else if (index == 1)
        {
            ToggleLaunchpad();
        }
    }

    private void EnsureLaunchpadForm()
    {
        if (this.launchpadForm != null && !this.launchpadForm.IsDisposed)
        {
            return;
        }

        this.launchpadForm = new LaunchpadForm();
    }

    private void ToggleLaunchpad()
    {
        EnsureLaunchpadForm();
        if (this.launchpadForm == null || this.launchpadForm.IsDisposed)
        {
            return;
        }

        if (this.launchpadForm.IsLaunchpadVisible)
        {
            this.launchpadForm.HideLaunchpad();
            return;
        }

        bool topMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        this.launchpadForm.ShowLaunchpad(
            Screen.PrimaryScreen.WorkingArea,
            this.Bounds,
            topMost,
            this.currentSettings.LaunchpadItemsText,
            this.currentSettings.DockItemsText);
    }

    private void HideLaunchpad()
    {
        if (this.launchpadForm != null && !this.launchpadForm.IsDisposed)
        {
            this.launchpadForm.HideLaunchpad();
        }
    }

    private void CloseLaunchpadForm()
    {
        if (this.launchpadForm != null)
        {
            this.launchpadForm.Close();
            this.launchpadForm.Dispose();
            this.launchpadForm = null;
        }
    }

    private void RepositionLaunchpad()
    {
        if (this.launchpadForm != null && !this.launchpadForm.IsDisposed && this.launchpadForm.IsLaunchpadVisible)
        {
            this.launchpadForm.PositionLaunchpad(Screen.PrimaryScreen.WorkingArea, this.Bounds);
        }
    }

    private void UpdatePreviewFromCursor(Point cursor)
    {
        if (this.hiddenForFullscreen || !this.Visible)
        {
            HidePreview();
            return;
        }

        if (this.Bounds.Contains(cursor))
        {
            Point client = this.PointToClient(cursor);
            int hoverIndex;
            DockLayout layout = CalculateDockLayout(new PointF(client.X, client.Y), true, out hoverIndex);
            int index = FindItemAtPoint(new PointF(client.X, client.Y));
            if (index >= 0 && index < this.runtimeItems.Count)
            {
                DockRuntimeItem item = this.runtimeItems[index];
                if (item.IsRunning && item.Windows.Count > 0)
                {
                    ShowPreviewForItem(index, item, layout.ItemRects[index]);
                    return;
                }
            }

            SchedulePreviewHide();
            return;
        }

        if (this.previewForm != null && !this.previewForm.IsDisposed && this.previewForm.ContainsScreenPoint(cursor))
        {
            CancelPreviewHide();
            return;
        }

        SchedulePreviewHide();
    }

    private void ShowPreviewForItem(int index, DockRuntimeItem item, RectangleF itemRect)
    {
        if (item == null || item.Windows.Count == 0)
        {
            HidePreview();
            return;
        }

        CancelPreviewHide();
        if (this.previewForm == null || this.previewForm.IsDisposed)
        {
            EnsurePreviewForm();
        }

        Rectangle anchor = Rectangle.Round(itemRect);
        anchor.Location = this.PointToScreen(anchor.Location);
        bool topMost = this.currentSettings.VisibilityMode != WidgetVisibilityMode.DesktopOnly;
        this.previewForm.ShowPreview(this, item.Windows, anchor, topMost);
        this.previewItemIndex = index;
    }

    private void SchedulePreviewHide()
    {
        if (this.previewForm == null || this.previewForm.IsDisposed || !this.previewForm.Visible)
        {
            this.previewHidePending = false;
            return;
        }

        if (this.previewHidePending)
        {
            return;
        }

        this.previewHidePending = true;
        this.previewHideDeadlineUtc = DateTime.UtcNow.AddMilliseconds(PreviewHideDelayMs);
    }

    private void CancelPreviewHide()
    {
        this.previewHidePending = false;
    }

    private bool UpdatePendingPreviewHide(DateTime now, Point cursor)
    {
        if (!this.previewHidePending)
        {
            return false;
        }

        if (this.previewForm != null && !this.previewForm.IsDisposed && this.previewForm.ContainsScreenPoint(cursor))
        {
            CancelPreviewHide();
            return false;
        }

        if (now < this.previewHideDeadlineUtc)
        {
            return false;
        }

        HidePreview();
        return true;
    }

    private void EnsurePreviewForm()
    {
        if (this.previewForm != null && !this.previewForm.IsDisposed)
        {
            return;
        }

        this.previewForm = new DockPreviewForm();
        this.previewForm.PrepareHidden();
    }

    private void HidePreview()
    {
        this.previewHidePending = false;
        this.previewItemIndex = -1;
        if (this.previewForm != null && !this.previewForm.IsDisposed)
        {
            this.previewForm.HidePreview();
        }
    }

    private void ClosePreviewForm()
    {
        if (this.previewForm != null)
        {
            this.previewForm.Close();
            this.previewForm.Dispose();
            this.previewForm = null;
        }

        this.previewHidePending = false;
        this.previewItemIndex = -1;
    }

    private void DrawDockSeparator(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.DrawLine(pen, rect.Left, rect.Top, rect.Left, rect.Bottom);
        }
    }

    private void DrawRunningIndicator(Graphics g, RectangleF iconRect, bool focused)
    {
        DrawRunningIndicator(g, iconRect, focused, 255);
    }

    private void DrawRunningIndicator(Graphics g, RectangleF iconRect, bool focused, int alpha)
    {
        float height = Math.Max(S(3), iconRect.Width * 0.10f);
        float width = focused ? Math.Max(S(12), iconRect.Width * 0.50f) : height;
        RectangleF dot = new RectangleF(iconRect.Left + (iconRect.Width - width) / 2.0f, this.Height - S(5), width, height);
        using (GraphicsPath path = RoundedRectangle(dot, height / 2.0f))
        using (SolidBrush brush = new SolidBrush(focused ? Color.FromArgb(ScaleAlpha(238, alpha), 82, 211, 255) : Color.FromArgb(ScaleAlpha(210, alpha), 142, 149, 158)))
        {
            g.FillPath(brush, path);
        }
    }

    private void DrawInstanceBadge(Graphics g, RectangleF iconRect, int count)
    {
        DrawInstanceBadge(g, iconRect, count, 255);
    }

    private void DrawInstanceBadge(Graphics g, RectangleF iconRect, int count, int alpha)
    {
        string text = Math.Min(count, 99).ToString(CultureInfo.InvariantCulture);
        float height = Math.Max(S(14), iconRect.Height * 0.28f);
        float width = Math.Max(height, height + Math.Max(0, text.Length - 1) * S(6));
        RectangleF badge = new RectangleF(iconRect.Right - width * 0.78f, iconRect.Bottom - height * 0.76f, width, height);
        using (GraphicsPath path = RoundedRectangle(badge, height / 2.0f))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(ScaleAlpha(230, alpha), 154, 160, 170)))
        using (Pen border = new Pen(Color.FromArgb(ScaleAlpha(150, alpha), 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        using (Font font = new Font("Segoe UI", Math.Max(8.0f, height * 0.58f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(ScaleAlpha(245, alpha), 248, 250, 252)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            g.DrawString(text, font, brush, badge, format);
        }
    }

    private void CalculateMediaButtonRects(DockLayout layout)
    {
        RectangleF media = layout.MediaRect;
        RectangleF content = GetMediaRightContentRect(media);
        float buttonGap = Math.Max(S(5), Math.Min(S(9), content.Width * 0.045f));
        float buttonHeight = Math.Max(S(22), Math.Min(S(34), media.Height * 0.39f));
        float buttonTop = media.Bottom - buttonHeight - S(5);
        float buttonLeft = content.Left + buttonGap;
        float buttonWidth = Math.Max(1.0f, content.Width - buttonGap * 4.0f);
        float third = buttonWidth / 3.0f;

        layout.PreviousRect = new RectangleF(buttonLeft, buttonTop, third, buttonHeight);
        layout.PlayPauseRect = new RectangleF(buttonLeft + third + buttonGap, buttonTop, third, buttonHeight);
        layout.NextRect = new RectangleF(buttonLeft + (third + buttonGap) * 2.0f, buttonTop, third, buttonHeight);
        this.mediaPreviousRect = layout.PreviousRect;
        this.mediaPlayPauseRect = layout.PlayPauseRect;
        this.mediaNextRect = layout.NextRect;
    }

    private RectangleF GetMediaRightContentRect(RectangleF media)
    {
        float leftSectionWidth = media.Width / 3.0f;
        RectangleF rightSection = new RectangleF(media.Left + leftSectionWidth, media.Top, media.Width - leftSectionWidth, media.Height);
        float inset = S(7);
        return new RectangleF(
            rightSection.Left + inset,
            rightSection.Top,
            Math.Max(1.0f, rightSection.Width - inset * 2.0f),
            rightSection.Height);
    }

    private RectangleF GetMediaVisualRect(RectangleF logoArea, Bitmap bitmap, bool isArtwork)
    {
        if (isArtwork)
        {
            float pad = S(3);
            RectangleF bounds = new RectangleF(
                logoArea.Left + pad,
                logoArea.Top + pad,
                Math.Max(1.0f, logoArea.Width - pad * 2.0f),
                Math.Max(1.0f, logoArea.Height - pad * 2.0f));
            float aspect = 16.0f / 9.0f;
            if (bitmap != null && bitmap.Width > bitmap.Height && bitmap.Height > 0)
            {
                float bitmapAspect = bitmap.Width / (float)bitmap.Height;
                if (bitmapAspect >= 1.20f && bitmapAspect <= 2.20f)
                {
                    aspect = bitmapAspect;
                }
            }

            float width = bounds.Width;
            float height = width / aspect;
            if (height > bounds.Height)
            {
                height = bounds.Height;
                width = height * aspect;
            }

            return new RectangleF(
                bounds.Left + (bounds.Width - width) / 2.0f,
                bounds.Top + (bounds.Height - height) / 2.0f,
                width,
                height);
        }

        float maxIconSize = Math.Max(1.0f, Math.Min(logoArea.Width - S(2), logoArea.Height - S(2)));
        float iconSize = Math.Min(maxIconSize, Math.Max(S(48), Math.Min(Math.Min(logoArea.Width, logoArea.Height) * 0.90f, S(76))));
        return new RectangleF(
            logoArea.Left + (logoArea.Width - iconSize) / 2.0f,
            logoArea.Top + (logoArea.Height - iconSize) / 2.0f,
            iconSize,
            iconSize);
    }

    private void DrawMediaControls(Graphics g, DockLayout layout)
    {
        RectangleF rect = layout.MediaRect;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        Point cursor = this.PointToClient(Cursor.Position);
        DrawDockItemTile(g, rect, rect.Contains(cursor.X, cursor.Y));

        float leftSectionWidth = rect.Width / 3.0f;
        RectangleF logoArea = new RectangleF(rect.Left, rect.Top, leftSectionWidth, rect.Height);
        RectangleF content = GetMediaRightContentRect(rect);
        RectangleF iconRect = GetMediaVisualRect(logoArea, this.mediaAppBitmap, this.mediaBitmapIsArtwork);
        DrawMediaThumbnail(g, iconRect, this.mediaAppBitmap, this.mediaBitmapIsArtwork);

        RectangleF titleRect = new RectangleF(
            content.Left,
            content.Top + S(3),
            content.Width,
            Math.Max(S(22), layout.PreviousRect.Top - content.Top - S(5)));
        string title = BuildMediaDisplayTitle(this.mediaInfo);

        using (SolidBrush titleBrush = new SolidBrush(Color.FromArgb(244, 244, 247, 251)))
        {
            DrawMediaTitle(g, title, titleRect, titleBrush);
        }

        bool isPlaying = this.mediaInfo != null && this.mediaInfo.HasSession && this.mediaInfo.IsPlaying;
        DrawMediaButton(g, layout.PreviousRect, 0, isPlaying);
        DrawMediaButton(g, layout.PlayPauseRect, 1, isPlaying);
        DrawMediaButton(g, layout.NextRect, 2, isPlaying);
    }

    private void DrawMediaTitle(Graphics g, string title, RectangleF rect, Brush brush)
    {
        this.mediaTitleOverflow = false;
        this.mediaTitlePageCount = 0;
        this.mediaTitlePageIndex = -1;
        if (string.IsNullOrEmpty(title) || rect.Width <= 1.0f || rect.Height <= 1.0f)
        {
            return;
        }

        float maxSize = Math.Max(18.0f, Math.Min(27.0f, rect.Height * 0.88f));
        float minSize = Math.Max(12.0f, Math.Min(16.0f, rect.Height * 0.56f));
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.None;
            format.FormatFlags = StringFormatFlags.NoWrap;

            for (float size = maxSize; size >= minSize; size -= 1.0f)
            {
                using (Font font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                {
                    SizeF measured = g.MeasureString(title, font);
                    if (measured.Width <= rect.Width)
                    {
                        g.DrawString(title, font, brush, rect, format);
                        return;
                    }
                }
            }
        }

        using (Font font = new Font("Segoe UI", minSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (StringFormat marqueeFormat = new StringFormat())
        {
            List<string> pages = BuildMediaTitlePages(g, title, font, rect.Width);
            if (pages.Count <= 0)
            {
                return;
            }

            this.mediaTitleOverflow = pages.Count > 1;
            this.mediaTitlePageCount = pages.Count;
            int pageIndex = GetMediaTitlePageIndex(pages.Count, DateTime.UtcNow);
            this.mediaTitlePageIndex = pageIndex;

            marqueeFormat.Alignment = StringAlignment.Center;
            marqueeFormat.LineAlignment = StringAlignment.Center;
            marqueeFormat.Trimming = StringTrimming.None;
            marqueeFormat.FormatFlags = StringFormatFlags.NoWrap;

            GraphicsState state = g.Save();
            g.SetClip(rect);
            double phase = GetMediaTitleFlipPhase(DateTime.UtcNow);
            if (pages.Count > 1 && phase < MediaTitleFlipDurationMs)
            {
                int previousIndex = pageIndex == 0 ? pages.Count - 1 : pageIndex - 1;
                double progress = Math.Max(0.0, Math.Min(1.0, phase / MediaTitleFlipDurationMs));
                double eased = 1.0 - Math.Pow(1.0 - progress, 3.0);
                float offset = (float)(rect.Height * eased);
                RectangleF previousRect = new RectangleF(rect.Left, rect.Top - offset, rect.Width, rect.Height);
                RectangleF currentRect = new RectangleF(rect.Left, rect.Top + rect.Height - offset, rect.Width, rect.Height);
                g.DrawString(pages[previousIndex], font, brush, previousRect, marqueeFormat);
                g.DrawString(pages[pageIndex], font, brush, currentRect, marqueeFormat);
            }
            else
            {
                g.DrawString(pages[pageIndex], font, brush, rect, marqueeFormat);
            }

            g.Restore(state);
        }
    }

    private static int GetMediaTitlePageIndex(int pageCount, DateTime now)
    {
        if (pageCount <= 1)
        {
            return 0;
        }

        double cycleIndex = Math.Floor(now.TimeOfDay.TotalMilliseconds / MediaTitleFlipCycleMs);
        return ((int)cycleIndex) % pageCount;
    }

    private static double GetMediaTitleFlipPhase(DateTime now)
    {
        return now.TimeOfDay.TotalMilliseconds % MediaTitleFlipCycleMs;
    }

    private static List<string> BuildMediaTitlePages(Graphics g, string title, Font font, float maxWidth)
    {
        List<string> pages = new List<string>();
        if (string.IsNullOrEmpty(title))
        {
            return pages;
        }

        int start = 0;
        while (start < title.Length)
        {
            while (start < title.Length && char.IsWhiteSpace(title[start]))
            {
                start++;
            }

            if (start >= title.Length)
            {
                break;
            }

            int remaining = title.Length - start;
            int low = 1;
            int high = remaining;
            int best = 1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                string candidate = title.Substring(start, mid).Trim();
                SizeF measured = g.MeasureString(candidate, font);
                if (measured.Width <= maxWidth)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            int take = best;
            if (start + take < title.Length)
            {
                int lastSpace = title.LastIndexOf(' ', start + take - 1, take);
                if (lastSpace > start + Math.Max(4, take / 3))
                {
                    take = lastSpace - start + 1;
                }
            }

            string page = title.Substring(start, Math.Max(1, take)).Trim();
            if (page.Length == 0)
            {
                page = title.Substring(start, 1);
                take = 1;
            }

            pages.Add(page);
            start += Math.Max(1, take);
        }

        return pages;
    }

    private void DrawQuotaWidget(Graphics g, RectangleF rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        DockQuotaSnapshot snapshot = this.quotaSnapshot ?? DockQuotaSnapshot.CreateDefault();
        float rowGap = S(4);
        float rowHeight = (rect.Height - rowGap) / 2.0f;
        RectangleF firstRow = new RectangleF(rect.Left + S(5), rect.Top + S(4), rect.Width - S(10), rowHeight - S(2));
        RectangleF secondRow = new RectangleF(rect.Left + S(5), firstRow.Bottom + rowGap, rect.Width - S(10), rowHeight - S(2));
        DrawQuotaRow(
            g,
            firstRow,
            snapshot.FiveHourPercent,
            snapshot.FiveHourResetKnown ? snapshot.FiveHourResetLocal.ToString("HH:mm", CultureInfo.CurrentCulture) : "N/A");
        DrawQuotaRow(
            g,
            secondRow,
            snapshot.WeeklyPercent,
            snapshot.WeeklyResetKnown ? snapshot.WeeklyResetLocal.ToString("MM/dd", CultureInfo.CurrentCulture) : "N/A");
    }

    private void DrawQuotaRow(Graphics g, RectangleF rect, int percent, string resetText)
    {
        percent = ClampPercent(percent);
        float ringSize = Math.Max(S(25), Math.Min(rect.Height, S(36)));
        RectangleF ringRect = new RectangleF(rect.Left, rect.Top + (rect.Height - ringSize) / 2.0f, ringSize, ringSize);
        Color ringColor = GetQuotaColor(percent);
        float stroke = Math.Max(2.0f, ringSize * 0.14f);
        RectangleF arcRect = new RectangleF(
            ringRect.Left + stroke / 2.0f,
            ringRect.Top + stroke / 2.0f,
            ringRect.Width - stroke,
            ringRect.Height - stroke);

        using (Pen backgroundPen = new Pen(Color.FromArgb(78, 255, 255, 255), stroke))
        using (Pen valuePen = new Pen(ringColor, stroke))
        {
            backgroundPen.StartCap = LineCap.Round;
            backgroundPen.EndCap = LineCap.Round;
            valuePen.StartCap = LineCap.Round;
            valuePen.EndCap = LineCap.Round;
            g.DrawArc(backgroundPen, arcRect, -90.0f, 360.0f);
            if (percent > 0)
            {
                g.DrawArc(valuePen, arcRect, -90.0f, 360.0f * percent / 100.0f);
            }
        }

        using (Font numberFont = new Font("Segoe UI", Math.Max(8.0f, ringSize * 0.34f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush numberBrush = new SolidBrush(Color.FromArgb(248, 255, 255, 255)))
        using (StringFormat center = new StringFormat())
        {
            center.Alignment = StringAlignment.Center;
            center.LineAlignment = StringAlignment.Center;
            g.DrawString(percent.ToString(CultureInfo.InvariantCulture), numberFont, numberBrush, ringRect, center);
        }

        RectangleF textRect = new RectangleF(
            ringRect.Right + S(6),
            rect.Top,
            Math.Max(1.0f, rect.Right - ringRect.Right - S(6)),
            rect.Height);
        using (Font resetFont = new Font("Segoe UI", Math.Max(10.0f, ringSize * 0.66f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(226, 240, 244, 248)))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(resetText, resetFont, textBrush, textRect, format);
        }
    }

    private void DrawDockActionButtons(Graphics g, RectangleF[] rects)
    {
        if (rects == null)
        {
            return;
        }

        Point cursor = this.PointToClient(Cursor.Position);
        for (int i = 0; i < rects.Length; i++)
        {
            RectangleF rect = rects[i];
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            DrawDockActionButton(g, rect, i, rect.Contains(cursor.X, cursor.Y), i == 0 && this.dockUnpinMode);
        }
    }

    private void DrawDockActionButton(Graphics g, RectangleF rect, int kind, bool hovered, bool active)
    {
        Color fill = active
            ? (hovered ? Color.FromArgb(132, 66, 178, 255) : Color.FromArgb(108, 38, 145, 238))
            : (hovered ? Color.FromArgb(88, 255, 255, 255) : Color.FromArgb(50, 255, 255, 255));
        using (GraphicsPath path = RoundedRectangle(rect, S(4)))
        using (SolidBrush background = new SolidBrush(fill))
        using (Pen border = new Pen(Color.FromArgb(64, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        float inset = Math.Max(S(3), Math.Min(rect.Width, rect.Height) * 0.20f);
        RectangleF icon = new RectangleF(
            rect.Left + inset,
            rect.Top + inset,
            Math.Max(1.0f, rect.Width - inset * 2.0f),
            Math.Max(1.0f, rect.Height - inset * 2.0f));
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(236, 245, 248, 250)))
        using (Pen pen = new Pen(Color.FromArgb(236, 245, 248, 250), Math.Max(1.2f, 1.7f * this.scale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            pen.LineJoin = LineJoin.Round;
            if (kind == 0)
            {
                DrawUnpinGlyph(g, icon, brush, pen);
            }
            else if (kind == 1)
            {
                DrawSettingsGlyph(g, icon, brush, pen);
            }
            else if (kind == 2)
            {
                DrawExitGlyph(g, icon, pen);
            }
        }
    }

    private RectangleF GetUnpinBadgeRect(RectangleF itemRect)
    {
        float size = Math.Max(S(16), Math.Min(S(22), itemRect.Width * 0.36f));
        return new RectangleF(
            itemRect.Right - size * 0.82f,
            itemRect.Top - size * 0.12f,
            size,
            size);
    }

    private void DrawUnpinBadge(Graphics g, RectangleF rect, int alpha)
    {
        alpha = Math.Max(0, Math.Min(255, alpha));
        if (alpha <= 0 || rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        using (GraphicsPath path = RoundedRectangle(rect, rect.Height / 2.0f))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(ScaleAlpha(236, alpha), 218, 34, 52)))
        using (Pen border = new Pen(Color.FromArgb(ScaleAlpha(165, alpha), 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        float pad = rect.Width * 0.30f;
        using (Pen pen = new Pen(Color.FromArgb(ScaleAlpha(250, alpha), 255, 255, 255), Math.Max(1.6f, 2.0f * this.scale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(pen, rect.Left + pad, rect.Top + pad, rect.Right - pad, rect.Bottom - pad);
            g.DrawLine(pen, rect.Right - pad, rect.Top + pad, rect.Left + pad, rect.Bottom - pad);
        }
    }

    private void DrawUnpinGlyph(Graphics g, RectangleF rect, Brush brush, Pen pen)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float top = rect.Top + rect.Height * 0.16f;
        float bottom = rect.Bottom - rect.Height * 0.10f;
        float headWidth = rect.Width * 0.55f;
        RectangleF head = new RectangleF(cx - headWidth / 2.0f, top, headWidth, rect.Height * 0.20f);
        using (GraphicsPath headPath = RoundedRectangle(head, Math.Max(1.0f, head.Height * 0.35f)))
        {
            g.FillPath(brush, headPath);
        }

        g.DrawLine(pen, cx, head.Bottom, cx, bottom);
        g.DrawLine(pen, cx - rect.Width * 0.22f, rect.Top + rect.Height * 0.52f, cx + rect.Width * 0.22f, rect.Top + rect.Height * 0.52f);
        g.DrawLine(pen, rect.Left + rect.Width * 0.16f, rect.Bottom - rect.Height * 0.04f, rect.Right - rect.Width * 0.12f, rect.Top + rect.Height * 0.10f);
    }

    private void DrawSettingsGlyph(Graphics g, RectangleF rect, Brush brush, Pen pen)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float radius = Math.Min(rect.Width, rect.Height) * 0.34f;
        float innerRadius = radius * 0.38f;
        for (int i = 0; i < 8; i++)
        {
            double angle = Math.PI * 2.0 * i / 8.0;
            float x1 = cx + (float)Math.Cos(angle) * radius * 0.78f;
            float y1 = cy + (float)Math.Sin(angle) * radius * 0.78f;
            float x2 = cx + (float)Math.Cos(angle) * radius * 1.10f;
            float y2 = cy + (float)Math.Sin(angle) * radius * 1.10f;
            g.DrawLine(pen, x1, y1, x2, y2);
        }

        RectangleF outer = new RectangleF(cx - radius, cy - radius, radius * 2.0f, radius * 2.0f);
        RectangleF inner = new RectangleF(cx - innerRadius, cy - innerRadius, innerRadius * 2.0f, innerRadius * 2.0f);
        g.DrawEllipse(pen, outer);
        g.FillEllipse(brush, inner);
    }

    private void DrawExitGlyph(Graphics g, RectangleF rect, Pen pen)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float radius = Math.Min(rect.Width, rect.Height) * 0.38f;
        RectangleF arc = new RectangleF(cx - radius, cy - radius, radius * 2.0f, radius * 2.0f);
        g.DrawArc(pen, arc, 130.0f, 280.0f);
        g.DrawLine(pen, cx, rect.Top + rect.Height * 0.08f, cx, cy);
    }

    private static Color GetQuotaColor(int percent)
    {
        if (percent >= 80)
        {
            return Color.FromArgb(235, 76, 214, 116);
        }

        if (percent >= 30)
        {
            return Color.FromArgb(238, 242, 202, 73);
        }

        if (percent <= 5)
        {
            return Color.FromArgb(238, 126, 18, 28);
        }

        return Color.FromArgb(238, 226, 117, 49);
    }

    private void DrawMediaThumbnail(Graphics g, RectangleF rect, Bitmap appBitmap, bool isArtwork)
    {
        RectangleF inner = appBitmap != null
            ? rect
            : new RectangleF(rect.Left + S(4), rect.Top + S(4), rect.Width - S(8), rect.Height - S(8));
        using (GraphicsPath path = RoundedRectangle(inner, Math.Max(S(6), inner.Height * 0.22f)))
        {
            if (appBitmap != null)
            {
                using (SolidBrush background = new SolidBrush(Color.FromArgb(38, 255, 255, 255)))
                {
                    g.FillPath(background, path);
                }

                Rectangle contentBounds = GetVisibleBitmapBounds(appBitmap);
                if (contentBounds.Width <= 0 || contentBounds.Height <= 0)
                {
                    contentBounds = new Rectangle(0, 0, appBitmap.Width, appBitmap.Height);
                }

                Rectangle target;
                Rectangle sourceBounds;
                if (isArtwork)
                {
                    target = Rectangle.Round(rect);
                    sourceBounds = CropToFill(contentBounds, target);
                }
                else
                {
                    float iconPadding = Math.Max(1.0f, rect.Width * 0.04f);
                    Rectangle targetArea = Rectangle.Round(new RectangleF(
                        rect.Left + iconPadding,
                        rect.Top + iconPadding,
                        Math.Max(1.0f, rect.Width - iconPadding * 2.0f),
                        Math.Max(1.0f, rect.Height - iconPadding * 2.0f)));
                    target = FitInsideSquare(contentBounds.Size, targetArea);
                    sourceBounds = contentBounds;
                }

                GraphicsState state = g.Save();
                g.SetClip(path);
                g.DrawImage(appBitmap, target, sourceBounds, GraphicsUnit.Pixel);
                g.Restore(state);
            }
            else
            {
                GraphicsState state = g.Save();
                g.SetClip(path);
                using (LinearGradientBrush background = new LinearGradientBrush(inner, Color.FromArgb(255, 33, 43, 60), Color.FromArgb(255, 69, 148, 203), LinearGradientMode.ForwardDiagonal))
                {
                    g.FillRectangle(background, inner);
                }

                using (SolidBrush glow = new SolidBrush(Color.FromArgb(62, 255, 255, 255)))
                {
                    g.FillEllipse(glow, inner.Left - inner.Width * 0.35f, inner.Top - inner.Height * 0.35f, inner.Width * 0.95f, inner.Height * 0.95f);
                }

                g.Restore(state);
                DrawMediaWaveGlyph(g, inner);
            }

            using (Pen border = new Pen(Color.FromArgb(75, 255, 255, 255), Math.Max(1.0f, this.scale)))
            {
                g.DrawPath(border, path);
            }
        }
    }

    private void DrawMediaWaveGlyph(Graphics g, RectangleF rect)
    {
        float centerY = rect.Top + rect.Height / 2.0f;
        float left = rect.Left + rect.Width * 0.22f;
        float gap = rect.Width * 0.16f;
        float[] heights = new float[] { 0.28f, 0.58f, 0.38f };
        using (Pen pen = new Pen(Color.FromArgb(235, 236, 248, 255), Math.Max(1.2f, rect.Width * 0.10f)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            for (int i = 0; i < heights.Length; i++)
            {
                float h = rect.Height * heights[i];
                float x = left + gap * i;
                g.DrawLine(pen, x, centerY - h / 2.0f, x, centerY + h / 2.0f);
            }
        }
    }

    private void DrawMediaButton(Graphics g, RectangleF rect, int kind, bool isPlaying)
    {
        Point cursor = this.PointToClient(Cursor.Position);
        bool hovered = rect.Contains(cursor.X, cursor.Y);
        bool activePlayButton = kind == 1 && isPlaying;
        double pressElapsed = (DateTime.UtcNow - this.mediaButtonPressAnimationUtc).TotalMilliseconds;
        bool pressAnimating = this.mediaButtonPressKind == kind && pressElapsed >= 0.0 && pressElapsed < 190.0;
        double pressProgress = pressAnimating ? Math.Max(0.0, Math.Min(1.0, pressElapsed / 190.0)) : 1.0;
        Color backgroundColor = activePlayButton
            ? Color.FromArgb(66, 82, 211, 255)
            : (hovered ? Color.FromArgb(74, 255, 255, 255) : Color.FromArgb(48, 255, 255, 255));

        using (SolidBrush background = new SolidBrush(backgroundColor))
        using (Pen border = new Pen(Color.FromArgb(66, 255, 255, 255), Math.Max(1.0f, this.scale)))
        using (GraphicsPath path = RoundedRectangle(rect, S(5)))
        {
            g.FillPath(background, path);
            if (pressAnimating)
            {
                DrawMediaButtonPressAnimation(g, rect, pressProgress);
            }

            g.DrawPath(border, path);
        }

        using (SolidBrush brush = new SolidBrush(Color.FromArgb(238, 244, 248, 250)))
        using (Pen pen = new Pen(Color.FromArgb(238, 244, 248, 250), Math.Max(1.6f, 2.1f * this.scale)))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            if (kind == 0)
            {
                DrawPreviousGlyph(g, rect, brush, pen);
            }
            else if (kind == 1)
            {
                DrawPlayPauseGlyph(g, rect, brush, isPlaying);
            }
            else
            {
                DrawNextGlyph(g, rect, brush, pen);
            }
        }
    }

    private void DrawMediaButtonPressAnimation(Graphics g, RectangleF rect, double progress)
    {
        float size = (float)(Math.Max(rect.Width, rect.Height) * (0.10 + progress * 0.95));
        RectangleF ripple = new RectangleF(
            rect.Left + rect.Width / 2.0f - size / 2.0f,
            rect.Top + rect.Height / 2.0f - size / 2.0f,
            size,
            size);
        int alpha = Math.Max(0, (int)Math.Round(105.0 * (1.0 - progress)));
        using (GraphicsPath clipPath = RoundedRectangle(rect, S(5)))
        using (SolidBrush brush = new SolidBrush(Color.FromArgb(alpha, 255, 255, 255)))
        {
            GraphicsState state = g.Save();
            g.SetClip(clipPath);
            g.FillEllipse(brush, ripple);
            g.Restore(state);
        }
    }

    private void DrawPreviousGlyph(Graphics g, RectangleF rect, Brush brush, Pen pen)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float w = rect.Width * 0.20f;
        float h = rect.Height * 0.30f;
        g.DrawLine(pen, cx - w * 1.15f, cy - h, cx - w * 1.15f, cy + h);
        PointF[] triangle = new PointF[]
        {
            new PointF(cx + w * 0.75f, cy - h),
            new PointF(cx - w * 0.55f, cy),
            new PointF(cx + w * 0.75f, cy + h)
        };
        g.FillPolygon(brush, triangle);
    }

    private void DrawPlayPauseGlyph(Graphics g, RectangleF rect, Brush brush, bool isPlaying)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        if (isPlaying)
        {
            float barWidth = Math.Max(2.0f, rect.Width * 0.09f);
            float barGap = Math.Max(3.0f, rect.Width * 0.10f);
            float barHeight = rect.Height * 0.52f;
            RectangleF leftBar = new RectangleF(cx - barGap / 2.0f - barWidth, cy - barHeight / 2.0f, barWidth, barHeight);
            RectangleF rightBar = new RectangleF(cx + barGap / 2.0f, cy - barHeight / 2.0f, barWidth, barHeight);
            using (GraphicsPath leftPath = RoundedRectangle(leftBar, Math.Min(leftBar.Width, leftBar.Height) * 0.35f))
            using (GraphicsPath rightPath = RoundedRectangle(rightBar, Math.Min(rightBar.Width, rightBar.Height) * 0.35f))
            {
                g.FillPath(brush, leftPath);
                g.FillPath(brush, rightPath);
            }

            return;
        }

        float w = rect.Width * 0.21f;
        float h = rect.Height * 0.31f;
        PointF[] triangle = new PointF[]
        {
            new PointF(cx - w * 0.55f, cy - h),
            new PointF(cx - w * 0.55f, cy + h),
            new PointF(cx + w * 0.85f, cy)
        };
        g.FillPolygon(brush, triangle);
    }

    private void DrawNextGlyph(Graphics g, RectangleF rect, Brush brush, Pen pen)
    {
        float cx = rect.Left + rect.Width / 2.0f;
        float cy = rect.Top + rect.Height / 2.0f;
        float w = rect.Width * 0.20f;
        float h = rect.Height * 0.30f;
        PointF[] triangle = new PointF[]
        {
            new PointF(cx - w * 0.75f, cy - h),
            new PointF(cx + w * 0.55f, cy),
            new PointF(cx - w * 0.75f, cy + h)
        };
        g.FillPolygon(brush, triangle);
        g.DrawLine(pen, cx + w * 1.15f, cy - h, cx + w * 1.15f, cy + h);
    }

    private bool HandleMediaClick(PointF point)
    {
        int hoverIndex;
        DockLayout layout = CalculateDockLayout(point, true, out hoverIndex);
        if (layout.PreviousRect.Contains(point.X, point.Y))
        {
            StartMediaButtonPressAnimation(0);
            NativeMethods.SendMediaCommand(this.Handle, NativeMethods.APPCOMMAND_MEDIA_PREVIOUSTRACK);
            RefreshMediaInfoSoon(250);
            RefreshMediaInfoSoon(900);
            return true;
        }

        if (layout.PlayPauseRect.Contains(point.X, point.Y))
        {
            StartMediaButtonPressAnimation(1);
            ToggleMediaPlaybackStateOptimistically();
            NativeMethods.SendMediaCommand(this.Handle, NativeMethods.APPCOMMAND_MEDIA_PLAY_PAUSE);
            RefreshMediaInfoSoon(250);
            RefreshMediaInfoSoon(900);
            return true;
        }

        if (layout.NextRect.Contains(point.X, point.Y))
        {
            StartMediaButtonPressAnimation(2);
            NativeMethods.SendMediaCommand(this.Handle, NativeMethods.APPCOMMAND_MEDIA_NEXTTRACK);
            RefreshMediaInfoSoon(250);
            RefreshMediaInfoSoon(900);
            return true;
        }

        return false;
    }

    private bool HandleDockActionClick(PointF point)
    {
        int hoverIndex;
        DockLayout layout = CalculateDockLayout(point, true, out hoverIndex);
        RectangleF[] rects = layout.ActionButtonRects;
        if (rects == null)
        {
            return false;
        }

        for (int i = 0; i < rects.Length; i++)
        {
            if (!rects[i].Contains(point.X, point.Y))
            {
                continue;
            }

            if (i == 0)
            {
                this.dockUnpinMode = !this.dockUnpinMode;
                HidePreview();
                RenderLayeredWindow();
            }
            else if (i == 1)
            {
                if (this.openSettingsAction != null)
                {
                    this.openSettingsAction();
                }
            }
            else if (i == 2)
            {
                if (this.exitAction != null)
                {
                    this.exitAction();
                }
            }

            return true;
        }

        return false;
    }

    private bool HandleDockUnpinBadgeClick(PointF point)
    {
        if (!this.dockUnpinMode)
        {
            return false;
        }

        int pinnedIndex = FindUnpinBadgeAtPoint(point);
        if (pinnedIndex < 0)
        {
            return false;
        }

        StartPinnedItemUnpin(pinnedIndex);
        return true;
    }

    private int FindUnpinBadgeAtPoint(PointF point)
    {
        int hoverIndex;
        DockLayout layout = CalculateDockLayout(point, true, out hoverIndex);
        int count = Math.Min(this.runtimePinnedCount, Math.Min(this.runtimeItems.Count, layout.ItemRects.Length));
        for (int i = count - 1; i >= 0; i--)
        {
            DockRuntimeItem item = this.runtimeItems[i];
            if (item == null || item.AnimationState == DockItemAnimationState.Exiting)
            {
                continue;
            }

            if (GetUnpinBadgeRect(layout.ItemRects[i]).Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    private void StartPinnedItemUnpin(int pinnedIndex)
    {
        if (pinnedIndex < 0 || pinnedIndex >= this.runtimePinnedCount || pinnedIndex >= this.runtimeItems.Count)
        {
            return;
        }

        DockRuntimeItem item = this.runtimeItems[pinnedIndex];
        if (item == null || item.AnimationState == DockItemAnimationState.Exiting)
        {
            return;
        }

        bool removed;
        string updatedItemsText = DockItem.RemoveItemAt(this.currentSettings.DockItemsText, pinnedIndex, out removed);
        if (!removed)
        {
            return;
        }

        this.currentSettings.DockItemsText = updatedItemsText;
        this.currentSettings.Save();
        this.pendingDockUnpinRebuild = true;
        item.AnimationState = DockItemAnimationState.Exiting;
        item.AnimationStartedUtc = DateTime.UtcNow;
        HidePreview();

        if (this.launchpadForm != null &&
            !this.launchpadForm.IsDisposed &&
            this.launchpadForm.IsLaunchpadVisible)
        {
            this.launchpadForm.UpdatePinnedItemsText(this.currentSettings.LaunchpadItemsText, this.currentSettings.DockItemsText);
        }

        RenderLayeredWindow();
        Program.LogInfo("Dock item unpin animation started.");
    }

    private void StartMediaButtonPressAnimation(int kind)
    {
        this.mediaButtonPressKind = kind;
        this.mediaButtonPressAnimationUtc = DateTime.UtcNow;
        this.lastMediaAnimationRenderUtc = DateTime.MinValue;
        RenderLayeredWindow();
    }

    private void RenderLayeredWindow()
    {
        if (!this.IsHandleCreated || this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        try
        {
            using (Bitmap bitmap = new Bitmap(this.Width, this.Height, PixelFormat.Format32bppPArgb))
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                DrawDock(g);
                if (!NativeMethods.UpdateLayeredWindowFromBitmap(this.Handle, this.Location, bitmap, 255))
                {
                    if (!this.layeredUpdateFailureLogged)
                    {
                        this.layeredUpdateFailureLogged = true;
                        Program.LogInfo("Dock UpdateLayeredWindow failed; falling back to normal paint.");
                    }

                    this.Invalidate();
                }
            }
        }
        catch (Exception ex)
        {
            if (!this.layeredUpdateFailureLogged)
            {
                this.layeredUpdateFailureLogged = true;
                Program.LogException(ex);
            }
        }
    }

    private int GetBackgroundOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.BackgroundTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private int GetContentOpacityAlpha()
    {
        int alpha = (int)Math.Round(255.0 * (100 - this.currentSettings.ApplicationTransparencyPercent) / 100.0);
        return Math.Max(0, Math.Min(255, alpha));
    }

    private void ActivateOrLaunchItem(DockRuntimeItem item)
    {
        if (item == null)
        {
            return;
        }

        if (item.IsRunning && item.WindowHandle != IntPtr.Zero)
        {
            if (NativeMethods.ActivateWindow(item.WindowHandle))
            {
                return;
            }
        }

        LaunchItem(item.Item);
    }

    private void LaunchItem(DockItem item)
    {
        if (item == null || string.IsNullOrEmpty(item.Command))
        {
            return;
        }

        string fileName;
        string arguments;
        SplitCommandLine(item.Command, out fileName, out arguments);
        if (fileName.Length == 0)
        {
            return;
        }

        try
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.FileName = fileName;
            startInfo.Arguments = arguments;
            startInfo.UseShellExecute = true;
            Process.Start(startInfo);
            Program.LogInfo("Dock item launched: " + item.Label + " -> " + item.Command);
        }
        catch (Exception ex)
        {
            Program.LogException(ex);
            try
            {
                MessageBox.Show(
                    "无法启动 Dock 项目。\r\n\r\n" + item.Command + "\r\n\r\n" + ex.Message,
                    "DesktopPerfWidget",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch
            {
            }
        }
    }

    private static Rectangle GetVisibleBitmapBounds(Bitmap bitmap)
    {
        int left = bitmap.Width;
        int top = bitmap.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.A <= 8)
                {
                    continue;
                }

                if (x < left)
                {
                    left = x;
                }

                if (x > right)
                {
                    right = x;
                }

                if (y < top)
                {
                    top = y;
                }

                if (y > bottom)
                {
                    bottom = y;
                }
            }
        }

        if (right < left || bottom < top)
        {
            return Rectangle.Empty;
        }

        return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    private static Rectangle FitInsideSquare(Size sourceSize, Rectangle targetArea)
    {
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            return targetArea;
        }

        double scale = Math.Min(
            targetArea.Width / (double)sourceSize.Width,
            targetArea.Height / (double)sourceSize.Height);
        int width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
        int height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
        int left = targetArea.Left + (targetArea.Width - width) / 2;
        int top = targetArea.Top + (targetArea.Height - height) / 2;
        return new Rectangle(left, top, width, height);
    }

    private static Rectangle CropToFill(Rectangle sourceBounds, Rectangle targetArea)
    {
        if (sourceBounds.Width <= 0 || sourceBounds.Height <= 0 || targetArea.Width <= 0 || targetArea.Height <= 0)
        {
            return sourceBounds;
        }

        double sourceAspect = sourceBounds.Width / (double)sourceBounds.Height;
        double targetAspect = targetArea.Width / (double)targetArea.Height;
        if (sourceAspect > targetAspect)
        {
            int width = Math.Max(1, (int)Math.Round(sourceBounds.Height * targetAspect));
            int left = sourceBounds.Left + (sourceBounds.Width - width) / 2;
            return new Rectangle(left, sourceBounds.Top, width, sourceBounds.Height);
        }

        int height = Math.Max(1, (int)Math.Round(sourceBounds.Width / targetAspect));
        int top = sourceBounds.Top + (sourceBounds.Height - height) / 2;
        return new Rectangle(sourceBounds.Left, top, sourceBounds.Width, height);
    }

    private static string ResolveExecutablePath(string command)
    {
        string fileName;
        string arguments;
        SplitCommandLine(command, out fileName, out arguments);
        string uriIconPath = ResolveUriIconPath(fileName);
        if (!string.IsNullOrEmpty(uriIconPath))
        {
            return uriIconPath;
        }

        if (fileName.Length == 0 || IsShellCommand(fileName))
        {
            return string.Empty;
        }

        string expanded = Environment.ExpandEnvironmentVariables(fileName).Trim().Trim('"');
        if (File.Exists(expanded))
        {
            return expanded;
        }

        string[] candidates = new string[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), expanded),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), expanded)
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            if (File.Exists(candidates[i]))
            {
                return candidates[i];
            }
        }

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        string[] directories = path.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < directories.Length; i++)
        {
            try
            {
                string candidate = Path.Combine(directories[i].Trim(), expanded);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string ResolveUriIconPath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return string.Empty;
        }

        if (fileName.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
        {
            string settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "ImmersiveControlPanel",
                "SystemSettings.exe");
            if (File.Exists(settingsPath))
            {
                return settingsPath;
            }

            string controlPanelPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "control.exe");
            if (File.Exists(controlPanelPath))
            {
                return controlPanelPath;
            }
        }

        return string.Empty;
    }

    private static void SplitCommandLine(string command, out string fileName, out string arguments)
    {
        fileName = string.Empty;
        arguments = string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (IsShellCommand(value) || File.Exists(value))
        {
            fileName = value;
            return;
        }

        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1)
            {
                fileName = value.Substring(1, end - 1);
                arguments = value.Substring(end + 1).Trim();
                return;
            }
        }

        int split = value.IndexOf(' ');
        if (split > 0)
        {
            string candidate = value.Substring(0, split);
            if (File.Exists(candidate) || candidate.IndexOf('\\') < 0)
            {
                fileName = candidate;
                arguments = value.Substring(split + 1).Trim();
                return;
            }
        }

        fileName = value;
    }

    private static bool IsShellCommand(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int colon = value.IndexOf(':');
        if (colon <= 1)
        {
            return false;
        }

        int slash = value.IndexOf('\\');
        return slash < 0 || slash > colon;
    }

    private int S(int value)
    {
        return (int)Math.Round(value * Math.Min(this.scale, 1.15f));
    }

    private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
    {
        float diameter = radius * 2.0f;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
