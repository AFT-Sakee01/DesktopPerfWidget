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

internal sealed class AppIconRequest
{
    public AppIconRequest(string path, string label)
    {
        this.Path = path ?? string.Empty;
        this.Label = label ?? string.Empty;
    }

    public string Path { get; private set; }
    public string Label { get; private set; }
}

internal sealed class AppIconReadyEventArgs : EventArgs
{
    public AppIconReadyEventArgs(string key)
    {
        this.Key = key ?? string.Empty;
    }

    public string Key { get; private set; }
}

internal static class AppIconCache
{
    private const int IconSize = 128;
    private const int MaxIconResolutionDepth = 4;
    private static readonly object SyncRoot = new object();
    private static readonly Dictionary<string, CachedIcon> Icons = new Dictionary<string, CachedIcon>(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<IconLoadRequest> PendingLoads = new Queue<IconLoadRequest>();
    private static bool workerRunning;
    private static bool disposed;

    public static event EventHandler<AppIconReadyEventArgs> IconReady;

    private sealed class CachedIcon
    {
        public Bitmap Bitmap { get; set; }
        public Bitmap FallbackBitmap { get; set; }
        public bool Ready { get; set; }
        public bool LoadQueued { get; set; }
        public DateTime LastAccessUtc { get; set; }
    }

    private sealed class IconLoadRequest
    {
        public string Key { get; set; }
        public string Path { get; set; }
        public string Label { get; set; }
    }

    public static void WarmUp(IEnumerable<AppIconRequest> requests)
    {
        if (requests == null)
        {
            return;
        }

        foreach (AppIconRequest request in requests)
        {
            if (request != null)
            {
                RequestIcon(request.Path, request.Label, false);
            }
        }
    }

    public static string RequestIcon(string path, string label)
    {
        return RequestIcon(path, label, true);
    }

    private static string RequestIcon(string path, string label, bool createFallback)
    {
        string key = BuildIconKey(path, label);
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }

        string normalizedPath = NormalizePath(path);
        bool canLoadFromFile = CanLoadIconSource(normalizedPath);

        lock (SyncRoot)
        {
            if (disposed)
            {
                return string.Empty;
            }

            CachedIcon icon;
            if (!Icons.TryGetValue(key, out icon))
            {
                icon = new CachedIcon();
                if (createFallback)
                {
                    icon.FallbackBitmap = CreateFallbackIcon(label);
                }

                icon.LastAccessUtc = DateTime.UtcNow;
                Icons.Add(key, icon);
            }
            else
            {
                if (createFallback && icon.FallbackBitmap == null)
                {
                    icon.FallbackBitmap = CreateFallbackIcon(label);
                }

                icon.LastAccessUtc = DateTime.UtcNow;
            }

            if (!canLoadFromFile)
            {
                icon.Ready = true;
                return key;
            }

            if (!icon.Ready && !icon.LoadQueued)
            {
                icon.LoadQueued = true;
                PendingLoads.Enqueue(new IconLoadRequest
                {
                    Key = key,
                    Path = normalizedPath,
                    Label = label ?? string.Empty
                });

                EnsureWorkerLocked();
            }
        }

        return key;
    }

    public static Bitmap GetBitmap(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        lock (SyncRoot)
        {
            CachedIcon icon;
            if (!Icons.TryGetValue(key, out icon))
            {
                return null;
            }

            icon.LastAccessUtc = DateTime.UtcNow;
            if (icon.Bitmap != null)
            {
                return icon.Bitmap;
            }

            return icon.FallbackBitmap;
        }
    }

    public static void DisposeAll()
    {
        List<Bitmap> bitmaps = new List<Bitmap>();
        lock (SyncRoot)
        {
            disposed = true;
            PendingLoads.Clear();
            foreach (KeyValuePair<string, CachedIcon> pair in Icons)
            {
                if (pair.Value.Bitmap != null)
                {
                    bitmaps.Add(pair.Value.Bitmap);
                }

                if (pair.Value.FallbackBitmap != null &&
                    !object.ReferenceEquals(pair.Value.FallbackBitmap, pair.Value.Bitmap))
                {
                    bitmaps.Add(pair.Value.FallbackBitmap);
                }
            }

            Icons.Clear();
        }

        for (int i = 0; i < bitmaps.Count; i++)
        {
            try
            {
                bitmaps[i].Dispose();
            }
            catch
            {
            }
        }
    }

    private static void EnsureWorkerLocked()
    {
        if (workerRunning)
        {
            return;
        }

        workerRunning = true;
        Task.Run((Action)ProcessQueue);
    }

    private static void ProcessQueue()
    {
        while (true)
        {
            IconLoadRequest request;
            lock (SyncRoot)
            {
                if (disposed)
                {
                    workerRunning = false;
                    return;
                }

                if (PendingLoads.Count == 0)
                {
                    workerRunning = false;
                    return;
                }

                request = PendingLoads.Dequeue();
            }

            Bitmap bitmap = LoadBitmapFromPersistentCache(request.Key);
            if (bitmap == null)
            {
                bitmap = LoadBitmapFromFile(request.Path);
                if (bitmap != null)
                {
                    SaveBitmapToPersistentCache(request.Key, bitmap);
                }
            }
            bool shouldNotify = false;
            lock (SyncRoot)
            {
                if (disposed)
                {
                    if (bitmap != null)
                    {
                        bitmap.Dispose();
                    }

                    workerRunning = false;
                    return;
                }

                CachedIcon icon;
                if (Icons.TryGetValue(request.Key, out icon))
                {
                    if (bitmap != null)
                    {
                        icon.Bitmap = bitmap;
                    }

                    icon.Ready = true;
                    icon.LastAccessUtc = DateTime.UtcNow;
                    shouldNotify = true;
                }
                else if (bitmap != null)
                {
                    bitmap.Dispose();
                }
            }

            if (shouldNotify)
            {
                RaiseIconReady(request.Key);
            }
        }
    }

    private static void RaiseIconReady(string key)
    {
        EventHandler<AppIconReadyEventArgs> handler = IconReady;
        if (handler == null)
        {
            return;
        }

        try
        {
            handler(null, new AppIconReadyEventArgs(key));
        }
        catch
        {
        }
    }

    private static Bitmap LoadBitmapFromPersistentCache(string key)
    {
        try
        {
            string path = GetPersistentCachePath(key);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            using (Bitmap source = (Bitmap)Image.FromFile(path))
            {
                return NormalizeSourceBitmap(source);
            }
        }
        catch
        {
            return null;
        }
    }

    private static void SaveBitmapToPersistentCache(string key, Bitmap bitmap)
    {
        if (bitmap == null)
        {
            return;
        }

        try
        {
            string path = GetPersistentCachePath(key);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path));
            bitmap.Save(path, ImageFormat.Png);
        }
        catch
        {
        }
    }

    private static string GetPersistentCachePath(string key)
    {
        if (string.IsNullOrEmpty(key) || !key.StartsWith("file|", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(key);
            byte[] hash;
            using (System.Security.Cryptography.SHA1 sha1 = System.Security.Cryptography.SHA1.Create())
            {
                hash = sha1.ComputeHash(bytes);
            }

            StringBuilder name = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                name.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return Path.Combine(Logger.DirectoryPath, "IconCache", name.ToString() + ".png");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static Bitmap LoadBitmapFromFile(string path)
    {
        return LoadBitmapFromFile(path, 0);
    }

    private static Bitmap LoadBitmapFromFile(string path, int depth)
    {
        if (string.IsNullOrEmpty(path) || depth > MaxIconResolutionDepth)
        {
            return null;
        }

        string normalizedPath = NormalizePath(path);
        if (IsShellParsingName(normalizedPath))
        {
            Bitmap shellItemBitmap = NativeMethods.TryLoadShellItemBitmap(normalizedPath);
            return shellItemBitmap == null ? null : NormalizeNativeBitmap(shellItemBitmap);
        }

        string iconPath;
        int iconIndex;
        if (TryParseIconLocation(normalizedPath, out iconPath, out iconIndex))
        {
            return LoadIconLocationBitmap(iconPath, iconIndex);
        }

        if (!File.Exists(normalizedPath))
        {
            return null;
        }

        string extension = Path.GetExtension(normalizedPath);
        if (string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase))
        {
            Bitmap urlBitmap = LoadInternetShortcutBitmap(normalizedPath, depth);
            if (urlBitmap != null)
            {
                return urlBitmap;
            }
        }
        else if (string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            Bitmap shortcutBitmap = LoadShortcutBitmap(normalizedPath, depth);
            if (shortcutBitmap != null)
            {
                return shortcutBitmap;
            }
        }

        Bitmap shellBitmap = NativeMethods.TryLoadShellIconBitmap(normalizedPath);
        if (shellBitmap != null)
        {
            return NormalizeNativeBitmap(shellBitmap);
        }

        return LoadAssociatedIconBitmap(normalizedPath);
    }

    private static Bitmap LoadInternetShortcutBitmap(string path, int depth)
    {
        string iconFile = NativeMethods.TryReadIniValue(path, "InternetShortcut", "IconFile");
        if (string.IsNullOrEmpty(iconFile))
        {
            return null;
        }

        int iconIndex = 0;
        string iconIndexText = NativeMethods.TryReadIniValue(path, "InternetShortcut", "IconIndex");
        int.TryParse(iconIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out iconIndex);

        string parsedIconPath;
        int parsedIconIndex;
        if (TryParseIconLocation(iconFile, out parsedIconPath, out parsedIconIndex))
        {
            return LoadIconLocationBitmap(parsedIconPath, parsedIconIndex);
        }

        string normalizedIconFile = NormalizePath(iconFile, Path.GetDirectoryName(path));
        Bitmap bitmap = LoadIconLocationBitmap(normalizedIconFile, iconIndex);
        if (bitmap != null)
        {
            return bitmap;
        }

        return depth >= MaxIconResolutionDepth ? null : LoadBitmapFromFile(normalizedIconFile, depth + 1);
    }

    private static Bitmap LoadShortcutBitmap(string path, int depth)
    {
        NativeMethods.ShellLinkInfo linkInfo;
        if (!NativeMethods.TryResolveShortcut(path, out linkInfo) || linkInfo == null)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(linkInfo.IconPath))
        {
            string normalizedIconPath = NormalizePath(linkInfo.IconPath, Path.GetDirectoryName(path));
            Bitmap iconBitmap = LoadIconLocationBitmap(normalizedIconPath, linkInfo.IconIndex);
            if (iconBitmap != null)
            {
                return iconBitmap;
            }
        }

        if (!string.IsNullOrEmpty(linkInfo.TargetPath) && depth < MaxIconResolutionDepth)
        {
            Bitmap targetBitmap = LoadBitmapFromFile(linkInfo.TargetPath, depth + 1);
            if (targetBitmap != null)
            {
                return targetBitmap;
            }
        }

        return null;
    }

    private static Bitmap LoadIconLocationBitmap(string path, int iconIndex)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string parsedPath;
        int parsedIndex;
        if (TryParseIconLocation(path, out parsedPath, out parsedIndex))
        {
            path = parsedPath;
            iconIndex = parsedIndex;
        }

        string normalizedPath = NormalizePath(path);
        if (string.IsNullOrEmpty(normalizedPath) || !File.Exists(normalizedPath))
        {
            return null;
        }

        Bitmap extractedBitmap = NativeMethods.TryExtractIconBitmap(normalizedPath, iconIndex);
        if (extractedBitmap != null)
        {
            return NormalizeNativeBitmap(extractedBitmap);
        }

        Bitmap shellBitmap = NativeMethods.TryLoadShellIconBitmap(normalizedPath);
        if (shellBitmap != null)
        {
            return NormalizeNativeBitmap(shellBitmap);
        }

        return LoadAssociatedIconBitmap(normalizedPath);
    }

    private static Bitmap LoadAssociatedIconBitmap(string path)
    {
        try
        {
            using (Icon icon = Icon.ExtractAssociatedIcon(path))
            {
                if (icon != null)
                {
                    return IconToBitmap(icon);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static Bitmap NormalizeNativeBitmap(Bitmap source)
    {
        if (source == null)
        {
            return null;
        }

        try
        {
            return NormalizeSourceBitmap(source);
        }
        finally
        {
            source.Dispose();
        }
    }

    private static Bitmap IconToBitmap(Icon icon)
    {
        using (Bitmap source = icon.ToBitmap())
        {
            return NormalizeSourceBitmap(source);
        }
    }

    private static Bitmap NormalizeSourceBitmap(Bitmap source)
    {
        Bitmap bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
        Rectangle contentBounds = GetVisibleBitmapBounds(source);
        if (contentBounds.Width <= 0 || contentBounds.Height <= 0)
        {
            contentBounds = new Rectangle(0, 0, source.Width, source.Height);
        }

        Rectangle target = FitInside(contentBounds.Size, new Rectangle(1, 1, IconSize - 2, IconSize - 2));
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.DrawImage(source, target, contentBounds, GraphicsUnit.Pixel);
        }

        return bitmap;
    }

    private static Bitmap CreateFallbackIcon(string label)
    {
        Bitmap bitmap = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppPArgb);
        string glyph = string.IsNullOrEmpty(label) ? "A" : label.Substring(0, 1).ToUpperInvariant();
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            RectangleF rect = new RectangleF(10, 10, 108, 108);
            using (GraphicsPath path = RoundedRectangle(rect, 28))
            using (LinearGradientBrush brush = new LinearGradientBrush(rect, Color.FromArgb(76, 191, 255), Color.FromArgb(122, 236, 177), LinearGradientMode.ForwardDiagonal))
            using (Pen border = new Pen(Color.FromArgb(160, 255, 255, 255), 2.0f))
            {
                g.FillPath(brush, path);
                g.DrawPath(border, path);
            }

            using (Font font = new Font("Segoe UI", 46.0f, FontStyle.Bold, GraphicsUnit.Pixel))
            using (SolidBrush textBrush = new SolidBrush(Color.FromArgb(245, 250, 252)))
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(glyph, font, textBrush, rect, format);
            }
        }

        return bitmap;
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

    private static Rectangle FitInside(Size sourceSize, Rectangle targetArea)
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

    private static string BuildIconKey(string path, string label)
    {
        string normalizedPath = NormalizePath(path);
        if (IsShellParsingName(normalizedPath))
        {
            return "file|shellitem|" + normalizedPath.ToUpperInvariant();
        }

        string keyPath = normalizedPath;
        string keySuffix = string.Empty;

        string iconPath;
        int iconIndex;
        if (TryParseIconLocation(normalizedPath, out iconPath, out iconIndex))
        {
            keyPath = iconPath;
            keySuffix = "|iconindex=" + iconIndex.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(keyPath) && File.Exists(keyPath))
        {
            try
            {
                FileInfo info = new FileInfo(keyPath);
                return "file|" + info.FullName.ToUpperInvariant() + "|" +
                    info.Length.ToString(CultureInfo.InvariantCulture) + "|" +
                    info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) +
                    keySuffix;
            }
            catch
            {
                return "file|" + keyPath.ToUpperInvariant() + keySuffix;
            }
        }

        string fallback = string.IsNullOrEmpty(label) ? "App" : label.Trim();
        return "fallback|" + fallback.ToUpperInvariant();
    }

    private static bool CanLoadIconSource(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (IsShellParsingName(path))
        {
            return true;
        }

        string iconPath;
        int iconIndex;
        if (TryParseIconLocation(path, out iconPath, out iconIndex) && File.Exists(iconPath))
        {
            return true;
        }

        return File.Exists(path);
    }

    private static bool TryParseIconLocation(string value, out string iconPath, out int iconIndex)
    {
        iconPath = string.Empty;
        iconIndex = 0;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string text = Environment.ExpandEnvironmentVariables(value).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        if (text.StartsWith("\"", StringComparison.Ordinal))
        {
            int endQuote = text.IndexOf('"', 1);
            if (endQuote > 1)
            {
                string rest = text.Substring(endQuote + 1).Trim();
                if (rest.StartsWith(",", StringComparison.Ordinal) &&
                    int.TryParse(rest.Substring(1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out iconIndex))
                {
                    iconPath = NormalizePath(text.Substring(1, endQuote - 1));
                    return !string.IsNullOrEmpty(iconPath);
                }
            }
        }

        int comma = text.LastIndexOf(',');
        if (comma <= 0 || comma >= text.Length - 1)
        {
            return false;
        }

        string indexText = text.Substring(comma + 1).Trim();
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out iconIndex))
        {
            return false;
        }

        iconPath = NormalizePath(text.Substring(0, comma).Trim().Trim('"'));
        return !string.IsNullOrEmpty(iconPath);
    }

    private static bool IsShellParsingName(string value)
    {
        return !string.IsNullOrEmpty(value) &&
            value.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        return NormalizePath(path, string.Empty);
    }

    private static string NormalizePath(string path, string baseDirectory)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(path).Trim().Trim('"');
            if (string.IsNullOrEmpty(expanded))
            {
                return string.Empty;
            }

            if (IsShellParsingName(expanded))
            {
                return expanded;
            }

            if (!string.IsNullOrEmpty(baseDirectory) && !Path.IsPathRooted(expanded))
            {
                expanded = Path.Combine(baseDirectory, expanded);
            }

            return Path.GetFullPath(expanded);
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }
}
