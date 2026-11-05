using HubRadio.Core.Settings;
using HubRadio.Infrastructure.Database;

namespace HubRadio.App.DefaultSettings;

/// <summary>
/// FR-146 / PARAM-012: whether the template auto-selection (FR-145) is used.
/// The value lives in the settings snapshot (key <see cref="Key"/>, profile 1.3.0 or later), so a
/// Project or Job keeps the value it was created with. A snapshot of an older profile has no such
/// key and reads as the default (use).
/// </summary>
internal static class TemplateAutoSelectionSetting
{
    internal const string Key = "project.templateAutoSelectEnabled";

    /// <summary>PARAM-012: the initial value is "use".</summary>
    internal const bool Default = true;

    internal static bool IsEnabled(DefaultSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (DefaultSettingSelection selection in snapshot.Values)
        {
            if (string.Equals(selection.Key, Key, StringComparison.Ordinal))
            {
                return selection.Value.BooleanValue ??
                    throw new InvalidOperationException(
                        "The template auto-selection setting must be a boolean.");
            }
        }

        return Default;
    }

    internal static bool IsEnabled(StoredGlobalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return IsEnabled(settings.Snapshot);
    }
}
