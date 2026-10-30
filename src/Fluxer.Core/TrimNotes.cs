namespace Fluxer.Core;

/// The app is published trimmed with TrimMode=partial: only the framework and libraries that opt in are trimmed, while
/// Fluxer's own assemblies stay whole. Reflection over Fluxer types (the JSON serializer on settings and records) is
/// therefore safe; call sites say so with this justification.
public static class TrimNotes
{
    public const string OwnTypesKept = "Fluxer assemblies are published untrimmed (TrimMode=partial), so reflection sees every member of their types.";
}
