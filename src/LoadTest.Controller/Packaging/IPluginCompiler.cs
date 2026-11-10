using LoadTest.Plugins;

namespace LoadTest.Controller.Packaging;

/// <summary>
/// Compila un pacchetto con entry .csx in un pacchetto con entry .dll (CONTRATTO sez. 1: compilazione una sola volta sul controller).
/// L'implementazione sta in LoadTest.Plugins.Compilation (chat 5): serve l'adattatore, vedi G16 in PROPOSTE-CONTRATTO-chat6-parte3.md.
/// Il pacchetto restituito deve avere hash calcolato sul compilato (P7).
/// </summary>
public interface IPluginCompiler
{
    PluginPackage Compile(PluginPackage scriptPackage, string outputDirectory);
}
