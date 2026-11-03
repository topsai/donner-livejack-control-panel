using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace Donner.Panel;

internal sealed class MixerSetupWindow:Window
{
    internal const string OfficialDownload="https://download.vb-audio.com/Download_CABLE/VoicemeeterSetup_v2130.zip";
    private readonly TextBlock status=new(){Margin=new Thickness(0,16,0,12)};
    private readonly Button install=new(){Content="下载并安装 Banana"};
    private readonly CancellationTokenSource cancellation=new();
    public MixerSetupWindow()
    {
        Title="启用调音台 · 安装引导";Width=560;Height=410;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var body=new StackPanel{Margin=new Thickness(24)};Content=body;
        body.Children.Add(new TextBlock{Text="安装 Voicemeeter Banana",FontSize=21,FontWeight=FontWeights.SemiBold});
        body.Children.Add(new TextBlock{Text="调音台由 VB-Audio 的 Banana 提供混音、音效和虚拟音频设备，需要单独安装。",Margin=new Thickness(0,16,0,12)});
        body.Children.Add(new TextBlock{Text="1. 下载官方安装包，按原厂向导选择 Install。\n2. 安装虚拟驱动需要管理员授权。\n3. 安装后重启 Windows，再打开 Donner 面板。",LineHeight=24});
        body.Children.Add(new TextBlock{Text="Banana 采用 Donationware 授权；个人可先评估，专业用途须按官方条款授权。来源：vb-audio.com。",Margin=new Thickness(0,12,0,0),FontSize=12});
        body.Children.Add(status);var actions=new WrapPanel();body.Children.Add(actions);actions.Children.Add(install);
        var check=new Button{Content="已安装，继续"};actions.Children.Add(check);
        var cancel=new Button{Content="取消",IsCancel=true};actions.Children.Add(cancel);
        install.Click+=async(_,_)=>await InstallAsync();
        check.Click+=(_,_)=>{if(VoicemeeterClient.InstalledDirectory() is not null)DialogResult=true;else status.Text="尚未检测到 Banana，请完成安装；刚安装时可能需要重启 Windows。";};
        Closed+=(_,_)=>cancellation.Cancel();
    }
    private async Task InstallAsync()
    {
        install.IsEnabled=false;status.Text="正在从 VB-Audio 官方网站下载…";
        try
        {
            var dir=Path.Combine(Path.GetTempPath(),"Donner-Banana-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            var archive=Path.Combine(dir,"VoicemeeterSetup.zip");
            using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(4)};
            using(var response=await http.GetAsync(OfficialDownload,HttpCompletionOption.ResponseHeadersRead,cancellation.Token))
            {
                response.EnsureSuccessStatusCode();var final=response.RequestMessage!.RequestUri!;
                if(final.Scheme!="https"||!final.Host.Equals("download.vb-audio.com",StringComparison.OrdinalIgnoreCase))throw new IOException("官方下载地址发生变化，请从官网安装。");
                await using var input=await response.Content.ReadAsStreamAsync(cancellation.Token);await using var output=File.Create(archive);
                byte[] buffer=new byte[65536];long total=0;int n;
                while((n=await input.ReadAsync(buffer,cancellation.Token))>0)
                {total+=n;if(total>140*1024*1024)throw new IOException("安装包大小异常。");await output.WriteAsync(buffer.AsMemory(0,n),cancellation.Token);status.Text=$"正在下载… {total/1024/1024} MB";}
            }
            string exe=Path.Combine(dir,"VoicemeeterProSetup.exe");
            using(var zip=ZipFile.OpenRead(archive))
            {
                var entry=zip.Entries.SingleOrDefault(e=>e.FullName.Equals("voicemeeterprosetup.exe",StringComparison.OrdinalIgnoreCase))??throw new IOException("安装包内容已变化，请从官网安装。");
                if(entry.Length>160*1024*1024)throw new IOException("安装程序大小异常。");entry.ExtractToFile(exe);
            }
            status.Text="正在验证原厂数字签名…";await VerifyInstaller(exe,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Process.Start(new ProcessStartInfo(exe){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Normal});
            DialogResult=true;
        }
        catch(OperationCanceledException){if(IsVisible)status.Text="下载已取消。";}
        catch(Exception error) when(error is HttpRequestException or InvalidDataException or IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or JsonException)
        {if(IsVisible)status.Text="安装未开始："+error.Message;}
        finally{if(IsVisible)install.IsEnabled=true;}
    }
    internal static async Task VerifyInstaller(string exe,CancellationToken cancellation)
    {
        var psHome=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0");
        var start=new ProcessStartInfo(Path.Combine(psHome,"powershell.exe")){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        start.Environment["PSModulePath"]=Path.Combine(psHome,"Modules");
        start.ArgumentList.Add("-NoProfile");start.ArgumentList.Add("-NonInteractive");start.ArgumentList.Add("-Command");
        start.ArgumentList.Add("$ErrorActionPreference='Stop'; $s=Get-AuthenticodeSignature -LiteralPath '"+exe.Replace("'","''")+"'; @{Status=[int]$s.Status; Subject=$s.SignerCertificate.Subject} | ConvertTo-Json -Compress");
        using var process=Process.Start(start)??throw new IOException("无法验证安装程序。");
        var output=process.StandardOutput.ReadToEndAsync(cancellation);var errors=process.StandardError.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);var json=await output;var stderr=await errors;
        if(process.ExitCode!=0||string.IsNullOrWhiteSpace(json))throw new IOException("系统签名验证不可用，未运行安装包。"+stderr);
        using var parsed=JsonDocument.Parse(json);var root=parsed.RootElement;
        var subject=root.GetProperty("Subject").GetString()??"";
        if(process.ExitCode!=0||root.GetProperty("Status").GetInt32()!=0||
           !(subject.Contains("Vincent Burel",StringComparison.OrdinalIgnoreCase)||subject.Contains("BUREL VINCENT",StringComparison.OrdinalIgnoreCase)||subject.Contains("VB-Audio",StringComparison.OrdinalIgnoreCase)))
            throw new IOException("安装程序的 VB-Audio 有效签名验证失败，未运行该文件。");
    }
}
