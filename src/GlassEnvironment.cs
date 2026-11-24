using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexUsageTaskbar
{
    internal sealed class GlassEnvironment
    {
        // Each band contains actual image detail at different distances from the edge.
        const int Depth = 10;
        readonly int[][] bands = new int[4][];
        readonly Size[] sizes = new Size[4];
        internal bool Bright;

        internal static GlassEnvironment Read(Rectangle bounds, Rectangle monitor)
        {
            var env = new GlassEnvironment();
            double light = 0;
            int count = 0;
            try
            {
                for (int side = 0; side < 4; side++)
                {
                    bool horizontal = side % 2 == 0;
                    var size = horizontal ? new Size(bounds.Width, Depth) : new Size(Depth, bounds.Height);
                    int x = side == 1 ? bounds.Right : side == 3 ? bounds.Left - Depth : bounds.Left;
                    int y = side == 0 ? bounds.Top - Depth : side == 2 ? bounds.Bottom : bounds.Top;
                    x = Math.Max(monitor.Left, Math.Min(monitor.Right - size.Width, x));
                    y = Math.Max(monitor.Top, Math.Min(monitor.Bottom - size.Height, y));
                    using (var strip = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(strip))
                            g.CopyFromScreen(x, y, 0, 0, size, CopyPixelOperation.SourceCopy);
                        var rect = new Rectangle(Point.Empty, size);
                        var bits = strip.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        var pixels = new int[size.Width * size.Height];
                        try { Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length); }
                        finally { strip.UnlockBits(bits); }
                        env.bands[side] = pixels;
                        env.sizes[side] = size;
                        for (int i = 0; i < pixels.Length; i += 13)
                        {
                            int c = pixels[i];
                            light += .2126 * ((c >> 16) & 255) + .7152 * ((c >> 8) & 255) + .0722 * (c & 255);
                            count++;
                        }
                    }
                }
            }
            catch (Win32Exception) { return null; }
            env.Bright = light / count > 146;
            return env;
        }

        int Sample(int side, double along, double depth)
        {
            double across = side == 0 || side == 3 ? 1 - depth : depth;
            bool horizontal = side % 2 == 0;
            var size = sizes[side];
            double x = Math.Max(0, Math.Min(1, horizontal ? along : across)) * (size.Width - 1);
            double y = Math.Max(0, Math.Min(1, horizontal ? across : along)) * (size.Height - 1);
            int left = (int)x, top = (int)y;
            int right = Math.Min(left + 1, size.Width - 1), bottom = Math.Min(top + 1, size.Height - 1);
            var pixels = bands[side];
            int a = MixColor(pixels[top * size.Width + left], pixels[top * size.Width + right], x - left);
            int b = MixColor(pixels[bottom * size.Width + left], pixels[bottom * size.Width + right], x - left);
            return MixColor(a, b, y - top);
        }

        static int MixColor(int a, int b, double weight)
        {
            return (Blend(a, b, 16, weight) << 16) | (Blend(a, b, 8, weight) << 8) | Blend(a, b, 0, weight);
        }

        internal void DrawEdge(Graphics g, RectangleF body, float cornerRadius, float focus, float scale)
        {
            int w = (int)Math.Ceiling(body.Width * scale), h = (int)Math.Ceiling(body.Height * scale);
            double radius = cornerRadius * scale;
            double bevel = Math.Min(cornerRadius * .53, 11) * scale;
            double lx = (focus - .75) * 1.8, ly = -.8;
            double lightLength = Math.Sqrt(lx*lx + ly*ly);
            var pixels = new int[w*h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (x > radius && x < w-radius && y > bevel && y < h-bevel) continue;
                double px = x + .5 - w / 2.0, py = y + .5 - h / 2.0;
                double qx = Math.Abs(px) - w / 2.0 + radius, qy = Math.Abs(py) - h / 2.0 + radius;
                double ax = Math.Max(0, qx), ay = Math.Max(0, qy);
                double len = Math.Sqrt(ax*ax + ay*ay);
                double inside = radius - len - Math.Min(0, Math.Max(qx,qy));
                if (inside < 0 || inside >= bevel) continue;
                double nx = Math.Sign(px) * (len > 0 ? ax/len : qx > qy ? 1 : 0);
                double ny = Math.Sign(py) * (len > 0 ? ay/len : qy >= qx ? 1 : 0);
                double strength = 1 - inside/bevel;
                // Quarter-circle cross section. The surface normal turns toward
                // grazing incidence at the outline, concentrating the displacement there.
                double bend = 1 - Math.Sqrt(1 - strength * strength);
                int a = Sample(nx < 0 ? 3 : 1, (y + ny * bend * radius)/h, bend);
                int b = Sample(ny < 0 ? 0 : 2, (x + nx * bend * radius)/w, bend);
                double weight = ny*ny;
                int red = Blend(a,b,16,weight), green = Blend(a,b,8,weight), blue = Blend(a,b,0,weight);
                // Opposed specular lobes describe the bevel even on a flat wallpaper.
                double lighting = (nx*lx + ny*ly) / lightLength;
                double highlight = Math.Pow(Math.Abs(lighting), 3) * Math.Pow(strength, 2) *
                    (lighting > 0 ? .80 : .47);
                red += (int)((240-red)*highlight);
                green += (int)((252-green)*highlight);
                blue += (int)((255-blue)*highlight);
                int alpha = (int)(220 * Math.Sqrt(strength));
                pixels[y*w+x] = (alpha << 24) | (red << 16) | (green << 8) | blue;
            }
            using (var lens = new Bitmap(w,h,PixelFormat.Format32bppArgb))
            {
                var bits = lens.LockBits(new Rectangle(0,0,w,h),ImageLockMode.WriteOnly,PixelFormat.Format32bppArgb);
                try { Marshal.Copy(pixels,0,bits.Scan0,pixels.Length); }
                finally { lens.UnlockBits(bits); }
                g.DrawImage(lens, body, new RectangleF(0, 0, w, h), GraphicsUnit.Pixel);
            }
        }

        static int Blend(int a, int b, int shift, double weight)
        {
            return (int)(((a >> shift) & 255) * (1-weight) + ((b >> shift) & 255) * weight);
        }
    }
}
