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

internal sealed class DockItem
{
    public DockItem(string label, string command)
    {
        this.Label = label ?? string.Empty;
        this.Command = command ?? string.Empty;
    }

    public string Label { get; private set; }
    public string Command { get; private set; }

    public static string SerializeItems(List<DockItem> items)
    {
        if (items == null || items.Count == 0)
        {
            return string.Empty;
        }

        List<string> lines = new List<string>();
        for (int i = 0; i < items.Count; i++)
        {
            DockItem item = items[i];
            if (item == null || string.IsNullOrEmpty(item.Command))
            {
                continue;
            }

            string label = SanitizeTextPart(item.Label);
            string command = SanitizeTextPart(item.Command);
            if (command.Length == 0)
            {
                continue;
            }

            lines.Add(label + "|" + command);
        }

        return string.Join("\r\n", lines.ToArray());
    }

    public static string RemoveItemAt(string text, int index, out bool removed)
    {
        removed = false;
        List<DockItem> items = ParseItems(text);
        if (index < 0 || index >= items.Count)
        {
            return text ?? string.Empty;
        }

        items.RemoveAt(index);
        removed = true;
        return SerializeItems(items);
    }

    public static string RemoveItemByCommand(string text, string command, out bool removed)
    {
        removed = false;
        List<DockItem> items = ParseItems(text);
        string target = NormalizeCommandForCompare(command);
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(NormalizeCommandForCompare(items[i].Command), target, StringComparison.OrdinalIgnoreCase))
            {
                items.RemoveAt(i);
                removed = true;
                return SerializeItems(items);
            }
        }

        return text ?? string.Empty;
    }

    public static List<DockItem> ParseItems(string text)
    {
        List<DockItem> items = new List<DockItem>();
        if (string.IsNullOrEmpty(text))
        {
            return items;
        }

        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] lines = normalized.Split(new char[] { '\n' }, StringSplitOptions.None);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            string label;
            string command;
            int split = line.IndexOf('|');
            if (split >= 0)
            {
                label = line.Substring(0, split).Trim();
                command = line.Substring(split + 1).Trim();
            }
            else
            {
                command = line;
                label = BuildLabelFromCommand(command);
            }

            if (command.Length == 0)
            {
                continue;
            }

            if (label.Length == 0)
            {
                label = BuildLabelFromCommand(command);
            }

            items.Add(new DockItem(label, command));
        }

        return items;
    }

    private static string SanitizeTextPart(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        return value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', ' ').Trim();
    }

    private static string NormalizeCommandForCompare(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return string.Empty;
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim().Trim('"');
        try
        {
            return Path.GetFullPath(value).TrimEnd('\\');
        }
        catch
        {
            return value;
        }
    }

    private static string BuildLabelFromCommand(string command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return "App";
        }

        string value = Environment.ExpandEnvironmentVariables(command).Trim().Trim('"');
        try
        {
            string fileName = Path.GetFileNameWithoutExtension(value);
            if (!string.IsNullOrEmpty(fileName))
            {
                return fileName;
            }
        }
        catch
        {
        }

        int colon = value.IndexOf(':');
        if (colon > 1)
        {
            return value.Substring(0, colon);
        }

        return value.Length > 0 ? value : "App";
    }
}
