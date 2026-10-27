$ErrorActionPreference = 'Stop'

$assetDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\RemoteCli.Desktop\Assets'
$assetDirectory = (Resolve-Path -LiteralPath $assetDirectory).Path

$rendererSource = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Collections.Generic;

public static class LongShellIconRenderer
{
    private static GraphicsPath CreateRoundedRectangle(float x, float y, float width, float height, float radius)
    {
        GraphicsPath path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static GraphicsPath CreateLineArt()
    {
        GraphicsPath path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(42, 337, 42, 146);
        path.AddBezier(42, 146, 43, 123, 52, 91, 62, 78);
        path.AddBezier(62, 78, 74, 77, 90, 95, 105, 112);
        path.AddBezier(105, 112, 125, 136, 146, 162, 157, 171);
        path.AddBezier(157, 171, 158, 177, 163, 179, 165, 183);
        path.AddBezier(165, 183, 172, 192, 181, 204, 185, 215);
        path.AddBezier(185, 215, 188, 220, 184, 223, 178, 227);
        path.AddBezier(178, 227, 147, 248, 102, 270, 72, 285);

        path.StartFigure();
        path.AddBezier(42, 337, 96, 337, 145, 334, 181, 341);
        path.AddBezier(181, 341, 217, 343, 254, 339, 278, 337);
        path.AddBezier(278, 337, 294, 334, 299, 317, 304, 294);
        path.AddBezier(304, 294, 312, 258, 318, 197, 320, 145);

        path.StartFigure();
        path.AddBezier(334, 42, 344, 39, 352, 53, 366, 65);
        path.AddBezier(366, 65, 390, 86, 417, 103, 441, 124);
        path.AddBezier(441, 124, 470, 149, 491, 166, 504, 213);

        path.StartFigure();
        path.AddBezier(396, 289, 388, 284, 379, 290, 376, 300);
        path.AddBezier(376, 300, 367, 308, 372, 324, 382, 337);
        path.AddBezier(382, 337, 390, 348, 401, 357, 412, 354);
        path.AddBezier(412, 354, 426, 350, 442, 338, 447, 327);
        path.AddBezier(447, 327, 450, 313, 446, 299, 435, 290);
        path.AddBezier(435, 290, 425, 282, 410, 277, 400, 278);
        path.AddBezier(400, 278, 394, 278, 391, 284, 396, 289);
        path.CloseFigure();

        return path;
    }

    private static Bitmap Render(int size)
    {
        Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);

        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (GraphicsPath tile = CreateRoundedRectangle(35, 35, 442, 442, 106))
        using (SolidBrush background = new SolidBrush(Color.FromArgb(32, 33, 31)))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.ScaleTransform(size / 512f, size / 512f);
            graphics.FillPath(background, tile);

            float scale = size / 512f;
            using (Matrix transform = new Matrix(0.762f * scale, 0, 0, 0.762f * scale, 46f * scale, 104f * scale))
            using (GraphicsPath lineArt = CreateLineArt())
            using (Pen stroke = new Pen(Color.FromArgb(242, 239, 232), 17f))
            {
                graphics.Transform = transform;
                stroke.StartCap = LineCap.Round;
                stroke.EndCap = LineCap.Round;
                stroke.LineJoin = LineJoin.Round;
                graphics.DrawPath(stroke, lineArt);
            }
        }

        return bitmap;
    }

    private static byte[] RenderPng(int size)
    {
        using (Bitmap bitmap = Render(size))
        using (MemoryStream stream = new MemoryStream())
        {
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
    }

    public static void SaveAssets(string assetDirectory)
    {
        byte[] mainImage = RenderPng(1254);
        File.WriteAllBytes(Path.Combine(assetDirectory, "longshell-icon.png"), mainImage);

        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> images = new List<byte[]>();
        foreach (int size in sizes)
        {
            images.Add(RenderPng(size));
        }

        string iconPath = Path.Combine(assetDirectory, "longshell-icon.ico");
        using (FileStream file = new FileStream(iconPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter writer = new BinaryWriter(file))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)sizes.Length);

            int imageOffset = 6 + (16 * sizes.Length);
            for (int index = 0; index < sizes.Length; index++)
            {
                int size = sizes[index];
                byte dimension = size == 256 ? (byte)0 : (byte)size;
                writer.Write(dimension);
                writer.Write(dimension);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write((uint)images[index].Length);
                writer.Write((uint)imageOffset);
                imageOffset += images[index].Length;
            }

            foreach (byte[] image in images)
            {
                writer.Write(image);
            }
        }
    }
}
'@

Add-Type -TypeDefinition $rendererSource -Language CSharp -ReferencedAssemblies @('System.Drawing.dll')
[LongShellIconRenderer]::SaveAssets($assetDirectory)
        Write-Output "Generated longshell-icon.png and longshell-icon.ico in $assetDirectory"
