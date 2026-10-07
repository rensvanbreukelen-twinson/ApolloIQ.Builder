namespace Builder.Persistence.Export;

public enum HmiAddressMode
{
    Path,
    SymbolKey
}

public sealed record HmiExportProfile(Guid? ConnectionId = null, int ScanRateMs = HmiExportProfile.DefaultScanRateMs, HmiAddressMode Address = HmiAddressMode.Path)
{
    public const int DefaultScanRateMs = 1000;
    public const int MinScanRateMs = 50;
    public const int MaxScanRateMs = 3_600_000;

    public static HmiExportProfile Default { get; } = new();
}
