namespace ScreenAgent.Core;
// Did the screen change enough for the agent to need a new picture? The frame is reduced to a 960x540 grey grid (area average, so thin text keeps some weight);
// a pixel counts as changed when it moved by more than Level; the grid is cut into 16x9 tiles overlapping by half, and a tile counts when it has MinPixels changed
// pixels spread over at least MinWidth columns. On a real page at 1920x1080 a whole 14 px number changes dozens of pixels, one digit of it more than a handful, and a caret is a line one or two columns wide: the shape tells them apart, so a number counts and a caret does not. A clock digit counts too, which costs one frame a minute.
public static class ChangeDetector
{
    public const int Width=960,Height=540,TilesX=16,TilesY=9,Level=12,MinPixels=6,MinWidth=3;
    // Straight BGRA/BGRX pixels (System.Drawing Format32bppArgb), rows `stride` bytes apart.
    public static byte[] FromBgra(ReadOnlySpan<byte> pixels,int width,int height,int stride)
    {
        if(width<=0||height<=0||stride<width*4||pixels.Length<(height-1)*stride+width*4)throw new ArgumentException("pixel buffer too small");
        var sum=new int[Width*Height];var count=new int[Width*Height];
        for(int y=0;y<height;y++)
        {
            int row=y*stride,gy=(int)((long)y*Height/height)*Width;
            for(int x=0;x<width;x++)
            {
                int i=row+x*4,g=(int)((long)x*Width/width)+gy;
                sum[g]+=(pixels[i+2]*77+pixels[i+1]*150+pixels[i]*29)>>8;count[g]++;
            }
        }
        var grid=new byte[Width*Height];
        for(int i=0;i<grid.Length;i++)grid[i]=count[i]==0?(byte)0:(byte)(sum[i]/count[i]);
        return grid;
    }
    public static bool Changed(byte[] before,byte[] after,int level=Level,int minPixels=MinPixels)=>ChangedTiles(before,after,level,minPixels)>0;
    public static int ChangedTiles(byte[] before,byte[] after,int level=Level,int minPixels=MinPixels)
    {
        if(before.Length!=Width*Height||after.Length!=Width*Height)throw new ArgumentException("960x540 grid expected");
        int tw=Width/TilesX,th=Height/TilesY,tiles=0;
        for(int top=0;top+th<=Height;top+=th/2)for(int left=0;left+tw<=Width;left+=tw/2)
        {
            int count=0,first=int.MaxValue,last=-1;
            for(int y=top;y<top+th;y++)for(int x=left;x<left+tw;x++)
                if(Math.Abs(before[y*Width+x]-after[y*Width+x])>level){count++;first=Math.Min(first,x);last=Math.Max(last,x);}
            if(count>=minPixels&&last-first+1>=MinWidth)tiles++;
        }
        return tiles;
    }
}
// Telemetry for unchanged frames: one "deduped" line per run of unchanged frames and then one per 30 s, not one every interval.
public sealed class RunLog(long repeatMs=30000)
{
    string? last;long at;
    public bool ShouldLog(string key,long now)
    {
        if(key==last&&now-at<repeatMs)return false;
        last=key;at=now;return true;
    }
    public void Break()=>last=null;
}
