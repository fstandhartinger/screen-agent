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
        using var tess=new Tesseract.TesseractEngine(Path.Combine(AppContext.BaseDirectory,"tessdata"),"eng+deu",Tesseract.EngineMode.LstmOnly);
        using var pix=Tesseract.Pix.LoadFromFile(path);
        using var page=tess.Process(pix,Tesseract.PageSegMode.Auto);
        string fallback=page.GetText();
        return new("tesseract",fallback,ArtifactMatcher.Find(fallback),failure);
    }
    // In-memory capture path: Windows bitmap -> PNG memory -> Tesseract, never a disk frame.
    public static OcrOutput ReadMemory(byte[] png)
    {
        using var tess=new Tesseract.TesseractEngine(Path.Combine(AppContext.BaseDirectory,"tessdata"),"eng+deu",Tesseract.EngineMode.LstmOnly);
        using var pix=Tesseract.Pix.LoadFromMemory(png);using var page=tess.Process(pix);
        string text=page.GetText();return new("tesseract",text,ArtifactMatcher.Find(text),null);
    }
}
