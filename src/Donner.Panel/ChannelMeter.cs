using System.Windows;
using System.Windows.Media;
using Donner.Core;

namespace Donner.Panel;
internal sealed class ChannelMeter : FrameworkElement
{
    public static readonly DependencyProperty PeakProperty=DependencyProperty.Register(nameof(Peak),typeof(double),typeof(ChannelMeter),new FrameworkPropertyMetadata(0d,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Peak {get=>(double)GetValue(PeakProperty);set=>SetValue(PeakProperty,value);}
    private static readonly Brush Off=new SolidColorBrush(Color.FromRgb(48,65,74));
    private static readonly Brush Green=new SolidColorBrush(Color.FromRgb(40,190,128));
    private static readonly Brush Yellow=new SolidColorBrush(Color.FromRgb(242,193,62));
    private static readonly Brush Red=new SolidColorBrush(Color.FromRgb(241,88,82));
    protected override void OnRender(DrawingContext drawing)
    {
        var level=Peak>0?Math.Clamp((AudioPeaks.Db((float)Peak)+60)/60,0,1):0;
        int lit=(int)Math.Ceiling(level*20);double step=(ActualHeight-9)/20;
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(23,42,52)),null,new Rect(0,0,ActualWidth,ActualHeight),2,2);
        drawing.DrawRectangle(Peak>=.999?Red:Off,null,new Rect(2,2,Math.Max(0,ActualWidth-4),3));
        for(int i=0;i<20;i++)
        {
            var brush=i<lit?(i>=19?Red:i>=17?Yellow:Green):Off;
            drawing.DrawRectangle(brush,null,new Rect(2,ActualHeight-(i+1)*step,Math.Max(0,ActualWidth-4),Math.Max(0,step-1.5)));
        }
    }
}
