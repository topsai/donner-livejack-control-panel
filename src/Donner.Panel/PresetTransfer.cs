using System.Windows;
using Donner.Core;
using Microsoft.Win32;
namespace Donner.Panel;
public partial class MainWindow
{
    private void ExportPresetClick(object sender,RoutedEventArgs e)
    {
        if(PresetsList.SelectedItem is not Preset preset){Record("请先选择要导出的预设。");return;}
        var dialog=new SaveFileDialog{Title="导出完整预设",Filter="Donner 配置预设|*.json",FileName=preset.Name+".json"};
        if(dialog.ShowDialog(this)!=true)return;
        try{PresetStore.ExportFile(dialog.FileName,preset);Record("已导出完整预设："+preset.Name);}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException){Record(error.Message);}
    }
    private void ImportPresetClick(object sender,RoutedEventArgs e)
    {
        var dialog=new OpenFileDialog{Title="导入配置预设",Filter="Donner 配置预设|*.json"};if(dialog.ShowDialog(this)!=true)return;
        try
        {
            var imported=PresetStore.ImportFile(dialog.FileName);var existing=presets.ReadAvailable(out _).Select(x=>x.Name).ToHashSet();string name=imported.Name;
            for(int n=1;existing.Contains(name);n++)name=imported.Name[..Math.Min(imported.Name.Length,48)]+$"（导入{n}）";
            imported=imported with{Name=name};presets.Save(imported);LoadPresets();PresetsList.SelectedItem=PresetsList.Items.Cast<Preset>().Single(x=>x.Name==name);Record("已导入，请点击应用所选预设："+name);
        }
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException){Record(error.Message);}
    }
    private async Task<ApplicationConfiguration> CaptureApplicationPreset()
    {
        foreach(var channel in channelViews)if(channel.Value.SelectedPercent is {} percent)panelState.Percents[PercentKey(channel.Key.Item1,channel.Key.Item2)]=percent;
        var recorded=panelState.Names.ToDictionary(x=>x.Id);foreach(var name in await Task.Run(()=>MixerEndpointNames.CaptureNames(storage)))recorded[name.Id]=name;var names=recorded.Values.ToArray();
        return new(MeterCheck.IsChecked==true,startup.Enabled,new(panelState.Percents),names);
    }
    private async Task RestoreApplicationPreset(ApplicationConfiguration? configuration)
    {
        if(configuration is null)return;configuration.Validate();MeterCheck.IsChecked=configuration.Levels;
        startup.Set(configuration.Startup,Environment.ProcessPath!);StartupCheck.IsChecked=configuration.Startup;
        panelState=panelState with{Percents=new(configuration.Percents),Names=configuration.Names};
        foreach(var channel in channelViews)channel.Value.SelectedPercent=null;
        await RestoreSessionNames();
    }
    private readonly System.Windows.Threading.DispatcherTimer nameRestoreTimer=new(){Interval=TimeSpan.FromMilliseconds(1500)};
    private void ScheduleNamesRestore(){nameRestoreTimer.Stop();nameRestoreTimer.Start();}
    private bool restoringNames;
    private async Task RestoreSessionNames()
    {
        if(restoringNames||panelState.Names.Length==0||smokeDirectory is not null)return;restoringNames=true;
        try{Record(await MixerEndpointNames.RestoreNamesAsync(storage,panelState.Names));mixerPanel?.RefreshDevices();}
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){Record(error.Message);}
        finally{restoringNames=false;}
    }
}
