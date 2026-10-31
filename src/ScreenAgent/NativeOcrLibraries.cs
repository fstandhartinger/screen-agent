using System.Reflection;
using System.Security.Cryptography;
namespace ScreenAgent;
internal static class NativeOcrLibraries
{
    static readonly object gate=new();
    static bool ready;
    public static void Ensure()
    {
        lock(gate)
        {
            if(ready)return;
            var assembly=Assembly.GetExecutingAssembly();
            string root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ScreenAgent","native","tesseract-5.2.0");
            string x64=Path.Combine(root,"x64");Directory.CreateDirectory(x64);
            foreach(string name in new[]{"leptonica-1.82.0.dll","tesseract50.dll"})
            {
                using var resource=assembly.GetManifestResourceStream("ScreenAgent.Native."+name)??throw new InvalidOperationException("Missing bundled OCR library "+name);
                using var memory=new MemoryStream();resource.CopyTo(memory);byte[] bytes=memory.ToArray();
                string path=Path.Combine(x64,name);
                // Reuse only exact embedded bytes; recover interrupted extraction atomically.
                if(!File.Exists(path)||!SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(SHA256.HashData(bytes)))
                {
                    string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
                    try{File.WriteAllBytes(temporary,bytes);File.Move(temporary,path,true);}
                    finally{if(File.Exists(temporary))File.Delete(temporary);}
                }
            }
            Tesseract.TesseractEnviornment.CustomSearchPath=root;
            ready=true;
        }
    }
}
