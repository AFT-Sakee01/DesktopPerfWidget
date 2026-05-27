using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

internal static class AppDropHelper
{
    public static List<DockItem> BuildItemsFromData(IDataObject data)
    {
        List<DockItem> items = new List<DockItem>();
        if (data == null || !data.GetDataPresent(DataFormats.FileDrop))
        {
            return items;
        }

        string[] paths = data.GetData(DataFormats.FileDrop) as string[];
        if (paths == null)
        {
            return items;
        }

        for (int i = 0; i < paths.Length; i++)
        {
            DockItem item;
            if (TryBuildItemFromPath(paths[i], out item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    public static bool TryBuildItemFromPath(string path, out DockItem item)
    {
        item = null;
        string normalized = NormalizeSelectedProgramPath(path);
        if (string.IsNullOrEmpty(normalized) || !File.Exists(normalized))
        {
            return false;
        }

        if (!IsSupportedProgramPath(normalized))
        {
            return false;
        }

        item = new DockItem(BuildProgramLabel(normalized), normalized);
        return true;
    }

    public static string AppendItemsText(string text, IList<DockItem> newItems, out int addedCount)
    {
        return InsertItemsText(text, newItems, int.MaxValue, out addedCount);
    }

    public static string AppendItemsText(string text, IList<DockItem> newItems, string existingText, out int addedCount)
    {
        addedCount = 0;
        List<DockItem> existing = DockItem.ParseItems(text);
        if (newItems == null || newItems.Count == 0)
        {
            return DockItem.SerializeItems(existing);
        }

        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < existing.Count; i++)
        {
            seen.Add(NormalizeCommandForCompare(existing[i].Command));
        }

        List<DockItem> blocked = DockItem.ParseItems(existingText);
        for (int i = 0; i < blocked.Count; i++)
        {
            seen.Add(NormalizeCommandForCompare(blocked[i].Command));
        }

        for (int i = 0; i < newItems.Count; i++)
        {
            DockItem item = newItems[i];
            if (item == null || string.IsNullOrEmpty(item.Command))
            {
                continue;
            }

            string key = NormalizeCommandForCompare(item.Command);
            if (string.IsNullOrEmpty(key) || !seen.Add(key))
            {
                continue;
            }

            existing.Add(item);
            addedCount++;
        }

        return DockItem.SerializeItems(existing);
    }

    public static string InsertItemsText(string text, IList<DockItem> newItems, int insertIndex, out int addedCount)
    {
        addedCount = 0;
        List<DockItem> existing = DockItem.ParseItems(text);
        if (newItems == null || newItems.Count == 0)
        {
            return DockItem.SerializeItems(existing);
        }

        int index = Math.Max(0, Math.Min(insertIndex, existing.Count));
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < existing.Count; i++)
        {
            seen.Add(NormalizeCommandForCompare(existing[i].Command));
        }

        for (int i = 0; i < newItems.Count; i++)
        {
            DockItem item = newItems[i];
            if (item == null || string.IsNullOrEmpty(item.Command))
            {
                continue;
            }

            string key = NormalizeCommandForCompare(item.Command);
            if (string.IsNullOrEmpty(key) || !seen.Add(key))
            {
                continue;
            }

            existing.Insert(index, item);
            index++;
            addedCount++;
        }

        return DockItem.SerializeItems(existing);
    }

    public static string InsertOrMoveItemsText(string text, IList<DockItem> newItems, int insertIndex, out int changedCount)
    {
        changedCount = 0;
        List<DockItem> existing = DockItem.ParseItems(text);
        if (newItems == null || newItems.Count == 0)
        {
            return DockItem.SerializeItems(existing);
        }

        int index = Math.Max(0, Math.Min(insertIndex, existing.Count));
        for (int i = 0; i < newItems.Count; i++)
        {
            DockItem newItem = newItems[i];
            if (newItem == null || string.IsNullOrEmpty(newItem.Command))
            {
                continue;
            }

            string key = NormalizeCommandForCompare(newItem.Command);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            int existingIndex = FindItemIndex(existing, key);
            DockItem itemToInsert = newItem;
            if (existingIndex >= 0)
            {
                itemToInsert = existing[existingIndex];
                existing.RemoveAt(existingIndex);
                if (existingIndex < index)
                {
                    index--;
                }
            }

            int targetIndex = Math.Max(0, Math.Min(index, existing.Count));
            bool changed = existingIndex < 0 || existingIndex != targetIndex;
            existing.Insert(targetIndex, itemToInsert);
            index = targetIndex + 1;
            if (changed)
            {
                changedCount++;
            }
        }

        return DockItem.SerializeItems(existing);
    }

    public static string NormalizeCommandForCompare(string command)
    {
        string path = ExtractCommandPath(command);
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path).Trim().Trim('"')).TrimEnd('\\');
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }

    private static int FindItemIndex(List<DockItem> items, string normalizedCommand)
    {
        if (items == null || string.IsNullOrEmpty(normalizedCommand))
        {
            return -1;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(NormalizeCommandForCompare(items[i].Command), normalizedCommand, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsSupportedProgramPath(string path)
    {
        string extension = Path.GetExtension(path);
        return string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".appref-ms", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildProgramLabel(string path)
    {
        try
        {
            string extension = Path.GetExtension(path);
            if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrEmpty(info.FileDescription))
                {
                    return TrimProgramLabel(info.FileDescription);
                }

                if (!string.IsNullOrEmpty(info.ProductName))
                {
                    return TrimProgramLabel(info.ProductName);
                }
            }

            string fileName = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrEmpty(fileName))
            {
                return TrimProgramLabel(fileName);
            }
        }
        catch
        {
        }

        return "App";
    }

    private static string TrimProgramLabel(string label)
    {
        label = (label ?? string.Empty).Trim();
        return label.Length == 0 ? "App" : label;
    }

    private static string NormalizeSelectedProgramPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string value = Environment.ExpandEnvironmentVariables(path).Trim().Trim('"');
        try
        {
            return Path.GetFullPath(value);
        }
        catch
        {
            return value;
        }
    }

    private static string ExtractCommandPath(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return string.Empty;
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim();
        if (value.StartsWith("\"", StringComparison.Ordinal))
        {
            int end = value.IndexOf('"', 1);
            if (end > 1)
            {
                return value.Substring(1, end - 1);
            }
        }

        if (File.Exists(value))
        {
            return value;
        }

        int split = value.IndexOf(' ');
        if (split > 0)
        {
            string candidate = value.Substring(0, split);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return value;
    }
}
