using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
namespace ScreenAgent;
internal static class FrameGrid
{
    // 320x180 grey grid for the change test (ChangeDetector).
    public static byte[] Of(Bitmap image)
    {
        var data=image.LockBits(new Rectangle(0,0,image.Width,image.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try{var buffer=new byte[data.Stride*data.Height];Marshal.Copy(data.Scan0,buffer,0,buffer.Length);return ChangeDetector.FromBgra(buffer,image.Width,image.Height,data.Stride);}
        finally{image.UnlockBits(data);}
    }
}
