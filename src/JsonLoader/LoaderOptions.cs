namespace JsonLoader;

public sealed class LoaderOptions
{
    public const string SectionName = "LoaderOptions";
    public string DataSourcePath { get; set; } = "../../DataSources/Json";
}