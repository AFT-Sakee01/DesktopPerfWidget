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

internal static class Logger
{
    private const long MaxLogDirectoryBytes = 10L * 1024L * 1024L;
    private static readonly object SyncRoot = new object();

    public static string DirectoryPath
    {
        get
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopPerfWidget");
        }
    }

    public static string LogPath
    {
        get { return Path.Combine(DirectoryPath, "DesktopPerfWidget.log"); }
    }

    public static string ErrorLogPath
    {
        get { return Path.Combine(DirectoryPath, "error.log"); }
    }

    public static void Info(string message)
    {
        Append(LogPath, "INFO", message);
    }

    public static void Error(Exception ex)
    {
        string text = ex.ToString();
        Append(LogPath, "ERROR", text);
        Append(ErrorLogPath, "ERROR", text);
    }

    private static void Append(string path, string level, string message)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(DirectoryPath);
                string line = DateTime.Now.ToString("u") + " [" + level + "] " + message + Environment.NewLine;
                EnforceLogDirectoryLimit(path, Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(path, line, Encoding.UTF8);
                EnforceLogDirectoryLimit(path, 0);
            }
        }
        catch
        {
        }
    }

    private static void EnforceLogDirectoryLimit(string activePath, long incomingBytes)
    {
        DirectoryInfo directory = new DirectoryInfo(DirectoryPath);
        if (!directory.Exists)
        {
            return;
        }

        FileInfo[] files = directory.GetFiles("*.log");
        long total = 0;
        for (int i = 0; i < files.Length; i++)
        {
            total += files[i].Length;
        }

        long budget = MaxLogDirectoryBytes - Math.Max(0, incomingBytes);
        if (budget < 4096)
        {
            budget = MaxLogDirectoryBytes;
        }

        if (total <= budget)
        {
            return;
        }

        Array.Sort(files, CompareLogFileAge);
        string activeFullPath = Path.GetFullPath(activePath);
        for (int i = 0; i < files.Length && total > budget; i++)
        {
            if (string.Equals(files[i].FullName, activeFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            long length = files[i].Length;
            try
            {
                files[i].Delete();
                total -= length;
            }
            catch
            {
            }
        }

        if (total > budget && File.Exists(activePath))
        {
            long otherTotal = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (!string.Equals(files[i].FullName, activeFullPath, StringComparison.OrdinalIgnoreCase) && files[i].Exists)
                {
                    otherTotal += files[i].Length;
                }
            }

            long keepBytes = Math.Max(4096, budget - otherTotal);
            TrimFileToLastBytes(activePath, keepBytes);
        }
    }

    private static int CompareLogFileAge(FileInfo left, FileInfo right)
    {
        int result = left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc);
        if (result != 0)
        {
            return result;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static void TrimFileToLastBytes(string path, long keepBytes)
    {
        try
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists || file.Length <= keepBytes)
            {
                return;
            }

            byte[] buffer = new byte[(int)keepBytes];
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                input.Seek(-keepBytes, SeekOrigin.End);
                int offset = 0;
                while (offset < buffer.Length)
                {
                    int read = input.Read(buffer, offset, buffer.Length - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }

            File.WriteAllBytes(path, buffer);
        }
        catch
        {
        }
    }
}
