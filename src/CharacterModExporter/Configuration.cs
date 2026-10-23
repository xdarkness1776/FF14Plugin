using CharacterModExporter.Core;
using Dalamud.Configuration;

namespace CharacterModExporter;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public ExportSettings Export { get; set; } = new();

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
