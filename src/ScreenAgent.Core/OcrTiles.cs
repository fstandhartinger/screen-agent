namespace ScreenAgent.Core;
public readonly record struct TileBox(int X,int Y,int W,int H);
public readonly record struct TileImage(byte[] Bgra,int W,int H);
// Pure pixel work for the id OCR, so the behaviour is testable without Windows.
public static class OcrTiles
{
    public const int TileW=800,TileH=450,Overlap=60;
    // Overlapping tiles in native pixels: a badge on a tile border is whole in the next tile.
    public static IEnumerable<TileBox> Grid(int width,int height)
    {
        for(int y=0;;y+=TileH-Overlap)
        {
            int h=Math.Min(TileH,height-y);
            for(int x=0;;x+=TileW-Overlap)
            {
                int w=Math.Min(TileW,width-x);
                if(w>0&&h>0)yield return new(x,y,w,h);
                if(x+w>=width)break;
            }
            if(y+h>=height)break;
        }
    }
    // Upscales by `scale` (bilinear) and maps every pixel to its distance from the most common brightness of the tile: `range` levels of distance become full black.
    // Dark theme, light theme and faint text on a card all come out as black on white.
    public static TileImage Flatten(byte[] bgra,int stride,int w,int h,int scale,int range)
    {
        var gray=new byte[w*h];var histogram=new int[256];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            int i=y*stride+x*4;byte v=(byte)((bgra[i]*114+bgra[i+1]*587+bgra[i+2]*299)/1000);
            gray[y*w+x]=v;histogram[v]++;
        }
        int background=Array.IndexOf(histogram,histogram.Max());
        int W=w*scale,H=h*scale;var result=new byte[W*H*4];
        for(int Y=0;Y<H;Y++)
        {
            int gy=Y*(h-1)/Math.Max(1,H-1),ny=Math.Min(h-1,gy+1),fy=Y*(h-1)-gy*Math.Max(1,H-1);
            for(int X=0;X<W;X++)
            {
                int gx=X*(w-1)/Math.Max(1,W-1),nx=Math.Min(w-1,gx+1),fx=X*(w-1)-gx*Math.Max(1,W-1);
                int d=Math.Max(1,W-1),e=Math.Max(1,H-1);
                long top=gray[gy*w+gx]*(long)(d-fx)+gray[gy*w+nx]*(long)fx,bottom=gray[ny*w+gx]*(long)(d-fx)+gray[ny*w+nx]*(long)fx;
                int v=(int)((top*(e-fy)+bottom*fy)/(d*(long)e));
                int ink=Math.Min(255,Math.Abs(v-background)*255/range);
                byte o=(byte)(255-ink);int i=(Y*W+X)*4;
                result[i]=o;result[i+1]=o;result[i+2]=o;result[i+3]=255;
            }
        }
        return new(result,W,H);
    }
}
