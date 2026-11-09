using System.Diagnostics;
namespace ScreenAgent;
internal sealed class DesktopInput(FreeHost host)
{
    public void Wait(int ms){var sw=Stopwatch.StartNew();while(sw.ElapsedMilliseconds<ms){host.ThrowIfStopped();Thread.Sleep((int)Math.Min(20,ms-sw.ElapsedMilliseconds));}host.ThrowIfStopped();}
    public void ModifiersUp(){var sw=Stopwatch.StartNew();while(!UiaDriver.WaitModifiersUp(0)){host.ThrowIfStopped();if(sw.ElapsedMilliseconds>=500)throw new InvalidOperationException("Bitte die gehaltenen Modifikatortasten loslassen.");Thread.Sleep(10);}host.ThrowIfStopped();}
    static Native.Input Scan(ushort vk,bool up=false,bool extended=false)
    {
        uint scan=DesktopNative.MapVirtualKey(vk,0);
        return new(){type=1,u=new(){keyboard=new(){scan=(ushort)scan,vk=scan==0?vk:(ushort)0,flags=(scan==0?0u:8u)|(up?2u:0u)|(extended||vk is 91 or 92?1u:0u),extra=Native.Marker}}};
    }
    public void WithMods(ushort[] mods,Action act)
    {
        ModifiersUp();int held=0;
        try{foreach(var m in mods){host.ThrowIfStopped();Native.Send(Scan(m));held++;}act();}
        finally{for(int i=held-1;i>=0;i--)Native.Send(Scan(mods[i],true));}
    }
    public void Keys(string keys,int times=1)
    {
        var chords=DesktopKeys.Parse(keys);
        for(int i=0;i<times;i++)foreach(var c in chords)
        {
            host.ThrowIfStopped();WithMods(c.Modifiers,()=>{host.ThrowIfStopped();try{Native.Send(Scan(c.Key,false,c.Extended));}finally{Native.Send(Scan(c.Key,true,c.Extended));}});Wait(3);
        }
    }
    public void Type(string text,bool paste,bool enter)
    {
        ModifiersUp();host.ThrowIfStopped();
        if(paste)
        {
            string old=ClipboardText.Read();
            try{ClipboardText.Write(text);Keys("ctrl+v");Wait(150);}finally{ClipboardText.Write(old);}
        }
        else
        {
            for(int i=0;i<text.Length;i++)
            {
                host.ThrowIfStopped();char c=text[i];
                if(c=='\r'){if(i+1<text.Length&&text[i+1]=='\n')continue;Keys("enter");}
                else if(c=='\n')Keys("enter");else if(c=='\t')Keys("tab");
                else
                {
                    var batch=new List<Native.Input>{Native.Key(0,false,c,true,Native.Marker),Native.Key(0,true,c,true,Native.Marker)};
                    if(char.IsHighSurrogate(c)&&i+1<text.Length&&char.IsLowSurrogate(text[i+1])){c=text[++i];batch.Add(Native.Key(0,false,c,true,Native.Marker));batch.Add(Native.Key(0,true,c,true,Native.Marker));}
                    Native.Send(batch.ToArray());Wait(3);
                }
            }
        }
        if(enter)Keys("enter");
    }
    public void Move(int x,int y)
    {
        host.ThrowIfStopped();var b=DesktopScreens.VirtualBounds();
        Native.Send(new Native.Input{type=0,u=new(){mouse=new(){dx=PixelFrame.Absolute(x,b.X,b.Width),dy=PixelFrame.Absolute(y,b.Y,b.Height),flags=0x8000|0x4000|1,extra=Native.Marker}}});
        DesktopNative.GetCursorPos(out var at);if(at.X!=x||at.Y!=y)Native.SetCursorPos(x,y);
    }
    static uint ButtonFlag(string button,bool down)=>button switch {"left"=>down?2u:4u,"right"=>down?8u:16u,"middle"=>down?32u:64u,_=>throw new ArgumentException("button: left, right oder middle")};
    static void Button(string button,bool down)=>Native.Send(new Native.Input{type=0,u=new(){mouse=new(){flags=ButtonFlag(button,down),extra=Native.Marker}}});
    public void Click(int x,int y,string button,int count,string mods="")
    {
        _=ButtonFlag(button,true);WithMods(DesktopKeys.Mods(mods),()=>{Move(x,y);for(int i=0;i<count;i++){host.ThrowIfStopped();try{Button(button,true);Wait(20);}finally{Button(button,false);}if(i+1<count)Wait(60);}});
    }
    public void Drag(int x,int y,int x2,int y2,string button,int ms)
    {
        _=ButtonFlag(button,true);ModifiersUp();Move(x,y);
        try
        {
            Button(button,true);Wait(80);int steps=Math.Max(12,ms/15);var sw=Stopwatch.StartNew();
            for(int i=1;i<=steps;i++){Move((int)Math.Round(x+(x2-x)*(double)i/steps),(int)Math.Round(y+(y2-y)*(double)i/steps));Wait(Math.Max(0,(int)((long)ms*i/steps-sw.ElapsedMilliseconds)));}
            var b=DesktopScreens.VirtualBounds();Move(x2+1<b.Right?x2+1:x2-1,y2);Wait(15);Move(x2,y2);Wait(15);
        }
        finally{Button(button,false);}
    }
    public void Wheel(double dx,double dy)
    {
        host.ThrowIfStopped();if(dy!=0)Native.Send(new Native.Input{type=0,u=new(){mouse=new(){flags=0x800,data=unchecked((uint)(int)Math.Round(-dy*120)),extra=Native.Marker}}});
        host.ThrowIfStopped();if(dx!=0)Native.Send(new Native.Input{type=0,u=new(){mouse=new(){flags=0x1000,data=unchecked((uint)(int)Math.Round(dx*120)),extra=Native.Marker}}});
    }
}
// Clipboard must run on an STA. It is independent of the WPF dispatcher, which stays responsive during actions.
internal static class ClipboardText
{
    static T Sta<T>(Func<T> action)
    {
        T result=default!;Exception? error=null;
        var thread=new Thread(()=>{try{result=action();}catch(Exception e){error=e; /* Propagate on the caller thread below. */ }}){IsBackground=true};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        if(!thread.Join(1500))throw new TimeoutException("Zwischenablage belegt.");if(error is not null)throw error;return result;
    }
    public static string Read()=>Sta(()=>System.Windows.Forms.Clipboard.ContainsText()?System.Windows.Forms.Clipboard.GetText():"");
    public static void Write(string text)=>Sta(()=>{if(text.Length==0)System.Windows.Forms.Clipboard.Clear();else System.Windows.Forms.Clipboard.SetText(text);return true;});
}
