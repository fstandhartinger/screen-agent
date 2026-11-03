using System.Drawing;
using System.Drawing.Imaging;
namespace ScreenAgent;
internal sealed record HighlightImages(byte[] Png,byte[] Jpeg,Box Bbox);
// The picture behind "[Markierung]": the clean capture with the strokes drawn into it (magenta, 5 physical px), the crop around them with margin,
// and the whole monitor as context. All in memory.
internal static class HighlightBuilder
{
    public static HighlightImages? Build(DrawOverlay overlay,Bitmap clean)
    {
        var points=overlay.Strokes.SelectMany(s=>s).ToArray();
        if(points.Length==0){Diag.Event("highlight.build.fail",new(){["why"]="no-points"});return null;}
        overlay.Composite(clean);
        if(HighlightGeometry.Crop(points.Min(p=>p.X),points.Max(p=>p.X),points.Min(p=>p.Y),points.Max(p=>p.Y),clean.Width,clean.Height) is not {} box){Diag.Event("highlight.build.fail",new(){["why"]="no-crop",["points"]=points.Length,["w"]=clean.Width,["h"]=clean.Height});return null;}
        using var crop=clean.Clone(new Rectangle(box.x,box.y,box.w,box.h),PixelFormat.Format32bppArgb);
        byte[] png=PrivacyCapture.Png(crop);
        int w=crop.Width,h=crop.Height;
        while(png.Length>2*1024*1024&&w>64&&h>64)
        {
            w=Math.Max(1,(int)(w*.8));h=Math.Max(1,(int)(h*.8));
            using var resized=new Bitmap(crop,new Size(w,h));png=PrivacyCapture.Png(resized);
        }
        var (jpeg,_,_)=FrameJpeg.Encode(clean);
        return new(png,jpeg,new(box.x+overlay.Monitor.X,box.y+overlay.Monitor.Y,box.w,box.h));
    }
}
