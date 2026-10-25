using System.Reflection;

namespace VidFlow.Core
{
    // Public identity shown in About and used for links. About points at ProjectUrl for the source
    // code (GPL "corresponding source"); Repo is the default for Check for Updates.
    public static class AppInfo
    {
        public const string Name = "VidFlow Player";
        public const string ShortName = "VidFlow";
        public const string Repo = "neil9bailey/vidflow";
        public const string ProjectUrl = "https://github.com/" + Repo;

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
