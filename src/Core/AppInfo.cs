using System.Reflection;

namespace VidFlow.Core
{
    // Public identity shown in About and used for links. ProjectUrl stays empty until the public
    // repository exists; About then points at it for the source code (GPL "corresponding source").
    public static class AppInfo
    {
        public const string Name = "VidFlow Player";
        public const string ShortName = "VidFlow";
        public const string ProjectUrl = "";

        // "1.0.0-beta.3": the informational version build.ps1 stamps from VERSION or the release tag.
        public static string Version
        {
            get
            {
                var asm = typeof(AppInfo).Assembly;
                var info = (AssemblyInformationalVersionAttribute)System.Attribute.GetCustomAttribute(asm, typeof(AssemblyInformationalVersionAttribute));
                if (info != null && !string.IsNullOrEmpty(info.InformationalVersion)) return info.InformationalVersion;
                var v = asm.GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }
    }
}
