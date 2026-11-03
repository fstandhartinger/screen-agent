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
        catch(Exception ex){failure=$"{ex.GetType().Name}: 0x{ex.HResult:x8} {Redact.Message(ex.Message,80)}";Diag.Fail("ocr-winrt-file",ex);}
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
        catch(Exception ex){failure=$"{ex.GetType().Name}: 0x{ex.HResult:x8}";Diag.Fail("ocr-winrt-memory",ex);}   // Tesseract takes over below
        return ReadTesseractMemory(png) with{WinRtFailure=failure};
    }
    // The ids on the Desk's pages are small, faint and monospaced: whole-screen OCR misreads them (real pages at 100-200 %: 2 of 11 read right).
    // So: tiles of the NATIVE frame, three times the size, and each tile flattened to "distance from its own background", which turns faint text
    // into black on white while bright text simply stays black. Tiles already read are remembered by content, so a frame after a small change costs one tile.
    // Wrong readings are harmless: the caller keeps only what the registry knows.
    public static async Task<OcrOutput> FindIds(System.Drawing.Bitmap frame,CancellationToken ct)
    {
        var found=new List<string>();var engine=(Environment.GetEnvironmentVariable("SCREENAGENT_OCR_LANG") is {} only?OcrEngine.TryCreateFromLanguage(new Language(only)):OcrEngine.TryCreateFromLanguage(new Language("en-US")))??OcrEngine.TryCreateFromUserProfileLanguages();
        if(engine is null)
        {
            using var whole=new MemoryStream();frame.Save(whole,System.Drawing.Imaging.ImageFormat.Png);
            var fallback=ReadTesseractMemory(whole.ToArray());return fallback with{Ids=ArtifactMatcher.FindLoose(fallback.Text),WinRtFailure="no-ocr-language"};
        }
        foreach(var box in OcrTiles.Grid(frame.Width,frame.Height))
        {
            ct.ThrowIfCancellationRequested();
            using var tile=frame.Clone(new System.Drawing.Rectangle(box.X,box.Y,box.W,box.H),System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            found.AddRange(await ReadTile(engine,tile));
        }
        return new("winrt+tiles:"+engine.RecognizerLanguage.LanguageTag,"",found.Distinct(StringComparer.Ordinal).ToArray(),null);
    }
    static readonly Dictionary<long,string[]> TileCache=[];
    static async Task<string[]> ReadTile(OcrEngine engine,System.Drawing.Bitmap tile)
    {
        var data=tile.LockBits(new(0,0,tile.Width,tile.Height),System.Drawing.Imaging.ImageLockMode.ReadOnly,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        byte[] raw=new byte[data.Stride*tile.Height];System.Runtime.InteropServices.Marshal.Copy(data.Scan0,raw,0,raw.Length);int stride=data.Stride;tile.UnlockBits(data);
        var hash=new HashCode();hash.AddBytes(raw);long key=hash.ToHashCode();
        lock(TileCache)if(TileCache.TryGetValue(key,out var known))return known;
        var ids=new List<string>();
        var pass=OcrTiles.Flatten(raw,stride,tile.Width,tile.Height,3,110); // measured on the real pages, ci/ocr-screens.ps1: 3x and 110 levels read every id at 100-200 %
        using var bitmap=Windows.Graphics.Imaging.SoftwareBitmap.CreateCopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pass.Bgra),BitmapPixelFormat.Bgra8,pass.W,pass.H,BitmapAlphaMode.Premultiplied);
        var result=await engine.RecognizeAsync(bitmap);
        ids.AddRange(ArtifactMatcher.FindLoose(string.Join("\n",result.Lines.Select(l=>l.Text))));
        var value=ids.Distinct(StringComparer.Ordinal).ToArray();
        lock(TileCache){if(TileCache.Count>=128)TileCache.Clear();TileCache[key]=value;}
        return value;
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
        catch(Exception ex)
        {
            Diag.Fail("ocr-ids-intersecting",ex);   // Tesseract takes over
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
