namespace ClassBanner.Core;

/// <summary>What goes on the left and right of each banner: the computer name, user name and clock, where configured.</summary>
public static class BannerCorners
{
    public const string Separator = "   |   ";

    /// <summary>
    /// The left and right text for the banner on <paramref name="edge"/>. Items sharing a side are joined in a fixed
    /// order (computer name, user name, clock); an empty item is left out.
    /// </summary>
    public static (string Left, string Right) For(BannerSettings settings, BannerEdge edge, string clock, string userName, string computerName)
    {
        var left = new List<string>();
        var right = new List<string>();

        void Put(bool on, InfoPosition position, string text)
        {
            if (!on || string.IsNullOrEmpty(text) || !IsOn(position, edge, settings.Position)) return;
            (IsLeft(position) ? left : right).Add(text);
        }

        Put(settings.ShowComputerName, settings.ComputerNamePosition, computerName);
        Put(settings.ShowUserName, settings.UserNamePosition, userName);
        Put(settings.ClockEnabled, settings.ClockPosition, clock);
        return (string.Join(Separator, left), string.Join(Separator, right));
    }

    public static bool IsLeft(InfoPosition position) =>
        position is InfoPosition.Left or InfoPosition.TopLeft or InfoPosition.BottomLeft;

    /// <summary>Whether an item at <paramref name="position"/> is drawn on the banner at <paramref name="edge"/>.</summary>
    public static bool IsOn(InfoPosition position, BannerEdge edge, BannerPosition banners)
    {
        if (position is InfoPosition.Left or InfoPosition.Right) return true;
        var wanted = position is InfoPosition.TopLeft or InfoPosition.TopRight ? BannerEdge.Top : BannerEdge.Bottom;
        // A corner on a banner that isn't shown moves to the one that is, rather than disappearing.
        return banners != BannerPosition.Both || edge == wanted;
    }
}
