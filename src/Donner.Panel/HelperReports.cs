using System.Text;
namespace Donner.Panel;
internal static class HelperReports
{
    internal static bool PlainPath(string path)
    {
        var full=Path.GetFullPath(path);
        if(File.Exists(full)&&(File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)return false;
        for(var parent=Directory.GetParent(full);parent is not null;parent=parent.Parent)
            if((parent.Attributes&FileAttributes.ReparsePoint)!=0)return false;
        return true;
    }
    internal static bool WriteNew(string path,string message)
    {
        try
        {
            if(!PlainPath(path))return false;
            using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough);
            using var writer=new StreamWriter(file,new UTF8Encoding(false));writer.Write(message);return true;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException){return false;}
    }
}
