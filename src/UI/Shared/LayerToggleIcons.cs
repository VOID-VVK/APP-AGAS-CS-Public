using Godot;

namespace AppAgasCs.UI.Shared;

/// <summary>
/// Small raster icons for layer visibility, lock, and package-share toggles.
/// Share uses the three-node "send to others" mark: this layer's images go in the scene package.
/// </summary>
internal static class LayerToggleIcons
{
    private static Texture2D? _eyeOpen;
    private static Texture2D? _eyeClosed;
    private static Texture2D? _lockClosed;
    private static Texture2D? _lockOpen;
    private static Texture2D? _shareOn;
    private static Texture2D? _shareOff;
    private static Texture2D? _rename;
    private static Texture2D? _moveUp;
    private static Texture2D? _moveDown;
    private static Texture2D? _prompt;
    private static Texture2D? _delete;

    private static readonly Color On = new(0.93f, 0.95f, 0.98f, 1f);
    private static readonly Color Off = new(0.52f, 0.56f, 0.62f, 1f);
    private static readonly Color Accent = new(0.35f, 0.62f, 0.98f, 1f);

    public static Texture2D Eye(bool visible) => visible
        ? _eyeOpen ??= DrawEye(open: true)
        : _eyeClosed ??= DrawEye(open: false);

    public static Texture2D Lock(bool locked) => locked
        ? _lockClosed ??= DrawLock(open: false)
        : _lockOpen ??= DrawLock(open: true);

    public static Texture2D Share(bool shared) => shared
        ? _shareOn ??= DrawShare(on: true)
        : _shareOff ??= DrawShare(on: false);

    public static Texture2D Rename() => _rename ??= DrawRename();

    public static Texture2D MoveUp() => _moveUp ??= DrawMove(up: true);

    public static Texture2D MoveDown() => _moveDown ??= DrawMove(up: false);

    public static Texture2D Prompt() => _prompt ??= DrawPrompt();

    public static Texture2D Delete() => _delete ??= DrawDelete();

    private static Texture2D DrawEye(bool open)
    {
        var image = Blank();
        Color color = open ? On : Off;
        Plot(image, 3, 8, color); Plot(image, 4, 7, color); Plot(image, 5, 6, color);
        Plot(image, 6, 6, color); Plot(image, 7, 6, color); Plot(image, 8, 6, color);
        Plot(image, 9, 6, color); Plot(image, 10, 6, color); Plot(image, 11, 7, color);
        Plot(image, 12, 8, color); Plot(image, 11, 9, color); Plot(image, 10, 10, color);
        Plot(image, 9, 10, color); Plot(image, 8, 10, color); Plot(image, 7, 10, color);
        Plot(image, 6, 10, color); Plot(image, 5, 10, color); Plot(image, 4, 9, color);
        if (open)
        {
            Fill(image, 7, 7, 3, 3, color);
            Plot(image, 8, 8, new Color(0.12f, 0.14f, 0.18f, 1f));
        }
        else
        {
            for (int x = 4; x <= 12; x++)
            {
                Plot(image, x, 8, color);
            }
        }

        return Bake(image);
    }

    private static Texture2D DrawLock(bool open)
    {
        var image = Blank();
        Color color = open ? Off : On;
        Plot(image, 6, 5, color); Plot(image, 6, 4, color); Plot(image, 7, 3, color);
        Plot(image, 8, 3, color); Plot(image, 9, 3, color); Plot(image, 10, 4, color);
        if (open)
        {
            Plot(image, 10, 5, color);
            Plot(image, 10, 6, color);
        }
        else
        {
            Plot(image, 10, 5, color);
            Plot(image, 10, 6, color);
            Plot(image, 6, 6, color);
        }

        Fill(image, 5, 7, 7, 6, color);
        Plot(image, 8, 9, new Color(0.12f, 0.14f, 0.18f, 1f));
        Plot(image, 8, 10, new Color(0.12f, 0.14f, 0.18f, 1f));
        return Bake(image);
    }

    private static Texture2D DrawShare(bool on)
    {
        var image = Blank();
        Color color = on ? Accent : Off;
        Fill(image, 2, 6, 4, 4, color);
        Fill(image, 10, 2, 4, 4, color);
        Fill(image, 10, 10, 4, 4, color);
        Line(image, 6, 8, 10, 4, color);
        Line(image, 6, 8, 10, 12, color);
        return Bake(image);
    }

    private static Texture2D DrawRename()
    {
        var image = Blank();
        Line(image, 3, 12, 11, 4, On);
        Line(image, 4, 13, 12, 5, On);
        Plot(image, 2, 13, On);
        Plot(image, 3, 13, On);
        Plot(image, 12, 3, Accent);
        Plot(image, 13, 4, Accent);
        return Bake(image);
    }

    private static Texture2D DrawMove(bool up)
    {
        var image = Blank();
        int tipY = up ? 3 : 12;
        int tailY = up ? 13 : 2;
        Line(image, 8, tipY, 8, tailY, On);
        Line(image, 9, tipY, 9, tailY, On);
        Line(image, 4, up ? 7 : 8, 8, tipY, On);
        Line(image, 9, tipY, 13, up ? 7 : 8, On);
        return Bake(image);
    }

    private static Texture2D DrawPrompt()
    {
        var image = Blank();
        Fill(image, 2, 3, 12, 9, On);
        Fill(image, 4, 5, 8, 5, Colors.Transparent);
        Plot(image, 4, 12, On);
        Plot(image, 4, 13, On);
        Plot(image, 5, 12, On);
        Plot(image, 6, 12, On);
        return Bake(image);
    }

    private static Texture2D DrawDelete()
    {
        var image = Blank();
        Fill(image, 4, 5, 8, 9, On);
        Fill(image, 6, 7, 1, 5, new Color(0.12f, 0.14f, 0.18f, 1f));
        Fill(image, 9, 7, 1, 5, new Color(0.12f, 0.14f, 0.18f, 1f));
        Fill(image, 3, 3, 10, 2, Accent);
        Fill(image, 6, 2, 4, 1, Accent);
        return Bake(image);
    }

    private static Image Blank()
    {
        var image = Image.CreateEmpty(16, 16, false, Image.Format.Rgba8);
        image.Fill(Colors.Transparent);
        return image;
    }

    private static Texture2D Bake(Image image)
    {
        image.Resize(AgasIconButtonFactory.IconSize, AgasIconButtonFactory.IconSize, Image.Interpolation.Nearest);
        var texture = ImageTexture.CreateFromImage(image);
        texture.ResourceName = "LayerToggleIcon";
        return texture;
    }

    private static void Fill(Image image, int x, int y, int width, int height, Color color)
    {
        for (int py = y; py < y + height; py++)
        {
            for (int px = x; px < x + width; px++)
            {
                Plot(image, px, py, color);
            }
        }
    }

    private static void Line(Image image, int x0, int y0, int x1, int y1, Color color)
    {
        int dx = Mathf.Abs(x1 - x0);
        int dy = -Mathf.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        int x = x0;
        int y = y0;
        while (true)
        {
            Plot(image, x, y, color);
            if (x == x1 && y == y1)
            {
                break;
            }

            int doubled = 2 * error;
            if (doubled >= dy)
            {
                error += dy;
                x += sx;
            }

            if (doubled <= dx)
            {
                error += dx;
                y += sy;
            }
        }
    }

    private static void Plot(Image image, int x, int y, Color color)
    {
        if ((uint)x >= 16 || (uint)y >= 16)
        {
            return;
        }

        image.SetPixel(x, y, color);
    }
}
