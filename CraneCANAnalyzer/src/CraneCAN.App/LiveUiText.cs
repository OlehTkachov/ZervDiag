using System.Globalization;
using System.Resources;
using CraneCAN.Core.Live;

namespace CraneCAN.App;

internal static class LiveUiText
{
    private static readonly ResourceManager Resources = new("CraneCAN.App.Resources.LiveStrings", typeof(LiveUiText).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string Stage(LiveExperimentState state) => Get("Stage" + state);
}
