namespace ScreenAgent.Core;
// Did the screen change enough for the agent to need a new picture? The frame is reduced to a 320x180 grey grid (area average, so thin
// text keeps some weight); a pixel counts as changed when it moved by more than Level; the grid is cut into 16x9 tiles (20x20 grid pixels, overlapping by half)
// and the screen counts as changed when any tile has MinPixels changed pixels. A 5-digit number in a corner changes ~10 pixels of one tile, a blinking
// caret at most a handful (it is 1-2 px wide), so the number counts and the caret does not. A clock digit may or may not.
public static class ChangeDetector
{
    public const int Width=320,Height=180,TilesX=16,TilesY=9,Level=20,MinPixels=8;
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
    // Tiles are 20x20 grid pixels and overlap by half, so a number that straddles a tile border still lands whole in one tile.
    public static int ChangedTiles(byte[] before,byte[] after,int level=Level,int minPixels=MinPixels)
    {
        if(before.Length!=Width*Height||after.Length!=Width*Height)throw new ArgumentException("320x180 grid expected");
        int tw=Width/TilesX,th=Height/TilesY,stride=Width+1;
        var sum=new int[stride*(Height+1)];   // integral image of the "changed" mask
        for(int y=0;y<Height;y++)
        {
            int row=0;
            for(int x=0;x<Width;x++)
            {
                if(Math.Abs(before[y*Width+x]-after[y*Width+x])>level)row++;
                sum[(y+1)*stride+x+1]=sum[y*stride+x+1]+row;
            }
        }
        int tiles=0;
        for(int y=0;y+th<=Height;y+=th/2)for(int x=0;x+tw<=Width;x+=tw/2)
            if(sum[(y+th)*stride+x+tw]-sum[y*stride+x+tw]-sum[(y+th)*stride+x]+sum[y*stride+x]>=minPixels)tiles++;
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
