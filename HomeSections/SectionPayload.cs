namespace Jellyfin.Plugin.MissingMediaChecker.HomeSections;

/// <summary>
/// Mirrors <c>Jellyfin.Plugin.HomeScreenSections.Model.Dto.HomeScreenSectionPayload</c>
/// so our handler methods can accept it without a compile-time reference to
/// the Home Screen Sections assembly. HSS deserialises its payload into
/// whatever type our handler's method signature declares.
/// </summary>
public sealed class SectionPayload
{
    public System.Guid UserId { get; set; }
    public string? AdditionalData { get; set; }
}
