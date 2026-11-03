using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Donner.Core;

namespace Donner.Panel;

internal sealed class EqGraph:FrameworkElement
{
    internal static readonly Color[] Colors=[Color.FromRgb(83,218,150),Color.FromRgb(60,201,230),Color.FromRgb(111,149,255),Color.FromRgb(206,104,241),Color.FromRgb(246,172,66),Color.FromRgb(237,105,105)];
    internal MicrophoneEqSettings Settings {get;set;}=MicrophoneEqSettings.Flat();
    internal int SampleRate {get;set;}=48000;
    internal int Selected {get;set;}=0;
    internal Action<int>? SelectionChanged;
    internal Action<int,EqBand>? BandChanged;
    private bool dragging;
    private readonly double[] spectrum=new double[1024];
    private readonly System.Numerics.Complex[] fft=new System.Numerics.Complex[2048];
    private bool hasSpectrum;
    internal Rect Plot=>new(42,18,Math.Max(1,ActualWidth-88),Math.Max(1,ActualHeight-52));
    internal double FrequencyX(double hz)=>Plot.Left+Math.Log10(Math.Clamp(hz,20,20000)/20)/3*Plot.Width;
    internal double XFrequency(double x)=>20*Math.Pow(1000,Math.Clamp((x-Plot.Left)/Plot.Width,0,1));
    private double GainY(double db)=>Plot.Top+(24-Math.Clamp(db,-24,24))/48*Plot.Height;
    private double YGain(double y)=>Math.Clamp(24-(y-Plot.Top)/Plot.Height*48,-24,24);
    private static double NodeGain(EqBand b)=>b.Type==EqFilter.Notch?-18:b.Type is EqFilter.HighPass or EqFilter.LowPass?0:b.Gain;
    internal EqGraph(){ClipToBounds=true;Focusable=true;Cursor=Cursors.Cross;MinHeight=210;}
    private void Label(DrawingContext dc,string text,Point at,Brush? brush=null,double size=11)
    {dc.DrawText(new FormattedText(text,CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,brush??new SolidColorBrush(Color.FromRgb(149,158,174)),VisualTreeHelper.GetDpi(this).PixelsPerDip),at);}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(30,32,40)),null,new Rect(RenderSize),8,8);var rect=Plot;
        var grid=new Pen(new SolidColorBrush(Color.FromRgb(56,60,71)),1);
        foreach(int hz in new[]{20,30,50,100,200,500,1000,2000,5000,10000,20000})
        {double x=FrequencyX(hz);dc.DrawLine(grid,new(x,rect.Top),new(x,rect.Bottom));if(hz!=30)Label(dc,hz>=1000?$"{hz/1000}k":hz.ToString(),new(x-10,rect.Bottom+8));}
        foreach(int db in new[]{-24,-18,-12,-6,0,6,12,18,24})
        {double y=GainY(db);dc.DrawLine(db==0?new Pen(new SolidColorBrush(Color.FromRgb(100,113,126)),1):grid,new(rect.Left,y),new(rect.Right,y));Label(dc,db>0?$"+{db}":db.ToString(),new(rect.Right+6,y-7));}
        Label(dc,"Hz",new(rect.Left-28,rect.Bottom+8));Label(dc,"dB",new(rect.Right+4,0));
        dc.PushClip(new RectangleGeometry(rect));
        if(hasSpectrum)
        {
            var geometry=new StreamGeometry();using(var ctx=geometry.Open())
            {
                ctx.BeginFigure(new(rect.Left,rect.Bottom),true,true);
                for(int bin=1;bin<1024;bin++)
                {
                    double hz=(double)bin*SampleRate/2048;if(hz<20)continue;if(hz>20000)break;
                    double y=rect.Bottom-Math.Clamp((spectrum[bin]+90)/90,0,1)*rect.Height;
                    ctx.LineTo(new(FrequencyX(hz),y),true,false);
                }
                ctx.LineTo(new(rect.Right,rect.Bottom),true,false);
            }
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(58,190,199,214)),new Pen(new SolidColorBrush(Color.FromArgb(120,180,190,207)),1),geometry);
        }
        var coefficients=Settings.Bands.Select(b=>EqCoefficients.Create(b,SampleRate)).ToArray();
        for(int band=0;band<6;band++)
        {
            if(!Settings.Bands[band].Enabled)continue;
            DrawCurve(dc,hz=>coefficients[band].Decibels(hz,SampleRate),new Pen(new SolidColorBrush(Color.FromArgb(125,Colors[band].R,Colors[band].G,Colors[band].B)),1));
        }
        DrawCurve(dc,hz=>Settings.Preamp+coefficients.Sum(c=>c.Decibels(hz,SampleRate)),new Pen(new SolidColorBrush(Color.FromRgb(250,210,72)),2.2));
        dc.Pop();
        for(int i=0;i<6;i++)
        {
            var band=Settings.Bands[i];var point=new Point(FrequencyX(band.Frequency),GainY(NodeGain(band)));
            dc.DrawEllipse(band.Enabled?new SolidColorBrush(Colors[i]):new SolidColorBrush(Color.FromRgb(80,83,94)),new Pen(i==Selected?Brushes.White:Brushes.Transparent,i==Selected?2:0),point,i==Selected?7:5,i==Selected?7:5);
        }
    }
    private void DrawCurve(DrawingContext dc,Func<double,double> response,Pen pen)
    {
        var line=new StreamGeometry();using(var ctx=line.Open())for(int i=0;i<=420;i++)
        {double hz=20*Math.Pow(1000,i/420d);var point=new Point(FrequencyX(hz),GainY(response(hz)));if(i==0)ctx.BeginFigure(point,false,false);else ctx.LineTo(point,true,false);}
        dc.DrawGeometry(null,pen,line);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);Focus();var p=e.GetPosition(this);double closest=20;int found=-1;
        for(int i=0;i<6;i++){var b=Settings.Bands[i];double distance=(p-new Point(FrequencyX(b.Frequency),GainY(NodeGain(b)))).Length;if(distance<closest){closest=distance;found=i;}}
        if(found<0)return;Selected=found;SelectionChanged?.Invoke(found);dragging=CaptureMouse();InvalidateVisual();e.Handled=true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);if(!dragging)return;var p=e.GetPosition(this);var band=Settings.Bands[Selected];
        BandChanged?.Invoke(Selected,band with{Frequency=Math.Round(XFrequency(p.X)),Gain=band.Type is EqFilter.Bell or EqFilter.LowShelf or EqFilter.HighShelf?Math.Round(YGain(p.Y),1):band.Gain});
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e){base.OnMouseLeftButtonUp(e);dragging=false;ReleaseMouseCapture();}
    protected override void OnLostMouseCapture(MouseEventArgs e){dragging=false;base.OnLostMouseCapture(e);}
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {base.OnMouseWheel(e);var b=Settings.Bands[Selected];BandChanged?.Invoke(Selected,b with{Q=Math.Round(Math.Clamp(b.Q*Math.Pow(1.12,e.Delta/120d),.1,20),2)});e.Handled=true;}
    internal void UpdateSpectrum(ReadOnlySpan<float> samples,bool available)
    {
        if(!available){hasSpectrum=false;InvalidateVisual();return;}
        for(int i=0;i<2048;i++)fft[i]=new(samples[i]*(.5-.5*Math.Cos(2*Math.PI*i/2047)),0);
        for(int i=1,j=0;i<2048;i++){int bit=1024;for(; (j&bit)!=0;bit>>=1)j^=bit;j^=bit;if(i<j)(fft[i],fft[j])=(fft[j],fft[i]);}
        for(int size=2;size<=2048;size*=2)
        {
            var root=System.Numerics.Complex.FromPolarCoordinates(1,-2*Math.PI/size);
            for(int i=0;i<2048;i+=size){var w=System.Numerics.Complex.One;for(int j=0;j<size/2;j++){var a=fft[i+j];var b=fft[i+j+size/2]*w;fft[i+j]=a+b;fft[i+j+size/2]=a-b;w*=root;}}
        }
        for(int i=1;i<1024;i++){double db=20*Math.Log10(Math.Max(fft[i].Magnitude/512,1e-6));spectrum[i]=hasSpectrum?.6*spectrum[i]+.4*db:db;}
        hasSpectrum=true;InvalidateVisual();
    }
}
