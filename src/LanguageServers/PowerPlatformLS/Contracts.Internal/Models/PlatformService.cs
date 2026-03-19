using System.Runtime.InteropServices;

public static class PlatformService
{
    public static Func<bool> IsUnixPlatform = () => RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    
    public static bool IsUnix() => IsUnixPlatform();
}
