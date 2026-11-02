using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace ScreenAgent;
// One small round icon per state, drawn at runtime (no resource files): grey = ready, green = session,
// blue = dictating or drawing, yellow = paused, orange = needs attention.
internal static class TrayIcons
{
    static readonly Dictionary<Level,Icon> cache=new();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    public static Icon For(Level level)
    {
        if(cache.TryGetValue(level,out var icon))return icon;
        using var bitmap=new Bitmap(32,32);
        using(var g=Graphics.FromImage(bitmap))
        {
            g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.Transparent);
            var c=StatusToast.Colour(level);
            using var fill=new SolidBrush(Color.FromArgb(c.R,c.G,c.B));
            using var ring=new Pen(Color.FromArgb(235,28,28,34),3);
            g.FillEllipse(fill,3,3,26,26);g.DrawEllipse(ring,3,3,26,26);
            using var eye=new SolidBrush(Color.FromArgb(235,28,28,34));
            g.FillEllipse(eye,11,11,10,10);
        }
        IntPtr handle=bitmap.GetHicon();
        try{icon=(Icon)Icon.FromHandle(handle).Clone();}finally{DestroyIcon(handle);}
        return cache[level]=icon;
    }
}
