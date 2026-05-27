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

internal static class DrawingUtil
{
    public static void DrawImageWithAlpha(Graphics target, Bitmap image, int alpha)
    {
        alpha = Math.Max(0, Math.Min(255, alpha));
        if (alpha <= 0)
        {
            return;
        }

        if (alpha >= 255)
        {
            target.DrawImageUnscaled(image, 0, 0);
            return;
        }

        using (ImageAttributes attributes = new ImageAttributes())
        {
            ColorMatrix matrix = new ColorMatrix();
            matrix.Matrix33 = alpha / 255.0f;
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            target.DrawImage(
                image,
                new Rectangle(0, 0, image.Width, image.Height),
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attributes);
        }
    }

    public static void DrawImageWithAlpha(Graphics target, Image image, RectangleF destination, int alpha)
    {
        alpha = Math.Max(0, Math.Min(255, alpha));
        if (alpha <= 0)
        {
            return;
        }

        if (alpha >= 255)
        {
            target.DrawImage(image, destination);
            return;
        }

        using (ImageAttributes attributes = new ImageAttributes())
        {
            ColorMatrix matrix = new ColorMatrix();
            matrix.Matrix33 = alpha / 255.0f;
            attributes.SetColorMatrix(matrix, ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
            Rectangle rectangle = Rectangle.Round(destination);
            target.DrawImage(
                image,
                rectangle,
                0,
                0,
                image.Width,
                image.Height,
                GraphicsUnit.Pixel,
                attributes);
        }
    }
}
