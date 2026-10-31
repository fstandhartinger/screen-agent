using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Globalization;
namespace ScreenAgent;
internal sealed record OcrOutput(string Engine,string Text,string[] Ids,string? WinRtFailure);
internal static class Ocr
{
    public static async Task<OcrOutput> Read(string path)
    {
        string? failure=null;
        try
        {
            var file=await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            using var stream=await file.OpenReadAsync();
            var decoder=await BitmapDecoder.CreateAsync(stream);
            using var bitmap=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied);
            var engine=OcrEngine.TryCreateFromLanguage(new Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages() ?? throw new InvalidOperationException("No WinRT OCR language installed");
            var text=(await engine.RecognizeAsync(bitmap)).Text;
            return new("winrt",text,ArtifactMatcher.Find(text),null);
        }
        catch(Exception ex){failure=$"{ex.GetType().Name}: 0x{ex.HResult:x8} {ex.Message}";}
        NativeOcrLibraries.Ensure();
        using var tess=new Tesseract.TesseractEngine(Path.Combine(AppContext.BaseDirectory,"tessdata"),"eng+deu",Tesseract.EngineMode.LstmOnly);
        using var pix=Tesseract.Pix.LoadFromFile(path);
        using var page=tess.Process(pix,Tesseract.PageSegMode.Auto);
        string fallback=page.GetText();
        return new("tesseract",fallback,ArtifactMatcher.Find(fallback),failure);
    }
    // WinRT consumes the masked PNG in memory; no temporary screenshot file.
    public static async Task<OcrOutput> ReadMemory(byte[] png)
    {
        string? failure=null;
        try
        {
            using var stream=new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using(var writer=new Windows.Storage.Streams.DataWriter(stream)){writer.WriteBytes(png);await writer.StoreAsync();writer.DetachStream();}
            stream.Seek(0);var decoder=await BitmapDecoder.CreateAsync(stream);
            using var bitmap=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied);
            var engine=OcrEngine.TryCreateFromLanguage(new Language("en-US"))??OcrEngine.TryCreateFromUserProfileLanguages()??throw new InvalidOperationException("No WinRT OCR language installed");
            string text=(await engine.RecognizeAsync(bitmap)).Text;return new("winrt",text,ArtifactMatcher.Find(text),null);
        }
        catch(Exception ex){failure=$"{ex.GetType().Name}: 0x{ex.HResult:x8}";}
        return ReadTesseractMemory(png) with{WinRtFailure=failure};
    }
    public static async Task<string[]> IdsIntersecting(byte[] png,System.Drawing.Rectangle bounds)
    {
        try
        {
            using var stream=new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using(var writer=new Windows.Storage.Streams.DataWriter(stream)){writer.WriteBytes(png);await writer.StoreAsync();writer.DetachStream();}
            stream.Seek(0);var decoder=await BitmapDecoder.CreateAsync(stream);
            using var bitmap=await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,BitmapAlphaMode.Premultiplied);
            var engine=OcrEngine.TryCreateFromLanguage(new Language("en-US"))??OcrEngine.TryCreateFromUserProfileLanguages()??throw new InvalidOperationException();
            var result=await engine.RecognizeAsync(bitmap);
            return result.Lines.SelectMany(l=>l.Words).Where(w=>bounds.IntersectsWith(new((int)w.BoundingRect.X,(int)w.BoundingRect.Y,(int)Math.Ceiling(w.BoundingRect.Width),(int)Math.Ceiling(w.BoundingRect.Height))))
                .SelectMany(w=>ArtifactMatcher.Find(w.Text)).Distinct().ToArray();
        }
        catch
        {
            NativeOcrLibraries.Ensure();
            using var tess=new Tesseract.TesseractEngine(Path.Combine(AppContext.BaseDirectory,"tessdata"),"eng+deu",Tesseract.EngineMode.LstmOnly);
            using var pix=Tesseract.Pix.LoadFromMemory(png);using var page=tess.Process(pix);using var words=page.GetIterator();
            var found=new List<string>();words.Begin();
            do {if(words.TryGetBoundingBox(Tesseract.PageIteratorLevel.Word,out var b)&&bounds.IntersectsWith(new(b.X1,b.Y1,b.Width,b.Height)))found.AddRange(ArtifactMatcher.Find(words.GetText(Tesseract.PageIteratorLevel.Word)??""));}
            while(words.Next(Tesseract.PageIteratorLevel.Word));
            return found.Distinct().ToArray();
        }
    }
    public static OcrOutput ReadTesseractMemory(byte[] png)
    {
        NativeOcrLibraries.Ensure();
        using var tess=new Tesseract.TesseractEngine(Path.Combine(AppContext.BaseDirectory,"tessdata"),"eng+deu",Tesseract.EngineMode.LstmOnly);
        string text;
        using(var pix=Tesseract.Pix.LoadFromMemory(png))
        using(var page=tess.Process(pix,Tesseract.PageSegMode.Auto))text=page.GetText();
        // Small antialiased screen fonts can merge adjacent f glyphs. A second,
        // independently rasterized pass improves recall without relaxing ID syntax.
        using var input=new MemoryStream(png);using var source=new System.Drawing.Bitmap(input);
        using var scaled=new System.Drawing.Bitmap(source.Width*2,source.Height*2);
        using(var graphics=System.Drawing.Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source,0,0,scaled.Width,scaled.Height);
        }
        using var bytes=new MemoryStream();scaled.Save(bytes,System.Drawing.Imaging.ImageFormat.Png);
        using(var pix=Tesseract.Pix.LoadFromMemory(bytes.ToArray()))
        using(var page=tess.Process(pix,Tesseract.PageSegMode.SparseText))text+="\n"+page.GetText();
        return new("tesseract",text,ArtifactMatcher.Find(text),null);
    }
}
