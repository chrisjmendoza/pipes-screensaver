using Pipes.Native;

namespace Pipes;

/// <summary>
/// Whether the machine is running on its battery, for <see cref="PipesSettings.BatteryQuality"/>.
/// </summary>
/// <remarks>
/// <para>
/// Polled from the render loop rather than hooked up to <c>SystemEvents.PowerModeChanged</c>: that event needs a
/// Windows Forms message pump on the thread, and the screensaver runs its own bare Win32 loop (<see cref="GLHost"/>).
/// <see cref="Win32.GetSystemPowerStatus"/> is a cheap read of state Windows already keeps, but there is no reason to
/// ask more than once every <see cref="PollSeconds"/> seconds: the answer only changes when someone pulls a plug.
/// </para>
/// <para>
/// A desktop answers "on mains" forever, so the battery setting simply never fires there.
/// </para>
/// </remarks>
internal static class PowerSource
{
    /// <summary>
    /// How often to ask. Unplugging should be noticed quickly enough to feel deliberate, but the switch costs a
    /// renderer rebuild (new shaders), so this is not something to check every frame.
    /// </summary>
    public const double PollSeconds = 2.0;

    /// <summary>
    /// True only when the machine has a battery and is running on it. Anything unknown or unreadable counts as
    /// mains: the setting is a power saving, and guessing "battery" wrong would quietly downgrade a desktop.
    /// </summary>
    public static bool OnBattery()
    {
        if (!Win32.GetSystemPowerStatus(out var status)) return false;
        if ((status.BatteryFlag & Win32.BATTERY_FLAG_NO_BATTERY) != 0) return false;
        return status.ACLineStatus == Win32.AC_LINE_OFFLINE;
    }
}
