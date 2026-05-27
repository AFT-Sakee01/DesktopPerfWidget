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

internal sealed class DockPreviewSource
{
    public DockPreviewSource(IntPtr handle, string title)
    {
        this.Handle = handle;
        this.Title = title ?? string.Empty;
    }

    public IntPtr Handle { get; private set; }
    public string Title { get; private set; }
}

internal sealed class DockPreviewForm : Form
{
    private readonly List<PreviewItem> items;
    private float scale;
    private int hoveredCloseIndex;
    private int hoveredItemIndex;

    private sealed class PreviewItem
    {
        public IntPtr SourceWindow { get; set; }
        public IntPtr ThumbnailHandle { get; set; }
        public string Title { get; set; }
        public Rectangle CardRect { get; set; }
        public Rectangle ThumbnailRect { get; set; }
        public Rectangle CloseButtonRect { get; set; }
    }

    public DockPreviewForm()
    {
        this.items = new List<PreviewItem>();
        this.hoveredCloseIndex = -1;
        this.hoveredItemIndex = -1;

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
        this.StartPosition = FormStartPosition.Manual;
        this.BackColor = Color.FromArgb(33, 33, 36);
        this.Opacity = 0.94;
        this.Size = new Size(S(390), S(300));
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation
    {
        get { return true; }
    }

    public bool ContainsScreenPoint(Point point)
    {
        return this.Visible && this.Bounds.Contains(point);
    }

    public void PrepareHidden()
    {
        if (!this.IsHandleCreated)
        {
            IntPtr handle = this.Handle;
        }
    }

    public void ShowPreview(Form owner, IList<DockPreviewSource> sources, Rectangle anchorRect, bool topMost)
    {
        List<DockPreviewSource> visibleSources = FilterVisibleSources(sources);
        if (visibleSources.Count == 0)
        {
            HidePreview();
            return;
        }

        if (!SameSources(visibleSources))
        {
            RebuildItems(visibleSources);
        }
        else
        {
            UpdateItemTitles(visibleSources);
        }

        LayoutPreview(anchorRect);
        this.TopMost = topMost;
        if (!this.Visible)
        {
            this.Show(owner);
        }

        RegisterThumbnails();
        LayoutPreview(anchorRect);
        NativeMethods.SetWindowPos(
            this.Handle,
            topMost ? NativeMethods.HWND_TOPMOST : NativeMethods.HWND_TOP,
            this.Left,
            this.Top,
            this.Width,
            this.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        UpdateThumbnailPlacement();
        this.Refresh();
    }

    public void HidePreview()
    {
        if (this.Visible)
        {
            this.Hide();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterThumbnails();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterThumbnails();
        base.OnFormClosed(e);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        using (GraphicsPath path = RoundedRectangle(new RectangleF(0, 0, this.Width, this.Height), S(10)))
        {
            this.Region = new Region(path);
        }

        UpdateThumbnailPlacement();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        RectangleF shellRect = new RectangleF(0, 0, this.Width - 1, this.Height - 1);
        using (GraphicsPath shell = RoundedRectangle(shellRect, S(10)))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(246, 34, 34, 38)))
        using (Pen border = new Pen(Color.FromArgb(105, 255, 255, 255), Math.Max(1.0f, this.scale)))
        {
            g.FillPath(background, shell);
            g.DrawPath(border, shell);
        }

        for (int i = 0; i < this.items.Count; i++)
        {
            DrawPreviewItem(g, this.items[i], i);
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        for (int i = this.items.Count - 1; i >= 0; i--)
        {
            PreviewItem item = this.items[i];
            if (item.CloseButtonRect.Contains(e.Location))
            {
                NativeMethods.RequestCloseWindow(item.SourceWindow);
                HidePreview();
                return;
            }

            if (item.ThumbnailRect.Contains(e.Location) || item.CardRect.Contains(e.Location))
            {
                NativeMethods.ActivateWindow(item.SourceWindow);
                HidePreview();
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hoveredItem = FindItemAtPoint(e.Location);
        int hoveredClose = FindCloseButtonAtPoint(e.Location);
        if (hoveredItem != this.hoveredItemIndex || hoveredClose != this.hoveredCloseIndex)
        {
            Rectangle invalid = Rectangle.Empty;
            if (this.hoveredItemIndex >= 0 && this.hoveredItemIndex < this.items.Count)
            {
                invalid = this.items[this.hoveredItemIndex].CardRect;
            }

            if (this.hoveredCloseIndex >= 0 && this.hoveredCloseIndex < this.items.Count)
            {
                invalid = invalid.IsEmpty ? this.items[this.hoveredCloseIndex].CloseButtonRect : Rectangle.Union(invalid, this.items[this.hoveredCloseIndex].CloseButtonRect);
            }

            this.hoveredItemIndex = hoveredItem;
            this.hoveredCloseIndex = hoveredClose;
            if (hoveredItem >= 0 && hoveredItem < this.items.Count)
            {
                invalid = invalid.IsEmpty ? this.items[hoveredItem].CardRect : Rectangle.Union(invalid, this.items[hoveredItem].CardRect);
            }

            if (hoveredClose >= 0 && hoveredClose < this.items.Count)
            {
                invalid = invalid.IsEmpty ? this.items[hoveredClose].CloseButtonRect : Rectangle.Union(invalid, this.items[hoveredClose].CloseButtonRect);
            }

            if (invalid.IsEmpty)
            {
                this.Invalidate();
            }
            else
            {
                invalid.Inflate(S(2), S(2));
                this.Invalidate(invalid);
            }
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (this.hoveredItemIndex >= 0 || this.hoveredCloseIndex >= 0)
        {
            int oldItemIndex = this.hoveredItemIndex;
            int oldCloseIndex = this.hoveredCloseIndex;
            this.hoveredItemIndex = -1;
            this.hoveredCloseIndex = -1;
            Rectangle invalid = Rectangle.Empty;
            if (oldItemIndex >= 0 && oldItemIndex < this.items.Count)
            {
                invalid = this.items[oldItemIndex].CardRect;
            }

            if (oldCloseIndex >= 0 && oldCloseIndex < this.items.Count)
            {
                invalid = invalid.IsEmpty ? this.items[oldCloseIndex].CloseButtonRect : Rectangle.Union(invalid, this.items[oldCloseIndex].CloseButtonRect);
            }

            if (invalid.IsEmpty)
            {
                this.Invalidate();
            }
            else
            {
                invalid.Inflate(S(2), S(2));
                this.Invalidate(invalid);
            }
        }
    }

    private static List<DockPreviewSource> FilterVisibleSources(IList<DockPreviewSource> sources)
    {
        List<DockPreviewSource> visible = new List<DockPreviewSource>();
        if (sources == null)
        {
            return visible;
        }

        HashSet<IntPtr> seen = new HashSet<IntPtr>();
        for (int i = 0; i < sources.Count; i++)
        {
            DockPreviewSource source = sources[i];
            if (source == null || source.Handle == IntPtr.Zero || seen.Contains(source.Handle))
            {
                continue;
            }

            if (!NativeMethods.IsApplicationWindowVisible(source.Handle))
            {
                continue;
            }

            seen.Add(source.Handle);
            visible.Add(source);
        }

        return visible;
    }

    private bool SameSources(IList<DockPreviewSource> sources)
    {
        if (sources == null || sources.Count != this.items.Count)
        {
            return false;
        }

        for (int i = 0; i < sources.Count; i++)
        {
            if (sources[i].Handle != this.items[i].SourceWindow)
            {
                return false;
            }
        }

        return true;
    }

    private void RebuildItems(IList<DockPreviewSource> sources)
    {
        UnregisterThumbnails();
        this.items.Clear();
        this.hoveredCloseIndex = -1;
        this.hoveredItemIndex = -1;
        for (int i = 0; i < sources.Count; i++)
        {
            this.items.Add(new PreviewItem
            {
                SourceWindow = sources[i].Handle,
                Title = string.IsNullOrEmpty(sources[i].Title) ? "Window" : sources[i].Title,
                ThumbnailHandle = IntPtr.Zero,
                CardRect = Rectangle.Empty,
                ThumbnailRect = Rectangle.Empty,
                CloseButtonRect = Rectangle.Empty
            });
        }

        RegisterThumbnails();
    }

    private void UpdateItemTitles(IList<DockPreviewSource> sources)
    {
        if (sources == null)
        {
            return;
        }

        int count = Math.Min(sources.Count, this.items.Count);
        for (int i = 0; i < count; i++)
        {
            this.items[i].Title = string.IsNullOrEmpty(sources[i].Title) ? "Window" : sources[i].Title;
        }
    }

    private void LayoutPreview(Rectangle anchorRect)
    {
        int count = Math.Max(1, this.items.Count);
        int columns = GetColumnCount(count);
        int rows = (count + columns - 1) / columns;
        int pad = S(10);
        int gap = S(10);
        int cardWidth = GetCardWidth(count);
        int cardHeight = GetCardHeight(count);
        int desiredWidth = pad * 2 + columns * cardWidth + Math.Max(0, columns - 1) * gap;
        int desiredHeight = pad * 2 + rows * cardHeight + Math.Max(0, rows - 1) * gap;

        Rectangle screen = Screen.FromRectangle(anchorRect).WorkingArea;
        int maxWidth = Math.Max(S(220), screen.Width - S(16));
        int maxHeight = Math.Max(S(160), screen.Height - S(24));
        if (desiredWidth > maxWidth)
        {
            cardWidth = Math.Max(S(180), (maxWidth - pad * 2 - Math.Max(0, columns - 1) * gap) / columns);
            desiredWidth = pad * 2 + columns * cardWidth + Math.Max(0, columns - 1) * gap;
        }

        if (desiredHeight > maxHeight)
        {
            cardHeight = Math.Max(S(130), (maxHeight - pad * 2 - Math.Max(0, rows - 1) * gap) / rows);
            desiredHeight = pad * 2 + rows * cardHeight + Math.Max(0, rows - 1) * gap;
        }

        if (this.Size.Width != desiredWidth || this.Size.Height != desiredHeight)
        {
            this.Size = new Size(desiredWidth, desiredHeight);
        }

        int anchorCenterX = anchorRect.Left + anchorRect.Width / 2;
        int left = anchorCenterX - desiredWidth / 2;
        left = Math.Max(screen.Left + S(8), Math.Min(left, screen.Right - desiredWidth - S(8)));
        int top = anchorRect.Top - desiredHeight - S(12);
        if (top < screen.Top + S(8))
        {
            top = anchorRect.Bottom + S(12);
        }

        this.Location = new Point(left, top);

        for (int i = 0; i < this.items.Count; i++)
        {
            int row = i / columns;
            int col = i % columns;
            Rectangle card = new Rectangle(
                pad + col * (cardWidth + gap),
                pad + row * (cardHeight + gap),
                cardWidth,
                cardHeight);
            this.items[i].CardRect = card;
            this.items[i].CloseButtonRect = GetCloseButtonRect(card);
            this.items[i].ThumbnailRect = CalculateThumbnailRect(this.items[i], card);
        }
    }

    private int GetColumnCount(int count)
    {
        if (count <= 1)
        {
            return 1;
        }

        if (count <= 4)
        {
            return 2;
        }

        return 3;
    }

    private int GetCardWidth(int count)
    {
        if (count <= 1)
        {
            return S(390);
        }

        if (count <= 2)
        {
            return S(300);
        }

        if (count <= 4)
        {
            return S(270);
        }

        return S(230);
    }

    private int GetCardHeight(int count)
    {
        if (count <= 1)
        {
            return S(280);
        }

        if (count <= 2)
        {
            return S(214);
        }

        if (count <= 4)
        {
            return S(194);
        }

        return S(168);
    }

    private Rectangle CalculateThumbnailRect(PreviewItem item, Rectangle card)
    {
        int headerHeight = GetHeaderHeight();
        Rectangle bounds = new Rectangle(
            card.Left + S(8),
            card.Top + headerHeight + S(6),
            Math.Max(1, card.Width - S(16)),
            Math.Max(1, card.Height - headerHeight - S(14)));
        Size sourceSize = NativeMethods.QueryThumbnailSourceSize(item.ThumbnailHandle);
        if (sourceSize.Width <= 0 || sourceSize.Height <= 0)
        {
            sourceSize = new Size(16, 9);
        }

        return FitInside(bounds, sourceSize);
    }

    private Rectangle GetCloseButtonRect(Rectangle card)
    {
        int size = S(24);
        return new Rectangle(card.Right - size - S(8), card.Top + S(7), size, size);
    }

    private void RegisterThumbnails()
    {
        if (!this.IsHandleCreated)
        {
            return;
        }

        for (int i = 0; i < this.items.Count; i++)
        {
            PreviewItem item = this.items[i];
            if (item.ThumbnailHandle != IntPtr.Zero || item.SourceWindow == IntPtr.Zero)
            {
                continue;
            }

            IntPtr thumbnailHandle;
            if (NativeMethods.RegisterDwmThumbnail(this.Handle, item.SourceWindow, out thumbnailHandle))
            {
                item.ThumbnailHandle = thumbnailHandle;
            }
        }
    }

    private void UnregisterThumbnails()
    {
        for (int i = 0; i < this.items.Count; i++)
        {
            if (this.items[i].ThumbnailHandle != IntPtr.Zero)
            {
                NativeMethods.UnregisterDwmThumbnail(this.items[i].ThumbnailHandle);
                this.items[i].ThumbnailHandle = IntPtr.Zero;
            }
        }
    }

    private void UpdateThumbnailPlacement()
    {
        if (this.Width <= 0 || this.Height <= 0)
        {
            return;
        }

        for (int i = 0; i < this.items.Count; i++)
        {
            PreviewItem item = this.items[i];
            if (item.ThumbnailHandle == IntPtr.Zero || item.ThumbnailRect.Width <= 0 || item.ThumbnailRect.Height <= 0)
            {
                continue;
            }

            NativeMethods.UpdateDwmThumbnail(item.ThumbnailHandle, item.ThumbnailRect, 255);
        }
    }

    private void DrawPreviewItem(Graphics g, PreviewItem item, int index)
    {
        bool hovered = index == this.hoveredItemIndex;
        Color cardColor = hovered
            ? Color.FromArgb(92, 58, 58, 64)
            : Color.FromArgb(116, 8, 8, 10);
        Color borderColor = hovered
            ? Color.FromArgb(112, 255, 255, 255)
            : Color.FromArgb(62, 255, 255, 255);
        using (GraphicsPath cardPath = RoundedRectangle(item.CardRect, S(8)))
        using (SolidBrush cardBrush = new SolidBrush(cardColor))
        using (Pen cardBorder = new Pen(borderColor, Math.Max(1.0f, this.scale)))
        {
            g.FillPath(cardBrush, cardPath);
            g.DrawPath(cardBorder, cardPath);
        }

        Rectangle headerRect = new Rectangle(item.CardRect.Left, item.CardRect.Top, item.CardRect.Width, GetHeaderHeight());
        Color headerColor = hovered
            ? Color.FromArgb(82, 74, 74, 82)
            : Color.FromArgb(118, 12, 12, 15);
        using (GraphicsPath headerPath = RoundedRectangle(headerRect, S(8)))
        using (SolidBrush headerBrush = new SolidBrush(headerColor))
        {
            g.FillPath(headerBrush, headerPath);
        }

        Rectangle titleRect = new Rectangle(
            item.CardRect.Left + S(12),
            item.CardRect.Top + S(6),
            Math.Max(1, item.CardRect.Width - S(52)),
            Math.Max(S(24), GetHeaderHeight() - S(12)));
        Color titleColor = hovered
            ? Color.FromArgb(250, 255, 255, 255)
            : Color.FromArgb(245, 245, 245, 245);
        using (Font font = new Font("Segoe UI", Math.Max(14.0f, 16.0f * Math.Min(this.scale, 1.15f)), FontStyle.Regular, GraphicsUnit.Pixel))
        using (SolidBrush brush = new SolidBrush(titleColor))
        using (StringFormat format = new StringFormat())
        {
            format.Alignment = StringAlignment.Near;
            format.LineAlignment = StringAlignment.Center;
            format.Trimming = StringTrimming.EllipsisCharacter;
            format.FormatFlags = StringFormatFlags.NoWrap;
            g.DrawString(item.Title, font, brush, titleRect, format);
        }

        DrawCloseButton(g, item.CloseButtonRect, index == this.hoveredCloseIndex);

        if (item.ThumbnailRect.Width > 0 && item.ThumbnailRect.Height > 0)
        {
            Color previewBorderColor = hovered
                ? Color.FromArgb(104, 255, 255, 255)
                : Color.FromArgb(76, 255, 255, 255);
            using (Pen previewBorder = new Pen(previewBorderColor, Math.Max(1.0f, this.scale)))
            {
                Rectangle borderRect = item.ThumbnailRect;
                borderRect.Width -= 1;
                borderRect.Height -= 1;
                g.DrawRectangle(previewBorder, borderRect);
            }
        }
    }

    private int FindCloseButtonAtPoint(Point point)
    {
        for (int i = this.items.Count - 1; i >= 0; i--)
        {
            if (this.items[i].CloseButtonRect.Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindItemAtPoint(Point point)
    {
        for (int i = this.items.Count - 1; i >= 0; i--)
        {
            if (this.items[i].CardRect.Contains(point))
            {
                return i;
            }
        }

        return -1;
    }

    private int GetHeaderHeight()
    {
        return S(40);
    }

    private void DrawCloseButton(Graphics g, Rectangle rect, bool hovered)
    {
        Color backgroundColor = hovered
            ? Color.FromArgb(185, 232, 72, 74)
            : Color.FromArgb(72, 255, 255, 255);
        using (GraphicsPath path = RoundedRectangle(rect, rect.Height / 2.0f))
        using (SolidBrush background = new SolidBrush(backgroundColor))
        {
            g.FillPath(background, path);
        }

        float pad = rect.Width * 0.32f;
        using (Pen pen = new Pen(Color.FromArgb(245, 255, 255, 255), Math.Max(1.4f, 1.8f * Math.Min(this.scale, 1.15f))))
        {
            pen.StartCap = LineCap.Round;
            pen.EndCap = LineCap.Round;
            g.DrawLine(
                pen,
                rect.Left + pad,
                rect.Top + pad,
                rect.Right - pad,
                rect.Bottom - pad);
            g.DrawLine(
                pen,
                rect.Right - pad,
                rect.Top + pad,
                rect.Left + pad,
                rect.Bottom - pad);
        }
    }

    private static Rectangle FitInside(Rectangle bounds, Size sourceSize)
    {
        double scale = Math.Min(
            bounds.Width / (double)Math.Max(1, sourceSize.Width),
            bounds.Height / (double)Math.Max(1, sourceSize.Height));
        int width = Math.Max(1, (int)Math.Round(sourceSize.Width * scale));
        int height = Math.Max(1, (int)Math.Round(sourceSize.Height * scale));
        int left = bounds.Left + (bounds.Width - width) / 2;
        int top = bounds.Top + (bounds.Height - height) / 2;
        return new Rectangle(left, top, width, height);
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
