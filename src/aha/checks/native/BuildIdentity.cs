using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Aha.Checks;
public static class BuildIdentity
{
    public static string ModuleId(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString();
    }
}
