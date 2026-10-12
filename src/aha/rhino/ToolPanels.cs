using Rhino;
using Rhino.UI;

namespace Aha.Runtime;

internal static class ToolPanels
{
    // Rhino owns the registered panel's container, position and visibility.
    public static void Open(RhinoDoc doc, Type panelType) => Panels.OpenPanel(panelType.GUID, true);
}
