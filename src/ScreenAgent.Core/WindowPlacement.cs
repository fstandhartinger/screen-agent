namespace ScreenAgent.Core;
public readonly record struct PxRect(int X,int Y,int W,int H)
{
    public int Right=>X+W;
    public int Bottom=>Y+H;
    public bool Contains(PxRect o)=>o.X>=X&&o.Y>=Y&&o.Right<=Right&&o.Bottom<=Bottom;
    public PxRect Intersect(PxRect o)
    {
        int l=Math.Max(X,o.X),t=Math.Max(Y,o.Y),r=Math.Min(Right,o.Right),b=Math.Min(Bottom,o.Bottom);
        return r>l&&b>t?new(l,t,r-l,b-t):new(0,0,0,0);
    }
}
// Pure pixel math for the Desk window and the status toast. All inputs and outputs are device pixels of the
// target monitor (the process is per-monitor DPI aware), so the result is right at 100 %, 150 % and 200 %.
public static class WindowPlacement
{
    public const int DeskDefaultWidthDip=520,DeskDefaultHeightDip=780,DeskMinWidthDip=360,DeskMinHeightDip=440;
    // Small, out of the way: bottom-right corner of the work area (above the taskbar), never off-screen.
    public static PxRect BottomRight(PxRect work,int width,int height,int marginPx)
    {
        int w=Math.Min(width,work.W),h=Math.Min(height,work.H);
        return new(work.Right-w-marginPx<work.X?work.X:work.Right-w-marginPx,work.Bottom-h-marginPx<work.Y?work.Y:work.Bottom-h-marginPx,w,h);
    }
    // Desk window: remembered size if sane, else a default; always fits the work area; remembered position only
    // when its title bar is still reachable on some monitor, else top-right of the work area.
    public static PxRect Desk(PxRect work,double scale,PxRect? saved,IReadOnlyList<PxRect> allWork)
    {
        int margin=(int)Math.Round(12*scale);
        int minW=(int)Math.Round(DeskMinWidthDip*scale),minH=(int)Math.Round(DeskMinHeightDip*scale);
        int maxW=Math.Max(1,work.W-2*margin),maxH=Math.Max(1,work.H-2*margin);
        int w=saved is {W:>0} s1?s1.W:(int)Math.Round(DeskDefaultWidthDip*scale);
        int h=saved is {H:>0} s2?s2.H:(int)Math.Round(DeskDefaultHeightDip*scale);
        w=Math.Min(Math.Max(w,Math.Min(minW,maxW)),maxW);h=Math.Min(Math.Max(h,Math.Min(minH,maxH)),maxH);
        if(saved is {} p)
        {
            var title=new PxRect(p.X,p.Y,w,(int)Math.Round(40*scale));
            if(allWork.Any(a=>a.Intersect(title) is {W:>=120,H:>=24}))
            {
                // Keep the remembered spot but pull the whole window back inside the monitor it mostly sits on.
                var area=allWork.OrderByDescending(a=>a.Intersect(new PxRect(p.X,p.Y,w,h)).W*(long)a.Intersect(new PxRect(p.X,p.Y,w,h)).H).First();
                int x=Math.Min(Math.Max(p.X,area.X),area.Right-w),y=Math.Min(Math.Max(p.Y,area.Y),area.Bottom-h);
                return new(x,y,w,h);
            }
        }
        return new(work.Right-w-margin,work.Y+margin,w,h);
    }
}
