using System.Runtime.InteropServices;

namespace GptAccountManager.Infrastructure;

internal static class WindowsDwm
{
    private const int WindowCornerPreferenceAttribute = 33;
    private const int BorderColorAttribute = 34;
    private const uint RoundCornerPreference = 2;
    private const uint NoBorderColor = 0xFFFFFFFE;

    public static bool TryApplyDefaultRoundedCorners(IntPtr windowHandle)
    {
        if (!CanStyleWindow(windowHandle))
        {
            return false;
        }

        var preference = RoundCornerPreference;
        return DwmSetWindowAttribute(
                   windowHandle,
                   WindowCornerPreferenceAttribute,
                   ref preference,
                   sizeof(uint)) == 0;
    }

    public static void RemoveSystemBorder(IntPtr windowHandle)
    {
        if (!CanStyleWindow(windowHandle))
        {
            return;
        }

        var borderColor = NoBorderColor;
        _ = DwmSetWindowAttribute(
            windowHandle,
            BorderColorAttribute,
            ref borderColor,
            sizeof(uint));
    }

    public static bool TrySetSystemBorderColor(
        IntPtr windowHandle,
        byte red,
        byte green,
        byte blue)
    {
        if (!CanStyleWindow(windowHandle))
        {
            return false;
        }

        var borderColor = (uint)(red | (green << 8) | (blue << 16));
        return DwmSetWindowAttribute(
                   windowHandle,
                   BorderColorAttribute,
                   ref borderColor,
                   sizeof(uint)) == 0;
    }

    private static bool CanStyleWindow(IntPtr windowHandle) =>
        windowHandle != IntPtr.Zero &&
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref uint value,
        int valueSize);
}
