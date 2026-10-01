namespace UserGroupsDelta.GroupsDelta;

public sealed class GroupsDeltaOptions
{
    public const string SectionName = "GroupsDelta";

    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0/";

    public string Select { get; set; } = "id,displayName,description,mail,mailEnabled,securityEnabled,groupTypes,members";

    public int ResponseLimit { get; set; } = 100;

    public int MaxRetries { get; set; } = 3;

    public string ContainerName { get; set; } = "groups-delta";
}