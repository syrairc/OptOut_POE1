using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;

namespace OptOut;

public class OptOutSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Patch the crash reporter", "Keeps it patched so a crash writes nothing.")]
    public ToggleNode PatchReporter { get; set; } = new ToggleNode(true);

    [Menu("Unpatch now", "Puts the original bytes back and stops patching.")]
    [JsonIgnore]
    public ButtonNode Unpatch { get; set; } = new ButtonNode();

    [Menu("Game folder", "Filled in from the running client. Only change it if that came out wrong.")]
    public TextNode GameFolder { get; set; } = new TextNode("");

    [Menu("Block folder reads", "Also stops the game listing the folder to find old crash files.")]
    public ToggleNode BlockFolderReads { get; set; } = new ToggleNode(true);

    [Menu("Lock the crash folder", "Denies the game permission to write there. Stays until you unlock it.")]
    [JsonIgnore]
    public ButtonNode LockFolder { get; set; } = new ButtonNode();

    [Menu("Unlock the crash folder", "Hands the permission back.")]
    [JsonIgnore]
    public ButtonNode UnlockFolder { get; set; } = new ButtonNode();
}
