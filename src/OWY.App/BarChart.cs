using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using OWY.Core;

namespace OWY.App;

// A reusable readout follows the selected bar immediately. A refresh replaces
// data, not the pointer target or the hover control.
public sealed class BarChart : Grid
{
    readonly Canvas plot=new(){Height=112,Background=new SolidColorBrush(Microsoft.UI.Colors.Transparent)};
    readonly TextBlock readout=new(){FontSize=12,Opacity=.8,MinHeight=20,Text=" "};
    readonly List<Rectangle> shapes=[];
    readonly Rectangle cursor=new(){Width=1,Fill=new SolidColorBrush(Microsoft.UI.Colors.White),IsHitTestVisible=false};
    ChartBin[] bins=[];bool tokenChart;int selected=-1;double pointerX;
    public int DesiredCount=>Math.Clamp((int)Math.Floor(Math.Max(360,plot.ActualWidth)/3),120,600);
    public int HoverIndex=>selected;
    public long UpdateCount {get;private set;}
    public string Readout=>readout.Text;
    public BarChart(bool tokens)
    {
        tokenChart=tokens;
        RowDefinitions.Add(new(){Height=GridLength.Auto});RowDefinitions.Add(new(){Height=GridLength.Auto});
        Children.Add(plot);SetRow(readout,1);Children.Add(readout);
        plot.PointerMoved+=Moved;plot.PointerExited+=(_,_)=>{selected=-1;readout.Text=" ";cursor.Visibility=Visibility.Collapsed;};
        plot.SizeChanged+=(_,_)=>Draw();
        ActualThemeChanged+=(_,_)=>Draw();
    }
    SolidColorBrush Accent=>new((Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
    public void Update(ChartBin[] values){UpdateCount++;bins=values;Draw();if(selected>=0)Hover(pointerX);}
    void Moved(object sender,PointerRoutedEventArgs e)=>Hover(e.GetCurrentPoint(plot).Position.X);
    void Hover(double x)
    {
        if(bins.Length==0 || plot.ActualWidth<=0)return;pointerX=x;
        selected=Math.Clamp((int)(x/plot.ActualWidth*bins.Length),0,bins.Length-1);
        var b=bins[selected];string time=DateTimeOffset.FromUnixTimeSeconds(b.Start).ToLocalTime().ToString("MMM d HH:mm:ss");
        string value=tokenChart?(b.ReceivedTps.HasValue?Display.Tokens(b.OutputTokens)+" received":"— received"):Display.Rate(b.ReceivedTps);
        readout.Text=$"{time}   "+value+
            $" · {b.End-b.Start}s"+(b.ReceiptCoverage<1?" · partial":"");
        cursor.Height=plot.Height;cursor.Visibility=Visibility.Visible;Canvas.SetLeft(cursor,selected*plot.ActualWidth/bins.Length);
    }
    void Draw()
    {
        if(bins.Length==0 || plot.ActualWidth<=0)return;
        while(shapes.Count<bins.Length)
        {
            var bar=new Rectangle{IsHitTestVisible=false};
            plot.Children.Add(bar);shapes.Add(bar);
        }
        if(!plot.Children.Contains(cursor))plot.Children.Add(cursor);
        Canvas.SetZIndex(cursor,2);
        double width=plot.ActualWidth/bins.Length;
        double max=Math.Max(1,bins.Max(b=>tokenChart?b.OutputTokens:b.ReceivedTps??0));
        var accent=Accent;
        for(int i=0;i<shapes.Count;i++)
        {
            var bar=shapes[i];bar.Visibility=i<bins.Length?Visibility.Visible:Visibility.Collapsed;
            if(i>=bins.Length)continue;var b=bins[i];
            bool observed=b.ReceivedTps.HasValue;
            double value=tokenChart?b.OutputTokens:b.ReceivedTps??0;
            bar.Width=width;bar.Height=observed?Math.Max(value>0?1:0,value/max*(plot.Height-2)):0;
            bar.Fill=accent;bar.Opacity=observed?1:.2;
            Canvas.SetLeft(bar,i*width);Canvas.SetTop(bar,plot.Height-bar.Height);
        }
        cursor.Fill=new SolidColorBrush(ActualTheme==ElementTheme.Dark?Microsoft.UI.Colors.White:Microsoft.UI.Colors.Black);
        if(selected<0)cursor.Visibility=Visibility.Collapsed;
    }
}
