using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
namespace ScreenAgent;
// Screenshot for the vision model: at most 1600 px wide, JPEG, under the gateway's 300 KB. Memory only; nothing touches the disk.
internal static class FrameJpeg
{
    static readonly ImageCodecInfo Codec=ImageCodecInfo.GetImageEncoders().First(c=>c.FormatID==ImageFormat.Jpeg.Guid);
    public static (byte[] Bytes,int W,int H) Encode(Bitmap source)
    {
        var (w,h)=FrameSizing.Fit(source.Width,source.Height);
        for(int attempt=0;;attempt++)
        {
            using var scaled=new Bitmap(w,h,PixelFormat.Format24bppRgb);
            using(var g=Graphics.FromImage(scaled)){g.InterpolationMode=InterpolationMode.HighQualityBicubic;g.PixelOffsetMode=PixelOffsetMode.HighQuality;g.DrawImage(source,0,0,w,h);}
            byte[] last=[];
            foreach(int quality in FrameSizing.Qualities)
            {
                using var stream=new MemoryStream();using var parameters=new EncoderParameters(1);
                parameters.Param[0]=new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,(long)quality);
                scaled.Save(stream,Codec,parameters);last=stream.ToArray();
                if(last.Length<=FrameSizing.MaxBytes)return (last,w,h);
            }
            if(attempt>=6){Diag.Event("frame.encode.fail",new(){["why"]="too-large",["w"]=w,["h"]=h,["bytes"]=last.Length,["max"]=FrameSizing.MaxBytes,["srcW"]=source.Width,["srcH"]=source.Height});throw new InvalidDataException("Frame too large");}
            (w,h)=FrameSizing.Shrink(w,h);
        }
    }
}
