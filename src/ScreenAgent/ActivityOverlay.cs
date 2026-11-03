using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
namespace ScreenAgent;
// What the agent is doing right now: up to four small dark rows above the status note (bottom right), one per tool call or local step. Click-through, never takes
// focus, left out of every screen capture. A message shows up at once (Present renders before it returns); the 100 ms tick of the app only advances ticks and fades.
internal sealed class ActivityOverlay : Window
{
    sealed class Row
    {
        public readonly Border Box=new(){CornerRadius=new CornerRadius(8),Padding=new Thickness(10,4,12,4),Margin=new Thickness(0,3,0,0),MaxWidth=420,Visibility=Visibility.Collapsed};
        public readonly TextBlock Mark=new(){FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=12,Width=14,VerticalAlignment=VerticalAlignment.Center,TextAlignment=TextAlignment.Center};
        public readonly TextBlock Label=new(){Foreground=Brushes.White,FontFamily=new System.Windows.Media.FontFamily("Segoe UI"),FontSize=12,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
        public Row()
        {
            var line=new StackPanel{Orientation=Orientation.Horizontal};line.Children.Add(Mark);line.Children.Add(Label);Label.Margin=new Thickness(6,0,0,0);
            Box.Child=line;
        }
    }
    static readonly System.Windows.Media.Color Dark=System.Windows.Media.Color.FromArgb(225,34,34,40),Orange=System.Windows.Media.Color.FromRgb(255,150,60),Green=System.Windows.Media.Color.FromRgb(61,220,132),Purple=System.Windows.Media.Color.FromRgb(168,120,255);
    static SolidColorBrush Frozen(System.Windows.Media.Color c,double opacity=1){var b=new SolidColorBrush(c){Opacity=opacity};b.Freeze();return b;}
    static readonly SolidColorBrush DarkBrush=Frozen(Dark),GreenBrush=Frozen(Green),OrangeBrush=Frozen(Orange),PurpleFull=Frozen(Purple),PurpleDim=Frozen(Purple,0.35);
    readonly ActivityBoard board=new();
    readonly Row[] rows;
    readonly Func<int> reserve;
    readonly bool requireExclusion;
    (int,int,int) placed;
    public bool CaptureExcluded {get;private set;}
    // reserve: pixels held free above the bottom edge (the status note stands there).
    public ActivityOverlay(Func<int> reserve,bool excludeFromCapture=true)
    {
        this.reserve=reserve;requireExclusion=excludeFromCapture;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=false;ShowActivated=false;Focusable=false;SizeToContent=SizeToContent.WidthAndHeight;
        rows=Enumerable.Range(0,board.MaxRows).Select(_=>new Row()).ToArray();
        var stack=new StackPanel{Orientation=Orientation.Vertical,HorizontalAlignment=HorizontalAlignment.Right};
        foreach(var r in rows)stack.Children.Add(r.Box);
        Content=stack;
        SizeChanged+=(_,_)=>Place();
        new WindowInteropHelper(this).EnsureHandle();
        CaptureExcluded=OverlayStyle.Apply(this,true,excludeFromCapture);
    }
    // The app's own steps come in through the same door as the page's messages. true: a row appeared that was not there before.
    public bool Present(ActivityNote e)
    {
        bool added=board.Apply(e,Environment.TickCount64);
        Render();return added;
    }
    public void Tick(){if(IsVisible||board.Any(Environment.TickCount64))Render();}
    public void Clear(){board.Clear();Render();}
    // What is on screen, for the selftest: one text per visible row.
    public string[] Texts=>rows.Where(r=>r.Box.Visibility==Visibility.Visible).Select(r=>r.Label.Text).ToArray();
    public string[] Marks=>rows.Where(r=>r.Box.Visibility==Visibility.Visible).Select(r=>r.Mark.Text).ToArray();
    void Render()
    {
        long now=Environment.TickCount64;var view=board.View(now);
        if(requireExclusion&&!CaptureExcluded){if(view.Count>0)OverlayStyle.RefusedOnce("activity");if(IsVisible)Hide();return;}   // not shown when a screenshot could catch it
        for(int i=0;i<rows.Length;i++)
        {
            var row=rows[i];
            if(i>=view.Count){row.Box.Visibility=Visibility.Collapsed;continue;}
            var v=view[i];
            row.Label.Text=v.Text;row.Box.Opacity=v.Opacity;row.Box.Visibility=Visibility.Visible;
            row.Box.Background=DarkBrush;
            switch(v.Kind)
            {
                case RowKind.Done:row.Mark.Text="✓";row.Mark.Foreground=GreenBrush;row.Label.Foreground=Brushes.White;break;
                case RowKind.Fail:row.Mark.Text="!";row.Mark.Foreground=OrangeBrush;row.Label.Foreground=OrangeBrush;break;
                default:   // a dot that breathes: the only motion, and it never delays a row
                    row.Mark.Text="●";row.Mark.Foreground=(now/350)%2==0?PurpleFull:PurpleDim;row.Label.Foreground=Brushes.White;break;
            }
        }
        if(view.Count==0){if(IsVisible)Hide();return;}
        if(!IsVisible)Show();
        Place();
    }
    void Place()
    {
        var hwnd=new WindowInteropHelper(this).EnsureHandle();
        var screen=System.Windows.Forms.Screen.PrimaryScreen??System.Windows.Forms.Screen.AllScreens[0];
        var dpi=VisualTreeHelper.GetDpi(this);
        int w=(int)Math.Ceiling(ActualWidth*dpi.DpiScaleX),h=(int)Math.Ceiling(ActualHeight*dpi.DpiScaleY);
        if(w<=0||h<=0)return;
        int res=reserve();var key=(w,h,res);
        if(key==placed)return;   // the 100 ms tick re-renders; the window only moves when its size or the reserved height changed
        placed=key;
        var work=StatusToast.Px(screen.WorkingArea);
        work=new(work.X,work.Y,work.W,Math.Max(h,work.H-res));
        var box=WindowPlacement.BottomRight(work,w,h,(int)Math.Round(14*dpi.DpiScaleX));
        Native.SetWindowPos(hwnd,IntPtr.Zero,box.X,box.Y,0,0,0x1|0x4|0x10);   // NOSIZE | NOZORDER | NOACTIVATE
    }
}
