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
        var value=track.ValueFromPoint(slider.TranslatePoint(position,track));
        if(double.IsFinite(value))slider.Value=Math.Clamp(Math.Round(value),slider.Minimum,slider.Maximum);
    }
}
