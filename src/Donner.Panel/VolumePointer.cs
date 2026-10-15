using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Donner.Panel;

internal static class VolumePointer
{
    public static void Attach(Slider slider)
    {
        slider.IsMoveToPointEnabled=true;
        slider.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            if(!slider.IsEnabled)return;
            slider.Focus();
            if(slider.CaptureMouse())Move(slider,e.GetPosition(slider));
            e.Handled=true;
        };
        slider.PreviewMouseMove+=(_,e)=>
        {
            if(slider.IsMouseCaptured&&e.LeftButton==MouseButtonState.Pressed)
            {Move(slider,e.GetPosition(slider));e.Handled=true;}
        };
        slider.PreviewMouseLeftButtonUp+=(_,e)=>
        {
            if(!slider.IsMouseCaptured)return;
            Move(slider,e.GetPosition(slider));slider.ReleaseMouseCapture();e.Handled=true;
        };
    }

    internal static void Move(Slider slider,Point position)
    {
        if(slider.Template.FindName("PART_Track",slider) is not Track track)return;
        var point=slider.TranslatePoint(position,track);
        bool vertical=slider.Orientation==Orientation.Vertical;
        var thumb=vertical?track.Thumb.ActualHeight:track.Thumb.ActualWidth;
        var length=(vertical?track.ActualHeight:track.ActualWidth)-thumb;
        if(length<=0)return;
        var ratio=((vertical?point.Y:point.X)-thumb/2)/length;
        if(vertical)ratio=1-ratio;
        if(slider.IsDirectionReversed)ratio=1-ratio;
        var value=slider.Minimum+ratio*(slider.Maximum-slider.Minimum);
        if(double.IsFinite(value))slider.Value=Math.Clamp(Math.Round(value),slider.Minimum,slider.Maximum);
    }
}
